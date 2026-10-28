using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using SecureNotes.Api.Data;
using SecureNotes.Api.Security;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class GoogleAuthController(
    IConfiguration configuration,
    IUserRepository users,
    PasswordHasher passwordHasher) : ControllerBase
{
    [HttpGet("google")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public IActionResult Login([FromQuery] string? returnUrl)
    {
        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        if (!GoogleOidcOptions.IsConfigured(configuration))
        {
            return Redirect(QueryHelpers.AddQueryString(safeReturnUrl, "authError", "google_unavailable"));
        }

        var properties = new AuthenticationProperties { RedirectUri = safeReturnUrl };
        properties.Items["returnUrl"] = safeReturnUrl;
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpPost("google/link")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Link([FromForm] GoogleLinkRequest request, CancellationToken cancellationToken)
    {
        if (!GoogleOidcOptions.IsConfigured(configuration))
        {
            return Redirect(QueryHelpers.AddQueryString("/", "authError", "google_unavailable"));
        }

        var attemptStartedAt = Stopwatch.GetTimestamp();
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = User.FindFirstValue(ClaimTypes.Email);
        var sessionId = User.FindFirstValue("session_id");
        var user = string.IsNullOrEmpty(email)
            ? null
            : await users.GetByEmailAsync(email, cancellationToken);
        var passwordValid = user is not null
            && Guid.TryParse(userIdValue, out var userId)
            && user.UserId == userId
            && !user.IsDisabled
            && (user.LockedUntil is null || user.LockedUntil <= DateTime.UtcNow)
            && passwordHasher.Verify(
                request.Password,
                user.PasswordHash,
                user.PasswordSalt,
                user.PasswordIterations);

        if (!passwordValid || user is null || string.IsNullOrEmpty(sessionId))
        {
            if (user is not null)
            {
                await users.RecordFailedLoginAsync(user.Email, cancellationToken);
            }

            var remainingDelay = TimeSpan.FromMilliseconds(500) - Stopwatch.GetElapsedTime(attemptStartedAt);
            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cancellationToken);
            }

            return Redirect(QueryHelpers.AddQueryString("/", "authError", "google_link_failed"));
        }

        await users.ResetFailedLoginAsync(user.UserId, cancellationToken);
        var properties = new AuthenticationProperties { RedirectUri = "/" };
        properties.Items["returnUrl"] = "/";
        properties.Items["linkUserId"] = user.UserId.ToString();
        properties.Items["linkSessionHash"] = WebEncoders.Base64UrlEncode(SessionToken.Hash(sessionId));
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }

    public sealed record GoogleLinkRequest(
        [Required, StringLength(128, MinimumLength = 1)] string Password);
}
