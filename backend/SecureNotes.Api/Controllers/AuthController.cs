using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;
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
    AppSessionService appSessions,
    ITwoFactorService twoFactor,
    IAuditRepository audit) : ControllerBase
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
        if (request.AdditionalFields is { Count: > 0 })
        {
            return BadRequest(new ErrorResponse("Registration contains unsupported fields."));
        }

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
            await appSessions.SignInAsync(HttpContext, user.UserId, user.Email, user.Role, false, cancellationToken);
            await RecordAuditAsync(user.UserId, "registration_success", cancellationToken);
            return Created("/api/auth/me", new AccountResponse(user.UserId, user.Email, user.Role));
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
            await RecordAuditAsync(user?.UserId, "login_failed", cancellationToken);
            if (user is not null && !isLocked)
            {
                var updatedUser = await users.GetByEmailAsync(request.Email.Trim(), cancellationToken);
                if (updatedUser?.LockedUntil is { } updatedLockedUntil && updatedLockedUntil > DateTime.UtcNow)
                {
                    await RecordAuditAsync(user.UserId, "account_locked", cancellationToken);
                }
            }

            var remainingDelay = TimeSpan.FromMilliseconds(500) - Stopwatch.GetElapsedTime(attemptStartedAt);
            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cancellationToken);
            }

            return Unauthorized(new ErrorResponse("Invalid email or password."));
        }

        await users.ResetFailedLoginAsync(user.UserId, cancellationToken);
        if (await twoFactor.IsEnabledAsync(user.UserId, cancellationToken))
        {
            if (!await twoFactor.CreateChallengeAsync(user.UserId, "password", HttpContext, cancellationToken))
            {
                throw new InvalidOperationException("The two-factor login challenge could not be created.");
            }

            await RecordAuditAsync(user.UserId, "login_2fa_required", cancellationToken);
            return Accepted(new TwoFactorRequiredResponse(true));
        }

        await appSessions.SignInAsync(HttpContext, user.UserId, user.Email, user.Role, false, cancellationToken);
        await RecordAuditAsync(user.UserId, "login_success", cancellationToken);
        return Ok(new AccountResponse(user.UserId, user.Email, user.Role));
    }

    [HttpPost("auth/2fa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-2fa")]
    public async Task<IActionResult> VerifyTwoFactor(
        TwoFactorVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await twoFactor.CompleteChallengeAsync(request.Code, HttpContext, cancellationToken);
        return result is null
            ? Unauthorized(new ErrorResponse("Mã xác minh không hợp lệ hoặc đã hết hạn."))
            : Ok(new AccountResponse(result.UserId, result.Email, result.Role));
    }

    [HttpGet("auth/2fa")]
    [Authorize]
    public async Task<IActionResult> GetTwoFactorStatus(CancellationToken cancellationToken)
    {
        return Ok(new { enabled = await twoFactor.IsEnabledAsync(CurrentUserId, cancellationToken) });
    }

    [HttpPost("auth/2fa/enroll")]
    [Authorize]
    [EnableRateLimiting("auth-2fa")]
    public async Task<IActionResult> EnrollTwoFactor(CancellationToken cancellationToken)
    {
        try
        {
            var user = await users.GetByEmailAsync(User.FindFirstValue(ClaimTypes.Email)!, cancellationToken)
                ?? throw new InvalidOperationException("The authenticated account no longer exists.");
            return Ok(await twoFactor.BeginEnrollmentAsync(CurrentUserId, user.Email, cancellationToken));
        }
        catch (TwoFactorEnrollmentException exception)
        {
            return Conflict(new ErrorResponse(exception.Message));
        }
    }

    [HttpPost("auth/2fa/enable")]
    [Authorize]
    [EnableRateLimiting("auth-2fa")]
    public async Task<IActionResult> EnableTwoFactor(
        TwoFactorCodeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { recoveryCodes = await twoFactor.EnableAsync(CurrentUserId, request.Code, HttpContext, cancellationToken) });
        }
        catch (TwoFactorEnrollmentException exception)
        {
            return BadRequest(new ErrorResponse(exception.Message));
        }
    }

    [HttpPost("auth/2fa/disable")]
    [Authorize]
    [EnableRateLimiting("auth-2fa")]
    public async Task<IActionResult> DisableTwoFactor(
        TwoFactorCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (!await twoFactor.DisableAsync(CurrentUserId, request.Code, HttpContext, cancellationToken))
        {
            return Conflict(new ErrorResponse("Không thể tắt xác minh hai bước. Mã có thể sai, đã dùng hoặc tài khoản quản trị phải luôn bật 2FA."));
        }

        return NoContent();
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

        return Ok(new AccountResponse(
            userId,
            User.FindFirstValue(ClaimTypes.Email)!,
            User.FindFirstValue(ClaimTypes.Role) ?? "user"));
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
    public sealed record AccountResponse(Guid UserId, string Email, string Role);
    public sealed record TwoFactorRequiredResponse(bool RequiresTwoFactor);
    public sealed record ErrorResponse(string Error);

    public sealed record RegisterRequest(
        [Required, EmailAddress, StringLength(320)] string Email,
        [Required, StringLength(128, MinimumLength = 12),
         RegularExpression("^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^A-Za-z0-9]).{12,128}$")]
        string Password)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
    }

    public sealed record LoginRequest(
        [Required, EmailAddress, StringLength(320)] string Email,
        [Required, StringLength(128, MinimumLength = 1)] string Password);
    public sealed record TwoFactorVerifyRequest(
        [Required, StringLength(32, MinimumLength = 6)] string Code);
    public sealed record TwoFactorCodeRequest(
        [Required, StringLength(32, MinimumLength = 6)] string Code);

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated principal has no valid user ID.");

    private async Task RecordAuditAsync(Guid? userId, string action, CancellationToken cancellationToken)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        await audit.RecordAsync(
            userId,
            action,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            cancellationToken);
    }
}
