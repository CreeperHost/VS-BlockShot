using System.Collections.Concurrent;
using System.Text;

namespace BlockShot.VintageStory.Core;

/// <summary>Writes small state files without exposing a partially written destination.</summary>
public static class AtomicTextFile
{
    private const int MaximumReplaceAttempts = 6;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The state-file path has no parent directory.", nameof(path));
        var writeGate = WriteGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    contents,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await ReplaceAsync(temporaryPath, fullPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static async Task ReplaceAsync(
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception error) when (
                attempt < MaximumReplaceAttempts &&
                error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay *= 2;
            }
        }
    }
}

