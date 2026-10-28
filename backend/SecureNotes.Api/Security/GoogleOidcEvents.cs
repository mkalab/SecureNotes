using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Security;

public sealed class GoogleOidcEvents(
    CryptoService cryptoService,
    AppSessionService appSessions,
    IConfiguration configuration,
    ILogger<GoogleOidcEvents> logger)
{
    public async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirst("sub")?.Value;
        var email = context.Principal?.FindFirst("email")?.Value;
        var emailVerified = context.Principal?.FindFirst("email_verified")?.Value;
        if (string.IsNullOrWhiteSpace(subject)
            || subject.Length > 255
            || !System.Net.Mail.MailAddress.TryCreate(email, out var address)
            || !string.Equals(address.Address, email, StringComparison.Ordinal)
            || !string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationFailureException("Google did not provide a verified account identity.");
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
        UserCredential? user;
        var linkUserIdValue = context.Properties?.Items.TryGetValue("linkUserId", out var linkUserIdItem) == true
            ? linkUserIdItem
            : null;
        if (linkUserIdValue is not null)
        {
            var linkSessionHash = context.Properties?.Items.TryGetValue("linkSessionHash", out var linkSessionHashItem) == true
                ? linkSessionHashItem
                : null;
            if (!Guid.TryParse(linkUserIdValue, out var linkUserId)
                || string.IsNullOrEmpty(linkSessionHash))
            {
                throw new AuthenticationFailureException("The account-link request is invalid.");
            }

            try
            {
                var sessions = context.HttpContext.RequestServices.GetRequiredService<ISessionRepository>();
                var sessionHash = WebEncoders.Base64UrlDecode(linkSessionHash);
                if (sessionHash.Length != 32)
                {
                    throw new AuthenticationFailureException("The account-link session is invalid.");
                }

                if (!await sessions.ValidateAndTouchAsync(
                    sessionHash,
                    DateTime.UtcNow,
                    20,
                    context.HttpContext.RequestAborted))
                {
                    throw new AuthenticationFailureException("The account-link session is no longer valid.");
                }

                if (await users.GetByGoogleSubjectAsync(subject, context.HttpContext.RequestAborted) is not null)
                {
                    throw new AuthenticationFailureException("This Google identity is already linked.");
                }

                user = await users.LinkGoogleLoginAsync(
                    linkUserId,
                    subject,
                    context.HttpContext.RequestAborted);
            }
            catch (SqlException exception) when (exception.Number is 2601 or 2627 or 50003 or 50004)
            {
                throw new AuthenticationFailureException("This Google identity cannot be linked.", exception);
            }
            catch (FormatException exception)
            {
                throw new AuthenticationFailureException("The account-link session is invalid.", exception);
            }
        }
        else
        {
            user = await users.GetByGoogleSubjectAsync(subject, context.HttpContext.RequestAborted);
            if (user is null)
            {
                if (await users.GetByEmailAsync(email!, context.HttpContext.RequestAborted) is not null)
                {
                    throw new AuthenticationFailureException(
                        "This Google account cannot be linked automatically to an existing account.");
                }

                var userDataKey = cryptoService.CreateUserDataKey();
                try
                {
                    user = await users.CreateGoogleUserAsync(
                        email!,
                        subject,
                        cryptoService.ProtectUserDataKey(userDataKey),
                        context.HttpContext.RequestAborted);
                }
                catch (SqlException exception) when (exception.Number is 2601 or 2627 or 50002 or 50003)
                {
                    user = await users.GetByGoogleSubjectAsync(subject, context.HttpContext.RequestAborted);
                    if (user is null)
                    {
                        throw new AuthenticationFailureException(
                            "This Google account cannot be linked automatically to an existing account.", exception);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(userDataKey);
                }
            }
        }

        if (user is null || user.IsDisabled)
        {
            throw new AuthenticationFailureException("This account is unavailable.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);
        context.Principal = new ClaimsPrincipal(identity);
    }

    public async Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        var principal = context.Principal ?? throw new AuthenticationFailureException("Google returned no identity.");
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || string.IsNullOrWhiteSpace(principal.FindFirstValue(ClaimTypes.Email))
            || string.IsNullOrWhiteSpace(principal.FindFirstValue(ClaimTypes.Role)))
        {
            throw new AuthenticationFailureException("Google returned an invalid application account.");
        }

        var email = principal.FindFirstValue(ClaimTypes.Email)!;
        var role = principal.FindFirstValue(ClaimTypes.Role)!;
        await appSessions.SignInAsync(
            context.HttpContext,
            userId,
            email,
            role,
            context.HttpContext.RequestAborted);

        var userAgent = context.HttpContext.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        var audit = context.HttpContext.RequestServices.GetRequiredService<IAuditRepository>();
        await audit.RecordAsync(
            userId,
            context.Properties?.Items.ContainsKey("linkUserId") == true
                ? "google_account_link"
                : "google_login",
            context.HttpContext.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            context.HttpContext.RequestAborted);

        var returnUrl = context.Properties?.Items.TryGetValue("returnUrl", out var value) == true
            && IsLocalReturnUrl(value)
                ? value!
                : "/";
        if (context.Properties?.Items.ContainsKey("linkUserId") == true)
        {
            returnUrl = QueryHelpers.AddQueryString(returnUrl, "authStatus", "google_linked");
        }

        context.Response.Redirect(GetFrontendRedirectUri(context.HttpContext.Request, returnUrl));
        context.HandleResponse();
    }

    public Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        logger.LogWarning("Google OIDC sign-in could not be completed.");
        var returnUrl = context.Properties?.Items.TryGetValue("returnUrl", out var value) == true
            && IsLocalReturnUrl(value)
                ? value!
                : "/";
        var redirectUri = GetFrontendRedirectUri(context.HttpContext.Request, returnUrl);
        context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, "authError", "google_failed"));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private string GetFrontendRedirectUri(HttpRequest request, string returnUrl)
    {
        if (!GoogleOidcOptions.TryGetFrontendOrigin(configuration, out var frontendOrigin))
        {
            throw new AuthenticationFailureException("The configured frontend origin is invalid.");
        }

        return frontendOrigin is null
            ? returnUrl
            : $"{frontendOrigin.GetLeftPart(UriPartial.Authority)}{returnUrl}";
    }

    private static bool IsLocalReturnUrl(string? value) =>
        !string.IsNullOrEmpty(value)
        && value[0] == '/'
        && !value.StartsWith("//", StringComparison.Ordinal)
        && !value.Contains('\\')
        && !value.Any(char.IsControl);
}
