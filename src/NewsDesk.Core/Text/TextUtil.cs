using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NewsDesk.Core.Text;

public static partial class TextUtil
{
    [GeneratedRegex(@"<(script|style|noscript|iframe|object|embed|template)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DangerousBlocks();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|/blockquote)\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpaceAroundNewline();

    [GeneratedRegex(@"\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLines();

    /// <summary>Converts untrusted HTML to plain text. The result contains no markup and is safe to display as text.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Length > 2_000_000 ? html[..2_000_000] : html;
        s = DangerousBlocks().Replace(s, " ");
        s = BlockBreaks().Replace(s, "\n");
        s = Tags().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = HorizontalSpace().Replace(s.Replace("\r", ""), " ");
        s = SpaceAroundNewline().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    public static string Excerpt(string text, int maxChars = 300)
    {
        var flat = HorizontalSpace().Replace(text.Replace('\n', ' '), " ").Trim();
        if (flat.Length <= maxChars) return flat;
        var cut = flat.LastIndexOf(' ', maxChars);
        return (cut > maxChars / 2 ? flat[..cut] : flat[..maxChars]) + "…";
    }

    public static string ContentHash(string title, string text)
    {
        var normalized = HorizontalSpace().Replace((title + "\n" + text).Replace("\r", ""), " ").Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
