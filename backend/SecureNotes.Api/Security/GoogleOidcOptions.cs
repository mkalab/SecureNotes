using System.Net;

namespace SecureNotes.Api.Security;

public static class GoogleOidcOptions
{
    public static bool TryGetRedirectUri(IConfiguration configuration, out Uri? redirectUri)
    {
        redirectUri = null;
        var value = configuration["Authentication:Google:RedirectUri"];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.UserInfo.Length != 0
            || candidate.Query.Length != 0
            || candidate.Fragment.Length != 0
            || candidate.AbsolutePath != "/signin-oidc"
            || !IsAllowedScheme(candidate))
        {
            return false;
        }

        redirectUri = candidate;
        return true;
    }

    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"])
        && !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"])
        && TryGetRedirectUri(configuration, out _)
        && TryGetFrontendOrigin(configuration, out _);

    public static bool TryGetFrontendOrigin(IConfiguration configuration, out Uri? frontendOrigin)
    {
        frontendOrigin = null;
        var value = configuration["Authentication:Google:FrontendOrigin"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.UserInfo.Length != 0
            || candidate.AbsolutePath != "/"
            || candidate.Query.Length != 0
            || candidate.Fragment.Length != 0
            || !IsAllowedScheme(candidate))
        {
            return false;
        }

        var allowedOrigins = configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];
        var origin = candidate.GetLeftPart(UriPartial.Authority);
        if (!allowedOrigins.Any(allowed => string.Equals(
            allowed.TrimEnd('/'),
            origin,
            StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        frontendOrigin = candidate;
        return true;
    }

    private static bool IsAllowedScheme(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}
