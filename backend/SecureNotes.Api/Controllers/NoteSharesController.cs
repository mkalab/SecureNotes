using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecureNotes.Api.Sharing;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notes/{noteId:guid}/shares")]
public sealed class NoteSharesController(IShareLinkService shares) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid noteId, CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        return Ok(await shares.ListAsync(CurrentUserId, noteId, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid noteId,
        CreateNoteShareRequest request,
        CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        try
        {
            var created = await shares.CreateAsync(
                CurrentUserId,
                noteId,
                request.Permission,
                request.ExpiresInHours,
                HttpContext,
                cancellationToken);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (ShareLinkRejectedException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (Microsoft.Data.SqlClient.SqlException exception) when (exception.Number == 50070)
        {
            return NotFound();
        }
    }

    [HttpPost("{shareId:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid noteId, Guid shareId, CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        return await shares.RevokeAsync(
            CurrentUserId,
            noteId,
            shareId,
            HttpContext,
            cancellationToken)
            ? NoContent()
            : NotFound();
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated principal has no valid user ID.");

    private IActionResult FeatureDisabled() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Tính năng chia sẻ ghi chú hiện đang được tắt." });
}

public sealed record CreateNoteShareRequest(string Permission = "view", int ExpiresInHours = 0);
