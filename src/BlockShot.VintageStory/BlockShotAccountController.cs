using BlockShot.VintageStory.Core;

namespace BlockShot.VintageStory;

internal enum BlockShotAccountState
{
    SignedOut,
    Pairing,
    SignedIn,
    Failed
}

internal sealed class BlockShotAccountController : IDisposable
{
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(30);
    private readonly MineTogetherPairingClient pairingClient;
    private readonly string tokenPath;
    private readonly Action<Uri> openLink;
    private readonly Action<string> logWarning;
    private CancellationTokenSource? pairingCancellation;
    private bool disposed;

    public BlockShotAccountController(
        MineTogetherPairingClient pairingClient,
        string tokenPath,
        Action<Uri> openLink,
        Action<string> logWarning)
    {
        this.pairingClient = pairingClient;
        this.tokenPath = Path.GetFullPath(tokenPath);
        this.openLink = openLink;
        this.logWarning = logWarning;
        ReloadSharedSession();
    }

    public event Action? Changed;

    public BlockShotAccountState State { get; private set; }

    public MineTogetherSessionToken? Session { get; private set; }

    public Uri? PairingUri { get; private set; }

    public string? Failure { get; private set; }

    public void ReloadSharedSession()
    {
        if (disposed) return;
        Session = null;
        PairingUri = null;
        Failure = null;
        try
        {
            if (File.Exists(tokenPath))
            {
                var candidate = MineTogetherSessionToken.ParseAndValidate(File.ReadAllText(tokenPath).Trim());
                if (!candidate.ExpiresWithin(RenewalMargin)) Session = candidate;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logWarning($"BlockShot could not reuse the shared MineTogether session: {error.Message}");
        }

        State = Session is null ? BlockShotAccountState.SignedOut : BlockShotAccountState.SignedIn;
        Changed?.Invoke();
    }

    public void LinkAccount()
    {
        if (disposed || State == BlockShotAccountState.Pairing) return;
        pairingCancellation?.Cancel();
        pairingCancellation?.Dispose();
        pairingCancellation = new CancellationTokenSource();
        var pairing = pairingClient.CreateRequest();
        PairingUri = pairing.VerificationUri;
        Failure = null;
        State = BlockShotAccountState.Pairing;
        Changed?.Invoke();
        openLink(pairing.VerificationUri);
        _ = CompletePairingAsync(pairing, pairingCancellation.Token);
    }

    public void OpenPairingLink()
    {
        if (PairingUri is not null) openLink(PairingUri);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pairingCancellation?.Cancel();
        pairingCancellation?.Dispose();
    }

    private async Task CompletePairingAsync(MineTogetherPairingRequest pairing, CancellationToken cancellationToken)
    {
        try
        {
            var session = await pairingClient.WaitForCompletionAsync(
                pairing,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            await AtomicTextFile.WriteAllTextAsync(tokenPath, session.Raw, cancellationToken).ConfigureAwait(false);
            if (disposed || cancellationToken.IsCancellationRequested) return;
            Session = session;
            PairingUri = null;
            Failure = null;
            State = BlockShotAccountState.SignedIn;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException)
        {
            if (disposed) return;
            Session = null;
            Failure = error.Message;
            State = BlockShotAccountState.Failed;
            logWarning($"BlockShot MineTogether pairing failed: {error.Message}");
            Changed?.Invoke();
        }
    }
}
