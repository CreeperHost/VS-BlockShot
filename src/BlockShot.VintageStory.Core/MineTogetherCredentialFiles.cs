namespace BlockShot.VintageStory.Core;

public sealed record MineTogetherRenewalResult(
    MineTogetherSessionToken Session,
    bool Renewed);

/// <summary>
/// Coordinates the access and renewal files shared by every Vintage Story MineTogether mod.
/// The exclusive lock is intentionally a file lock so independently loaded mod assemblies cannot race.
/// </summary>
public sealed class MineTogetherCredentialFiles
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly string tokenPath;
    private readonly string refreshPath;
    private readonly string lockPath;
    private readonly string authorityPublicKeyPem;

    public MineTogetherCredentialFiles(
        string tokenPath,
        string authorityPublicKeyPem = MineTogetherSessionAuthority.PublicKeyPem)
    {
        this.tokenPath = Path.GetFullPath(tokenPath);
        this.authorityPublicKeyPem = authorityPublicKeyPem;
        var directory = Path.GetDirectoryName(this.tokenPath)
            ?? throw new ArgumentException("The session path has no directory.", nameof(tokenPath));
        refreshPath = Path.Combine(directory, "session.refresh");
        lockPath = Path.Combine(directory, "session.refresh.lock");
    }

    public MineTogetherSessionToken? TryReadSession(string playerUid)
    {
        if (!File.Exists(tokenPath)) return null;
        return MineTogetherSessionToken.ParseAndValidate(
            File.ReadAllText(tokenPath).Trim(),
            authorityPublicKeyPem,
            expectedVintageStoryPlayerUid: playerUid);
    }

    public bool HasRenewalCredential => File.Exists(refreshPath);

    public async Task SaveAsync(
        MineTogetherSessionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        await SaveWhileLockedAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MineTogetherRenewalResult?> RenewIfNeededAsync(
        MineTogetherPairingClient client,
        string playerUid,
        TimeSpan renewalMargin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var normalizedUid = VintageStorySessionIdentity.NormalizePlayerUid(playerUid);
        await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);

        var current = TryReadSessionIgnoringExpiryFailure(normalizedUid);
        if (current is not null &&
            !current.ExpiresWithin(renewalMargin) &&
            !current.HasEmbeddedAccountIdentity)
        {
            return new MineTogetherRenewalResult(current, false);
        }
        if (!File.Exists(refreshPath))
        {
            return current is null ? null : new MineTogetherRenewalResult(current, false);
        }

        var refreshToken = (await File.ReadAllTextAsync(refreshPath, cancellationToken).ConfigureAwait(false)).Trim();
        if (refreshToken.Length == 0)
        {
            return current is null ? null : new MineTogetherRenewalResult(current, false);
        }
        var credentials = await client.RenewAsync(refreshToken, normalizedUid, cancellationToken)
            .ConfigureAwait(false);
        await SaveWhileLockedAsync(credentials, cancellationToken).ConfigureAwait(false);
        return new MineTogetherRenewalResult(credentials.Session, true);
    }

    private MineTogetherSessionToken? TryReadSessionIgnoringExpiryFailure(string playerUid)
    {
        try
        {
            return TryReadSession(playerUid);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private async Task SaveWhileLockedAsync(
        MineTogetherSessionCredentials credentials,
        CancellationToken cancellationToken)
    {
        // Persist the renewal credential first. If the second write is interrupted, the next
        // caller can still use it to recover a fresh access JWT.
        await AtomicTextFile.WriteAllTextAsync(refreshPath, credentials.RefreshToken, cancellationToken)
            .ConfigureAwait(false);
        await AtomicTextFile.WriteAllTextAsync(tokenPath, credentials.Session.Raw, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTimeOffset.UtcNow + LockWait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
