using System.Net;

namespace BlockShot.VintageStory.Core;

public static class BlockShotChatText
{
    public static string Uploaded(Uri shareUri, string? mediaName = null, bool copied = false)
    {
        ArgumentNullException.ThrowIfNull(shareUri);
        if (!shareUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The BlockShot share URL must be absolute.", nameof(shareUri));
        }

        var encodedUrl = WebUtility.HtmlEncode(shareUri.AbsoluteUri);
        var encodedMedia = string.IsNullOrWhiteSpace(mediaName)
            ? string.Empty
            : $" {WebUtility.HtmlEncode(mediaName.Trim())}";
        var copiedSuffix = copied ? " (copied)" : string.Empty;
        return $"BlockShot uploaded{encodedMedia}: <a href=\"{encodedUrl}\">{encodedUrl}</a>{copiedSuffix}";
    }
}
