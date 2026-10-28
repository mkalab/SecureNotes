using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;
using SecureNotes.Api.Security;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class AuthController(
    IUserRepository users,
    PasswordHasher passwordHasher,
    IAntiforgery antiforgery,
    ISessionRepository sessions,
    CryptoService cryptoService,
    AppSessionService appSessions) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult GetCsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new CsrfTokenResponse(tokens.RequestToken!));
    }

    [HttpPost("auth/register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var credentials = passwordHasher.Hash(request.Password);
        var userDataKey = cryptoService.CreateUserDataKey();
        try
        {
            var user = await users.CreateAsync(
                request.Email.Trim(),
                credentials.Hash,
                credentials.Salt,
                credentials.Iterations,
                cryptoService.ProtectUserDataKey(userDataKey),
                cancellationToken);
            await appSessions.SignInAsync(HttpContext, user.UserId, user.Email, user.Role, cancellationToken);
            return Created("/api/auth/me", new AccountResponse(user.UserId, user.Email));
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627 or 50002)
        {
            return Conflict(new ErrorResponse("An account with this email already exists."));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(userDataKey);
        }
    }

    [HttpPost("auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var attemptStartedAt = Stopwatch.GetTimestamp();
        var user = await users.GetByEmailAsync(request.Email.Trim(), cancellationToken);
        var validPassword = passwordHasher.Verify(
            request.Password,
            user?.PasswordHash,
            user?.PasswordSalt,
            user?.PasswordIterations);

        var isLocked = user?.LockedUntil is { } lockedUntil && lockedUntil > DateTime.UtcNow;
        if (!validPassword || user is null || user.IsDisabled || isLocked)
        {
            await users.RecordFailedLoginAsync(request.Email.Trim(), cancellationToken);
            var remainingDelay = TimeSpan.FromMilliseconds(500) - Stopwatch.GetElapsedTime(attemptStartedAt);
            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cancellationToken);
            }

            return Unauthorized(new ErrorResponse("Invalid email or password."));
        }

        await users.ResetFailedLoginAsync(user.UserId, cancellationToken);
        await appSessions.SignInAsync(HttpContext, user.UserId, user.Email, user.Role, cancellationToken);
        return Ok(new AccountResponse(user.UserId, user.Email));
    }

    [HttpGet("auth/me")]
    [AllowAnonymous]
    public IActionResult GetCurrentUser()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return NoContent();
        }

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Unauthorized();
        }

        return Ok(new AccountResponse(userId, User.FindFirstValue(ClaimTypes.Email)!));
    }

    [HttpPost("auth/logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var token = User.FindFirstValue("session_id");
        if (!string.IsNullOrEmpty(token))
        {
            await sessions.RevokeAsync(SessionToken.Hash(token), cancellationToken);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    public sealed record CsrfTokenResponse(string RequestToken);
    public sealed record AccountResponse(Guid UserId, string Email);
    public sealed record ErrorResponse(string Error);

    public sealed record RegisterRequest(
        [Required, EmailAddress, StringLength(320)] string Email,
        [Required, StringLength(128, MinimumLength = 12),
         RegularExpression("^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^A-Za-z0-9]).{12,128}$")]
        string Password);

    public sealed record LoginRequest(
        [Required, EmailAddress, StringLength(320)] string Email,
        [Required, StringLength(128, MinimumLength = 1)] string Password);
}
