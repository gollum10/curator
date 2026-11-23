using System.Net;
using NewsDesk.Connectors.Http;
using NewsDesk.Core.Text;

namespace NewsDesk.Tests;

public class SafetyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    public void Non_public_addresses_are_blocked(string ip) => Assert.False(AddressGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(AddressGuard.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task Requests_to_localhost_are_refused()
    {
        using var http = new SafeHttp(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<BlockedAddressException>(() => http.GetAsync(new Uri("http://localhost:9/"), null, null, CancellationToken.None));
    }

    [Fact]
    public void Html_to_text_strips_scripts_and_markup()
    {
        var text = TextUtil.HtmlToText("<p>Hello <b>world</b></p><script>steal()</script><style>p{}</style><iframe src=x></iframe>&lt;tag&gt;");
        Assert.Equal("Hello world\n<tag>", text.Replace("\r", ""));
    }

    [Theory]
    [InlineData("https://Example.COM:443/a?utm_source=x&id=5#frag", "https://example.com/a?id=5")]
    [InlineData("https://example.com/a?fbclid=1", "https://example.com/a")]
    [InlineData("http://example.com/p?page=2&sort=new", "http://example.com/p?page=2&sort=new")]
    public void Canonical_urls_drop_tracking_but_keep_meaningful_parameters(string input, string expected) =>
        Assert.Equal(expected, UrlNormalizer.Canonicalize(new Uri(input)));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("data:text/html,hi")]
    public void Unsafe_link_schemes_resolve_to_null(string link) =>
        Assert.Null(UrlNormalizer.Resolve(link, new Uri("https://example.com/")));
}
