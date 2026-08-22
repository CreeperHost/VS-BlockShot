using System.Text.Json;

namespace BlockShot.VintageStory.Core;

/// <summary>The exact MineTogether pack identity used by the Vintage Story port.</summary>
public sealed record VintageStoryPackIdentity(string ExactGameVersion)
{
    public const string Platform = "VintageStory";

    public string CompatibilityKey { get; } = CreateCompatibilityKey(ExactGameVersion);

    public string IdentifierJson => JsonSerializer.Serialize(
        new Dictionary<string, string> { ["p"] = CompatibilityKey });

    public static string CreateCompatibilityKey(string exactGameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactGameVersion);
        return "vintagestory:" + exactGameVersion;
    }
}

