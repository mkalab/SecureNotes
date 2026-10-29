using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Attachments;
using SecureNotes.Api.Data;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notes/{noteId:guid}/attachments")]
public sealed class AttachmentsController(
    IAttachmentService attachments,
    INotesRepository notes) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid noteId, CancellationToken cancellationToken)
    {
        if (!attachments.Enabled)
        {
            return FeatureDisabled();
        }

        if (await notes.GetNoteAsync(CurrentUserId, noteId, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await attachments.ListAsync(CurrentUserId, noteId, cancellationToken));
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("attachment-upload")]
    public async Task<IActionResult> Upload(
        Guid noteId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (!attachments.Enabled)
        {
            return FeatureDisabled();
        }

        if (file is null)
        {
            return BadRequest(new { error = "Hãy chọn một tệp để tải lên." });
        }

        try
        {
            var attachment = await attachments.UploadAsync(CurrentUserId, noteId, file, HttpContext, cancellationToken);
            return Created(
                $"/api/notes/{noteId}/attachments/{attachment.AttachmentId}/download",
                attachment);
        }
        catch (AttachmentRejectedException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (AttachmentLimitExceededException exception)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = exception.Message });
        }
        catch (SqlException exception) when (exception.Number == 50040)
        {
            return NotFound();
        }
        catch (SqlException exception) when (exception.Number == 50041)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Tệp vượt giới hạn kích thước." });
        }
        catch (SqlException exception) when (exception.Number is 50042 or 50043)
        {
            return Conflict(new { error = "Đã đạt giới hạn số lượng tệp hoặc dung lượng tài khoản." });
        }
    }

    [HttpGet("{attachmentId:guid}/download")]
    public async Task<IActionResult> Download(
        Guid noteId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        if (!attachments.Enabled)
        {
            return FeatureDisabled();
        }

        var download = await attachments.DownloadAsync(
            CurrentUserId,
            noteId,
            attachmentId,
            HttpContext,
            cancellationToken);
        if (download is null)
        {
            return NotFound();
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = download.ContentType;
        Response.ContentLength = download.Content.LongLength;
        Response.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("attachment")
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
            CryptographicOperations.ZeroMemory(download.Content);
        }
    }

    [HttpPost("{attachmentId:guid}/delete")]
    public async Task<IActionResult> Delete(Guid noteId, Guid attachmentId, CancellationToken cancellationToken)
    {
        if (!attachments.Enabled)
        {
            return FeatureDisabled();
        }

        return await attachments.DeleteAsync(
            CurrentUserId,
            noteId,
            attachmentId,
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
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Tính năng tệp đính kèm hiện đang được tắt." });
}
