using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NewsDesk.Core.Collection;
using NewsDesk.Core.Text;

namespace NewsDesk.Connectors.Feeds;

public sealed record ParsedFeed(string? Title, string? SiteUrl, IReadOnlyList<RawItem> Items);

public sealed class FeedFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parses RSS 2.0, RSS 1.0 (RDF), and Atom 1.0. DTDs and external entities are prohibited.</summary>
public static partial class FeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    public static ParsedFeed Parse(byte[] xml, Uri feedUri)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 50_000_000,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(xml);
            using var reader = XmlReader.Create(ms, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FeedFormatException($"Not a valid feed document: {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new FeedFormatException("Empty document.");
        return root.Name.LocalName switch
        {
            "rss" => ParseRss(root, feedUri),
            "feed" when root.Name.Namespace == Atom => ParseAtom(root, feedUri),
            "RDF" => ParseRdf(root, feedUri),
            _ => throw new FeedFormatException($"Unrecognized feed root element <{root.Name.LocalName}>."),
        };
    }

    private static ParsedFeed ParseRss(XElement root, Uri feedUri)
    {
        var channel = root.Element("channel") ?? throw new FeedFormatException("RSS feed has no <channel>.");
        var site = UrlNormalizer.Resolve(channel.Element("link")?.Value, feedUri);
        var items = channel.Elements("item").Select(i =>
        {
            var link = i.Element("link")?.Value;
            var guid = i.Element("guid");
            if (string.IsNullOrWhiteSpace(link) && guid is not null && !string.Equals((string?)guid.Attribute("isPermaLink"), "false", StringComparison.OrdinalIgnoreCase))
                link = guid.Value;
            return new RawItem
            {
                PublisherId = NullIfBlank(guid?.Value),
                Title = TitleText(i.Element("title")?.Value),
                Link = UrlNormalizer.Resolve(link, site ?? feedUri)?.ToString(),
                SummaryHtml = i.Element("description")?.Value,
                ContentHtml = i.Element(Content + "encoded")?.Value,
                Published = ParseDate(i.Element("pubDate")?.Value) ?? ParseDate(i.Element(Dc + "date")?.Value),
                Updated = null,
            };
        }).ToList();
        return new ParsedFeed(NullIfBlank(channel.Element("title")?.Value?.Trim()), site?.ToString(), items);
    }

    private static ParsedFeed ParseAtom(XElement root, Uri feedUri)
    {
        var baseUri = XmlBase(root, feedUri);
        var site = AtomLink(root, baseUri);
        var items = root.Elements(Atom + "entry").Select(e =>
        {
            var entryBase = XmlBase(e, baseUri);
            return new RawItem
            {
                PublisherId = NullIfBlank(e.Element(Atom + "id")?.Value),
                Title = TitleText(e.Element(Atom + "title")?.Value),
                Link = AtomLink(e, entryBase)?.ToString(),
                SummaryHtml = e.Element(Atom + "summary")?.Value,
                ContentHtml = e.Element(Atom + "content")?.Value,
                Published = ParseDate(e.Element(Atom + "published")?.Value),
                Updated = ParseDate(e.Element(Atom + "updated")?.Value),
            };
        }).ToList();
        return new ParsedFeed(NullIfBlank(root.Element(Atom + "title")?.Value?.Trim()), site?.ToString(), items);
    }

    private static ParsedFeed ParseRdf(XElement root, Uri feedUri)
    {
        var channel = root.Element(Rss1 + "channel");
        var site = UrlNormalizer.Resolve(channel?.Element(Rss1 + "link")?.Value, feedUri);
        var items = root.Elements(Rss1 + "item").Select(i => new RawItem
        {
            PublisherId = NullIfBlank((string?)i.Attribute(Rdf + "about")),
            Title = TitleText(i.Element(Rss1 + "title")?.Value),
            Link = UrlNormalizer.Resolve(i.Element(Rss1 + "link")?.Value, site ?? feedUri)?.ToString(),
            SummaryHtml = i.Element(Rss1 + "description")?.Value,
            ContentHtml = i.Element(Content + "encoded")?.Value,
            Published = ParseDate(i.Element(Dc + "date")?.Value),
        }).ToList();
        return new ParsedFeed(NullIfBlank(channel?.Element(Rss1 + "title")?.Value?.Trim()), site?.ToString(), items);
    }

    private static Uri XmlBase(XElement e, Uri fallback)
    {
        var b = (string?)e.Attribute(XNamespace.Xml + "base");
        return UrlNormalizer.Resolve(b, fallback) ?? fallback;
    }

    private static Uri? AtomLink(XElement e, Uri baseUri)
    {
        var links = e.Elements(Atom + "link").ToList();
        var chosen = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate"
                                               && ((string?)l.Attribute("type") is null or "text/html"))
                     ?? links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate");
        return UrlNormalizer.Resolve((string?)chosen?.Attribute("href"), baseUri);
    }

    private static string TitleText(string? raw)
    {
        var t = TextUtil.HtmlToText(raw).Replace('\n', ' ').Trim();
        return t.Length == 0 ? "(untitled)" : t;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"\s+(UT|GMT|Z)$")]
    private static partial Regex UtcSuffix();

    private static readonly Dictionary<string, string> ZoneAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EST"] = "-05:00", ["EDT"] = "-04:00", ["CST"] = "-06:00", ["CDT"] = "-05:00",
        ["MST"] = "-07:00", ["MDT"] = "-06:00", ["PST"] = "-08:00", ["PDT"] = "-07:00",
    };

    private static readonly string[] Rfc822Formats =
    [
        "ddd, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz", "d MMM yyyy HH:mm zzz",
        "ddd, d MMM yy HH:mm:ss zzz", "ddd, d MMMM yyyy HH:mm:ss zzz",
    ];

    /// <summary>Parses RFC 822/1123 and ISO 8601 dates. Unparseable or missing dates return null (never "now").</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso)
            && (s.Contains('T') || s.Contains('-')) && !s.Contains(','))
            return iso;

        s = UtcSuffix().Replace(s, " +00:00");
        var lastSpace = s.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            var zone = s[(lastSpace + 1)..];
            if (ZoneAbbreviations.TryGetValue(zone, out var offset)) s = s[..lastSpace] + " " + offset;
            else if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') && zone[1..].All(char.IsAsciiDigit))
                s = s[..lastSpace] + " " + zone[..3] + ":" + zone[3..];
        }

        if (DateTimeOffset.TryParseExact(s, Rfc822Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var rfc))
            return rfc.ToUniversalTime();
        // Some feeds use the wrong weekday name; retry without it.
        var comma = s.IndexOf(',');
        if (comma > 0 && DateTimeOffset.TryParseExact(s[(comma + 1)..].Trim(), Rfc822Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out rfc))
            return rfc.ToUniversalTime();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loose))
            return loose.ToUniversalTime();
        return null;
    }
}
