using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SecureNotes.Api.Attachments;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;
using SecureNotes.Api.Sharing;

namespace SecureNotes.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting("shared-links")]
[Route("api/shared")]
public sealed class SharedNotesController(
    IShareLinkService shares,
    INotesRepository notes,
    IAttachmentService attachments) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        var access = await shares.GetActiveAsync(token, cancellationToken);
        if (access is null)
        {
            return NotFound();
        }

        var note = await notes.GetNoteAsync(access.UserId, access.NoteId, cancellationToken);
        if (note is null)
        {
            return NotFound();
        }

        await shares.RecordAccessAsync(access, HttpContext, cancellationToken);
        return Ok(new SharedNoteResponse(note.Title, note.Content, access.Permission, access.ExpiresAt));
    }

    [HttpGet("{token}/attachments")]
    public async Task<IActionResult> ListAttachments(string token, CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        var access = await shares.GetActiveAsync(token, cancellationToken);
        if (access is null || await notes.GetNoteAsync(access.UserId, access.NoteId, cancellationToken) is null)
        {
            return NotFound();
        }

        await shares.RecordAccessAsync(access, HttpContext, cancellationToken);
        return Ok(await attachments.ListAsync(access.UserId, access.NoteId, cancellationToken));
    }

    [HttpGet("{token}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadAttachment(
        string token,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        var access = await shares.GetActiveAsync(token, cancellationToken);
        if (access is null)
        {
            return NotFound();
        }

        var download = await attachments.DownloadAsync(
            access.UserId,
            access.NoteId,
            attachmentId,
            HttpContext,
            cancellationToken);
        if (download is null)
        {
            return NotFound();
        }

        await shares.RecordAccessAsync(access, HttpContext, cancellationToken);
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = download.ContentType;
        Response.ContentLength = download.Content.LongLength;
        Response.Headers["Content-Disposition"] = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = download.FileName
        }.ToString();
        try
        {
            await Response.Body.WriteAsync(download.Content, cancellationToken);
            return new EmptyResult();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(download.Content);
        }
    }

    [HttpPost("{token}/update")]
    public async Task<IActionResult> Update(
        string token,
        SharedNoteUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!shares.Enabled)
        {
            return FeatureDisabled();
        }

        var access = await shares.GetActiveAsync(token, cancellationToken);
        if (access is null || access.Permission != "edit")
        {
            return NotFound();
        }

        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Title)
            || request.Title.Length > 200
            || request.Content.Length > 20_000)
        {
            return BadRequest(new { error = "Tiêu đề hoặc nội dung ghi chú không hợp lệ." });
        }

        var existing = await notes.GetNoteAsync(access.UserId, access.NoteId, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        var updated = await notes.UpdateNoteAsync(
            new NoteWriteModel(
                existing.UserId,
                existing.NoteId,
                request.Title.Trim(),
                request.Content,
                existing.CategoryId,
                existing.ParentId,
                existing.SourceUrl,
                existing.Color,
                existing.Importance,
                existing.IsPinned,
                existing.Position),
            cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        await shares.RecordAccessAsync(access, HttpContext, cancellationToken);
        return Ok(new SharedNoteResponse(updated.Title, updated.Content, access.Permission, access.ExpiresAt));
    }

    private IActionResult FeatureDisabled() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Tính năng chia sẻ ghi chú hiện đang được tắt." });
}
