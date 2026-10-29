using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Authorize(Policy = "admin-2fa")]
[Route("api/console-4d91")]
public sealed class AdminController(
    IAdminRepository admin,
    IAuditRepository audit) : ControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset > 100_000)
        {
            return BadRequest(new { error = "Offset không hợp lệ." });
        }

        return Ok(await admin.ListUsersAsync(CurrentUserId, offset, Math.Clamp(limit, 1, 100), cancellationToken));
    }

    [HttpPost("users/{userId:guid}/lock")]
    public Task<IActionResult> LockUser(Guid userId, CancellationToken cancellationToken) =>
        SetUserDisabled(userId, true, cancellationToken);

    [HttpPost("users/{userId:guid}/unlock")]
    public Task<IActionResult> UnlockUser(Guid userId, CancellationToken cancellationToken) =>
        SetUserDisabled(userId, false, cancellationToken);

    [HttpPost("users/{userId:guid}/sessions/revoke")]
    public async Task<IActionResult> RevokeSessions(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var count = await admin.RevokeUserSessionsAsync(CurrentUserId, userId, cancellationToken);
            await RecordAdminAuditAsync("admin_sessions_revoked", userId, cancellationToken);
            return Ok(new { revokedSessions = count });
        }
        catch (SqlException exception) when (exception.Number == 50082)
        {
            return NotFound();
        }
    }

    [HttpGet("audit")]
    public async Task<IActionResult> ListAudit(
        [FromQuery] long? beforeId = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (beforeId is <= 0)
        {
            return BadRequest(new { error = "beforeId không hợp lệ." });
        }

        return Ok(await admin.ListAuditAsync(CurrentUserId, beforeId, Math.Clamp(limit, 1, 100), cancellationToken));
    }

    [HttpGet("shares")]
    public async Task<IActionResult> ListShares(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await admin.ListSharesAsync(CurrentUserId, Math.Clamp(limit, 1, 100), cancellationToken));

    [HttpPost("shares/{shareId:guid}/revoke")]
    public async Task<IActionResult> RevokeShare(Guid shareId, CancellationToken cancellationToken)
    {
        if (!await admin.RevokeShareAsync(CurrentUserId, shareId, cancellationToken))
        {
            return NotFound();
        }

        await RecordAdminAuditAsync("admin_share_revoked", shareId, cancellationToken, "note_share");
        return NoContent();
    }

    private async Task<IActionResult> SetUserDisabled(
        Guid userId,
        bool disabled,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await admin.SetUserDisabledAsync(CurrentUserId, userId, disabled, cancellationToken))
            {
                return NotFound();
            }
        }
        catch (SqlException exception) when (exception.Number == 50082)
        {
            return NotFound();
        }
        catch (SqlException exception) when (exception.Number == 50083)
        {
            return Conflict(new { error = "Không thể khóa quản trị viên đang hoạt động cuối cùng." });
        }

        await RecordAdminAuditAsync(disabled ? "admin_user_locked" : "admin_user_unlocked", userId, cancellationToken);
        return NoContent();
    }

    private async Task RecordAdminAuditAsync(
        string action,
        Guid resourceId,
        CancellationToken cancellationToken,
        string resourceType = "user")
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        await audit.RecordAsync(
            CurrentUserId,
            action,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            cancellationToken,
            resourceType,
            resourceId);
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated administrator has no valid user ID.");
}
