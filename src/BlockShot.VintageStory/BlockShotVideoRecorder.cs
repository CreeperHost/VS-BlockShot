using System.Diagnostics;
using System.Threading.Channels;
using BlockShot.VintageStory.Core;
using SIPSorceryMedia.Abstractions;
using Vintagestory.API.Client;
using Vpx.Net;

namespace BlockShot.VintageStory;

/// <summary>
/// Queues GPU scaling and asynchronous pixel-buffer transfers from the render thread,
/// then hands only completed frames to a bounded, lower-priority encoder worker.
/// </summary>
internal sealed class BlockShotVideoRecorder : IRenderer
{
    public const int FramesPerSecond = VideoCaptureCadence.DefaultFramesPerSecond;
    public const int MaximumSeconds = 30;
    public const int MaximumWidth = 1280;
    public const int MaximumHeight = 720;

    // Observed 720p conversion + VP8 work takes roughly 31-42ms. A 75% ceiling still leaves
    // one quarter of the background worker idle while allowing that work to sustain the
    // 66.7ms cadence required by 15fps. BelowNormal priority additionally yields to the game.
    internal const double MaximumEncoderDutyCycle = 0.75;

    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(MaximumSeconds);

    private readonly ICoreClientAPI api;
    private readonly object sync = new();
    private RecordingSession? active;
    private bool disposed;

    public BlockShotVideoRecorder(ICoreClientAPI api)
    {
        this.api = api;
        api.Event.RegisterRenderer(this, EnumRenderStage.Done, "blockshot-video-capture");
    }

    public event Action? Changed;

    public double RenderOrder => 2.2;

    public int RenderRange => 0;

    public bool Active
    {
        get
        {
            lock (sync) return active is not null;
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (sync) return active?.AcceptingFrames == true;
        }
    }

    public bool IsFinalizing
    {
        get
        {
            lock (sync) return active is { AcceptingFrames: false, Cancelled: false };
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (sync) return active is null ? TimeSpan.Zero : active.Elapsed;
        }
    }

    public int DroppedFrames
    {
        get
        {
            lock (sync) return active?.DroppedFrames ?? 0;
        }
    }

    public bool Start(string path, Action<string?, Exception?> completed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(completed);

        RecordingSession session;
        lock (sync)
        {
            if (disposed || active is not null) return false;
            var layout = VideoFrameLayout.FitInside(
                api.Render.FrameWidth,
                api.Render.FrameHeight,
                MaximumWidth,
                MaximumHeight);
            session = new RecordingSession(Path.GetFullPath(path), layout, completed);
            active = session;
        }

        _ = Task.Factory.StartNew(
            () => Encode(session),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Changed?.Invoke();
        return true;
    }

    public bool Stop()
    {
        lock (sync)
        {
            if (active is null) return false;
            if (!active.AcceptingFrames) return true;
            StopLocked(active);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Cancel()
    {
        RecordingSession? session;
        lock (sync)
        {
            session = active;
            if (session is null) return false;
            session.Cancelled = true;
            session.AcceptingFrames = false;
            session.Cancellation.Cancel();
            ReleaseReadback(session);
            session.Frames.Writer.TryComplete();
        }
        Changed?.Invoke();
        return true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        RecordingSession? session;
        lock (sync)
        {
            session = active;
            if (session is null || !session.AcceptingFrames) return;
        }

        try
        {
            DrainCompletedReadbacks(session);

            TimeSpan timestamp;
            var shouldCapture = false;
            var automaticallyStopped = false;
            lock (sync)
            {
                if (!ReferenceEquals(active, session) || !session.AcceptingFrames) return;
                timestamp = session.Elapsed;
                if (timestamp >= MaximumDuration)
                {
                    StopLocked(session);
                    automaticallyStopped = true;
                }
                else if (session.Cadence.ShouldCapture(timestamp))
                {
                    shouldCapture = true;
                }
            }

            if (automaticallyStopped)
            {
                Changed?.Invoke();
                return;
            }

            if (shouldCapture)
            {
                session.Readback ??= new AsyncGpuVideoCapture(session.Layout);
                if (!session.Readback.TryIssue(api.Render.FrameWidth, api.Render.FrameHeight, timestamp))
                {
                    Interlocked.Increment(ref session.DroppedFrameCount);
                }
            }

            NotifyElapsedSecond(session, timestamp);
        }
        catch (Exception error)
        {
            Fail(session, error);
        }
    }

    public void Dispose()
    {
        RecordingSession? session;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            session = active;
            if (session is not null)
            {
                session.Cancelled = true;
                session.AcceptingFrames = false;
                session.Cancellation.Cancel();
                ReleaseReadback(session);
                session.Frames.Writer.TryComplete();
            }
        }
        api.Event.UnregisterRenderer(this, EnumRenderStage.Done);
    }

    private void Encode(RecordingSession session)
    {
        var encoderThread = Thread.CurrentThread;
        var previousPriority = TryLowerPriority(encoderThread);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(session.Path)!);
            using var stream = new FileStream(
                session.Path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            using var webm = new WebMVideoWriter(
                stream,
                session.Layout.CanvasWidth,
                session.Layout.CanvasHeight,
                FramesPerSecond,
                leaveOpen: true);
            using var encoder = new VP8Codec
            {
                BaseQIndex = 32,
                KeyframeIntervalFrames = FramesPerSecond * 2,
                EnableIntraFallback = true
            };

            while (session.Frames.Reader.WaitToReadAsync(session.Cancellation.Token).AsTask().GetAwaiter().GetResult())
            {
                while (session.Frames.Reader.TryRead(out var frame))
                {
                    var work = Stopwatch.StartNew();
                    try
                    {
                        BgraToI420Converter.Convert(
                            frame.Pixels,
                            session.I420Pixels,
                            session.Layout.CanvasWidth,
                            session.Layout.CanvasHeight);
                        var encoded = encoder.EncodeVideo(
                            session.Layout.CanvasWidth,
                            session.Layout.CanvasHeight,
                            session.I420Pixels,
                            VideoPixelFormatsEnum.I420,
                            VideoCodecsEnum.VP8);
                        if (encoded is null || encoded.Length == 0)
                        {
                            throw new InvalidDataException("The VP8 encoder returned an empty frame.");
                        }
                        var keyFrame = (encoded[0] & 0x01) == 0;
                        webm.WriteFrame(encoded, frame.Timestamp, keyFrame);
                    }
                    finally
                    {
                        work.Stop();
                        AsyncGpuVideoCapture.Return(frame);
                    }

                    if (session.AcceptingFrames)
                    {
                        var rest = EncoderWorkBudget.RestAfter(
                            work.Elapsed,
                            FramesPerSecond,
                            MaximumEncoderDutyCycle);
                        RestEncoder(session, rest);
                    }
                }
            }

            session.Cancellation.Token.ThrowIfCancellationRequested();
            webm.Complete(session.StopDuration ?? session.Elapsed);
            stream.Flush();
            CompleteOnMainThread(session, session.Path, null);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            DeletePartialFile(session.Path);
            CompleteOnMainThread(session, null, null);
        }
        catch (Exception error)
        {
            DeletePartialFile(session.Path);
            CompleteOnMainThread(session, null, error);
        }
        finally
        {
            while (session.Frames.Reader.TryRead(out var frame)) AsyncGpuVideoCapture.Return(frame);
            if (previousPriority is { } priority)
            {
                try
                {
                    encoderThread.Priority = priority;
                }
                catch
                {
                }
            }
        }
    }

    private void StopLocked(RecordingSession session)
    {
        session.StopDuration = TimeSpan.FromTicks(Math.Min(session.Elapsed.Ticks, MaximumDuration.Ticks));
        session.AcceptingFrames = false;
        ReleaseReadback(session);
        session.Frames.Writer.TryComplete();
    }

    private void Fail(RecordingSession session, Exception error)
    {
        lock (sync)
        {
            if (!ReferenceEquals(active, session)) return;
            session.AcceptingFrames = false;
            ReleaseReadback(session);
            session.Frames.Writer.TryComplete(error);
        }
        Changed?.Invoke();
    }

    private void NotifyElapsedSecond(RecordingSession session, TimeSpan elapsed)
    {
        var second = Math.Min(MaximumSeconds, (int)elapsed.TotalSeconds);
        if (Interlocked.Exchange(ref session.LastNotifiedSecond, second) != second) Changed?.Invoke();
    }

    private void CompleteOnMainThread(RecordingSession session, string? path, Exception? error) =>
        api.Event.EnqueueMainThreadTask(() =>
        {
            lock (sync)
            {
                if (ReferenceEquals(active, session))
                {
                    session.AcceptingFrames = false;
                    ReleaseReadback(session);
                    session.Frames.Writer.TryComplete();
                    while (session.Frames.Reader.TryRead(out var frame)) AsyncGpuVideoCapture.Return(frame);
                    active = null;
                }
            }
            if (!session.Cancelled) session.Completed(path, error);
            Changed?.Invoke();
            session.Cancellation.Dispose();
        }, "blockshot-video-complete");

    private static ThreadPriority? TryLowerPriority(Thread thread)
    {
        try
        {
            var previous = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            return previous;
        }
        catch
        {
            return null;
        }
    }

    private static void RestEncoder(RecordingSession session, TimeSpan duration)
    {
        // Check the recording state in short slices so Stop never has to wait through a long
        // duty-cycle rest after an unusually expensive frame.
        while (session.AcceptingFrames && duration > TimeSpan.Zero)
        {
            var slice = duration > TimeSpan.FromMilliseconds(25)
                ? TimeSpan.FromMilliseconds(25)
                : duration;
            if (session.Cancellation.Token.WaitHandle.WaitOne(slice))
            {
                session.Cancellation.Token.ThrowIfCancellationRequested();
            }
            duration -= slice;
        }
    }

    private static void DrainCompletedReadbacks(RecordingSession session)
    {
        var readback = session.Readback;
        if (readback is null) return;

        if (!readback.TryTakeCompleted(copyPixels: true, out var frame)) return;
        if (frame is null)
        {
            Interlocked.Increment(ref session.DroppedFrameCount);
            return;
        }
        if (session.Frames.Writer.TryWrite(frame)) return;

        AsyncGpuVideoCapture.Return(frame);
        Interlocked.Increment(ref session.DroppedFrameCount);
    }

    private static void ReleaseReadback(RecordingSession session)
    {
        session.Readback?.Dispose();
        session.Readback = null;
    }

    private static void DeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class RecordingSession
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();

        public RecordingSession(string path, VideoFrameLayout layout, Action<string?, Exception?> completed)
        {
            Path = path;
            Layout = layout;
            Completed = completed;
            Frames = Channel.CreateBounded<PooledVideoFrame>(new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            I420Pixels = new byte[BgraToI420Converter.RequiredByteCount(layout.CanvasWidth, layout.CanvasHeight)];
            Cadence = new VideoCaptureCadence(FramesPerSecond);
        }

        public string Path { get; }
        public VideoFrameLayout Layout { get; }
        public Action<string?, Exception?> Completed { get; }
        public Channel<PooledVideoFrame> Frames { get; }
        public byte[] I420Pixels { get; }
        public VideoCaptureCadence Cadence { get; }
        public AsyncGpuVideoCapture? Readback { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public volatile bool AcceptingFrames = true;
        public volatile bool Cancelled;
        public TimeSpan? StopDuration;
        public int DroppedFrameCount;
        public int LastNotifiedSecond = -1;
        public TimeSpan Elapsed => stopwatch.Elapsed;
        public int DroppedFrames => Volatile.Read(ref DroppedFrameCount);
    }

}
