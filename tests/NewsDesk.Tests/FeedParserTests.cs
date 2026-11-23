using System.Text;
using NewsDesk.Connectors.Feeds;

namespace NewsDesk.Tests;

public class FeedParserTests
{
    private static readonly Uri FeedUri = new("https://news.example.com/feed/");

    private static ParsedFeed Parse(string xml) => FeedParser.Parse(Encoding.UTF8.GetBytes(xml), FeedUri);

    [Fact]
    public void Rss2_resolves_relative_links_and_parses_rfc822_dates()
    {
        var feed = Parse("""
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel>
              <title>Example News</title><link>https://news.example.com/</link>
              <item><title>Arcade1Up announces &lt;b&gt;new&lt;/b&gt; cabinet</title><link>/2026/11/cabinet</link>
                <guid isPermaLink="false">post-1</guid><pubDate>Sun, 22 Nov 2026 09:30:00 EST</pubDate>
                <description>&lt;p&gt;Short &amp;amp; sweet&lt;/p&gt;</description>
                <content:encoded><![CDATA[<p>Full body</p><script>alert(1)</script>]]></content:encoded></item>
              <item><title>No date</title><link>https://news.example.com/x</link></item>
            </channel></rss>
            """);

        Assert.Equal("Example News", feed.Title);
        Assert.Equal(2, feed.Items.Count);
        var first = feed.Items[0];
        Assert.Equal("Arcade1Up announces new cabinet", first.Title);
        Assert.Equal("https://news.example.com/2026/11/cabinet", first.Link);
        Assert.Equal("post-1", first.PublisherId);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 14, 30, 0, TimeSpan.Zero), first.Published);
        Assert.Null(feed.Items[1].Published); // unknown dates stay unknown
    }

    [Fact]
    public void Atom_uses_alternate_link_xml_base_and_separate_dates()
    {
        var feed = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://blog.example.org/">
              <title>Advisories</title>
              <link rel="self" href="https://blog.example.org/atom.xml"/>
              <link rel="alternate" href="https://blog.example.org/"/>
              <entry><id>tag:example.org,2026:1</id><title type="html">CVE-2026-1234 patched</title>
                <link rel="alternate" type="text/html" href="posts/1"/>
                <published>2026-11-20T08:00:00Z</published><updated>2026-11-21T10:15:00+02:00</updated>
                <summary>Patch now.</summary></entry>
            </feed>
            """);

        var e = Assert.Single(feed.Items);
        Assert.Equal("https://blog.example.org/posts/1", e.Link);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero), e.Published);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 8, 15, 0, TimeSpan.Zero), e.Updated);
        Assert.Equal("https://blog.example.org/", feed.SiteUrl);
    }

    [Fact]
    public void Rdf_feeds_are_supported()
    {
        var feed = Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel rdf:about="https://x.example/"><title>RDF</title><link>https://x.example/</link></channel>
              <item rdf:about="https://x.example/a"><title>A</title><link>https://x.example/a</link><dc:date>2026-11-01T00:00:00Z</dc:date></item>
            </rdf:RDF>
            """);
        Assert.Equal("https://x.example/a", Assert.Single(feed.Items).Link);
    }

    [Fact]
    public void Doctype_and_external_entities_are_rejected()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <rss version="2.0"><channel><title>&xxe;</title></channel></rss>
            """;
        Assert.Throws<FeedFormatException>(() => Parse(xxe));
    }

    [Fact]
    public void Script_scheme_links_are_dropped()
    {
        var feed = Parse("""<rss version="2.0"><channel><item><title>x</title><link>javascript:alert(1)</link></item></channel></rss>""");
        Assert.Null(Assert.Single(feed.Items).Link);
    }

    [Theory]
    [InlineData("Mon, 02 Nov 2026 10:00:00 GMT", "2026-11-02T10:00:00Z")]
    [InlineData("Mon, 2 Nov 2026 10:00:00 +0100", "2026-11-02T09:00:00Z")]
    [InlineData("Tue, 02 Nov 2026 10:00:00 GMT", "2026-11-02T10:00:00Z")] // wrong weekday tolerated
    [InlineData("2026-11-02T10:00:00-05:00", "2026-11-02T15:00:00Z")]
    [InlineData("2026-11-02", "2026-11-02T00:00:00Z")]
    public void Dates_parse_to_utc(string input, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), FeedParser.ParseDate(input));

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("not a date at all")]
    public void Unparseable_dates_are_null(string input) => Assert.Null(FeedParser.ParseDate(input));
}
