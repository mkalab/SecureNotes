using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SecureNotes.Api.Data;

namespace SecureNotes.Api.Security;

public sealed class AppSessionService(ISessionRepository sessions)
{
    public async Task SignInAsync(
        HttpContext context,
        Guid userId,
        string email,
        string role,
        CancellationToken cancellationToken)
    {
        var token = SessionToken.Create();
        var issuedAtUtc = DateTime.UtcNow;
        var expiresAtUtc = issuedAtUtc.AddHours(8);
        await sessions.CreateAsync(userId, SessionToken.Hash(token), expiresAtUtc, cancellationToken);

        try
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, role),
                new Claim("session_id", token)
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { ExpiresUtc = expiresAtUtc, IsPersistent = false });
        }
        catch
        {
            await sessions.RevokeAsync(SessionToken.Hash(token), CancellationToken.None);
            throw;
        }
    }
}
