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
    private static readonly TimeSpan RenewalCheckInterval = TimeSpan.FromMinutes(1);
    private readonly MineTogetherPairingClient pairingClient;
    private readonly MineTogetherCredentialFiles credentialFiles;
    private readonly Func<string?> playerUid;
    private readonly Action<Uri> openLink;
    private readonly Action<string> logWarning;
    private CancellationTokenSource? pairingCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim renewalWake = new(0, 1);
    private readonly Task renewalOperation;
    private bool disposed;

    public BlockShotAccountController(
        MineTogetherPairingClient pairingClient,
        string tokenPath,
        Func<string?> playerUid,
        Action<Uri> openLink,
        Action<string> logWarning)
    {
        this.pairingClient = pairingClient;
        credentialFiles = new MineTogetherCredentialFiles(tokenPath);
        this.playerUid = playerUid;
        this.openLink = openLink;
        this.logWarning = logWarning;
        ReloadSharedSession();
        renewalOperation = Task.Run(() => RunRenewalLoopAsync(lifetime.Token));
    }

    public event Action? Changed;

    public BlockShotAccountState State { get; private set; }

    public MineTogetherSessionToken? Session { get; private set; }

    public string PlayerUid => CurrentPlayerUid();

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
            Session = credentialFiles.TryReadSession(CurrentPlayerUid());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logWarning($"BlockShot could not reuse the shared MineTogether session: {error.Message}");
        }

        State = Session is null ? BlockShotAccountState.SignedOut : BlockShotAccountState.SignedIn;
        Changed?.Invoke();
        if (Session is null || Session.ExpiresWithin(RenewalMargin) || Session.HasEmbeddedAccountIdentity)
        {
            try
            {
                renewalWake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public void LinkAccount()
    {
        if (disposed || State == BlockShotAccountState.Pairing) return;
        pairingCancellation?.Cancel();
        pairingCancellation?.Dispose();
        pairingCancellation = new CancellationTokenSource();
        try
        {
            var pairing = pairingClient.CreateRequest(CurrentPlayerUid());
            PairingUri = null;
            Failure = null;
            State = BlockShotAccountState.Pairing;
            Changed?.Invoke();
            _ = CompletePairingAsync(pairing, pairingCancellation.Token);
        }
        catch (InvalidDataException error)
        {
            Failure = error.Message;
            State = BlockShotAccountState.Failed;
            Changed?.Invoke();
        }
    }

    public void OpenPairingLink()
    {
        if (PairingUri is not null) openLink(PairingUri);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        pairingCancellation?.Cancel();
        pairingCancellation?.Dispose();
        try
        {
            renewalOperation.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        renewalWake.Dispose();
        lifetime.Dispose();
    }

    private async Task CompletePairingAsync(MineTogetherPairingRequest pairing, CancellationToken cancellationToken)
    {
        try
        {
            await pairingClient.RegisterAsync(pairing, cancellationToken).ConfigureAwait(false);
            if (disposed || cancellationToken.IsCancellationRequested) return;
            PairingUri = pairing.VerificationUri;
            Changed?.Invoke();
            openLink(pairing.VerificationUri);

            var credentials = await pairingClient.WaitForCompletionAsync(
                pairing,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            await credentialFiles.SaveAsync(credentials, cancellationToken).ConfigureAwait(false);
            if (disposed || cancellationToken.IsCancellationRequested) return;
            Session = credentials.Session;
            PairingUri = null;
            Failure = null;
            State = BlockShotAccountState.SignedIn;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (disposed) return;
            Session = null;
            Failure = error.Message;
            State = BlockShotAccountState.Failed;
            logWarning($"BlockShot MineTogether pairing failed: {error.Message}");
            Changed?.Invoke();
        }
    }

    private async Task RunRenewalLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (renewalWake.Wait(0))
                {
                }
                await EnsureFreshSessionAsync(cancellationToken).ConfigureAwait(false);
                await renewalWake.WaitAsync(RenewalCheckInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureFreshSessionAsync(CancellationToken cancellationToken)
    {
        if (disposed || State == BlockShotAccountState.Pairing) return;
        try
        {
            if (disposed || State == BlockShotAccountState.Pairing) return;
            var result = await credentialFiles.RenewIfNeededAsync(
                pairingClient,
                CurrentPlayerUid(),
                RenewalMargin,
                cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                if (Session is not null && Session.ExpiresWithin(TimeSpan.Zero) && !disposed)
                {
                    Session = null;
                    State = BlockShotAccountState.SignedOut;
                    Failure = "MineTogether needs to be linked again.";
                    Changed?.Invoke();
                }
                return;
            }
            if (disposed || cancellationToken.IsCancellationRequested ||
                State == BlockShotAccountState.Pairing) return;
            if (Session is not null && string.Equals(Session.Raw, result.Session.Raw, StringComparison.Ordinal)) return;

            Session = result.Session;
            PairingUri = null;
            Failure = null;
            State = BlockShotAccountState.SignedIn;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (disposed) return;
            logWarning($"BlockShot could not renew the shared MineTogether session: {error.Message}");
            if (Session is null || Session.ExpiresWithin(TimeSpan.Zero))
            {
                Session = null;
                State = BlockShotAccountState.SignedOut;
                Failure = "MineTogether session renewal failed; link the account again.";
                Changed?.Invoke();
            }
        }
    }

    private string CurrentPlayerUid()
    {
        var value = playerUid()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Vintage Story has not exposed the current PlayerUID yet.");
        }

        return value;
    }
}
