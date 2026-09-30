using BabyMonitarr.Backend.Talkback;

namespace BabyMonitarr.Backend.Tests.Talkback;

public class GoogleHomeCookiesTests
{
    [Fact]
    public void Merge_replaces_rotated_cookie_in_place()
    {
        string merged = GoogleHomeCookies.Merge(
            "SID=a; NID=old; __Secure-3PSIDCC=x",
            new[] { "NID=new; expires=Fri, 01-Jan-2100 00:00:00 GMT; path=/; domain=.google.com; HttpOnly" });

        Assert.Equal("SID=a; NID=new; __Secure-3PSIDCC=x", merged);
    }

    [Fact]
    public void Merge_appends_new_cookie()
    {
        string merged = GoogleHomeCookies.Merge("SID=a", new[] { "__Secure-3PSIDCC=y; Path=/; Secure" });

        Assert.Equal("SID=a; __Secure-3PSIDCC=y", merged);
    }

    [Theory]
    [InlineData("NID=; Max-Age=0; Path=/")]
    [InlineData("NID=gone; Expires=Thu, 01 Jan 1970 00:00:00 GMT")]
    public void Merge_removes_cookie_the_server_expires(string setCookie)
    {
        string merged = GoogleHomeCookies.Merge("SID=a; NID=old", new[] { setCookie });

        Assert.Equal("SID=a", merged);
    }

    [Fact]
    public void Merge_keeps_values_containing_equals_signs()
    {
        string merged = GoogleHomeCookies.Merge("SID=a==; X=1", new[] { "X=b=c; Path=/" });

        Assert.Equal("SID=a==; X=b=c", merged);
    }

    [Fact]
    public void Names_never_exposes_values()
    {
        var names = GoogleHomeCookies.Names(new[] { "NID=secret; Path=/", "__Secure-3PSIDCC=secret2" });

        Assert.Equal(new[] { "NID", "__Secure-3PSIDCC" }, names);
    }
}

public class GoogleHomeCredentialInputTests
{
    private const string Url = "https://accounts.google.com/o/oauth2/iframerpc?action=issueToken&response_type=token";

    [Fact]
    public void Accepts_capture_and_strips_cookie_header_name()
    {
        bool ok = GoogleHomeCredentialInput.TryNormalize($"  {Url}\n", "Cookie: SID=abc; HSID=def ", out var url, out var cookie, out _);

        Assert.True(ok);
        Assert.Equal(Url, url);
        Assert.Equal("SID=abc; HSID=def", cookie);
    }

    [Theory]
    [InlineData("https://home.nest.com/", "SID=abc")]
    [InlineData("https://accounts.google.com/o/oauth2/iframerpc?action=checkOrigin", "SID=abc")]
    [InlineData(Url, "HSID=def")]
    [InlineData(null, null)]
    public void Rejects_incomplete_capture(string? url, string? cookie)
    {
        Assert.False(GoogleHomeCredentialInput.TryNormalize(url, cookie, out _, out _, out var error));
        Assert.NotEmpty(error);
    }
}
