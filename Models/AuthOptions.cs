namespace BabyMonitarr.Backend.Models;

public class AuthOptions
{
    public string Method { get; set; } = "Local";

    public ProxyHeaderOptions Proxy { get; set; } = new();
    public OidcOptions Oidc { get; set; } = new();
}

public class ProxyHeaderOptions
{
    public string UserHeader { get; set; } = "Remote-User";
    public string NameHeader { get; set; } = "Remote-Name";
    public string EmailHeader { get; set; } = "Remote-Email";
}

public class OidcOptions
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scopes { get; set; } = "openid profile email";
}
