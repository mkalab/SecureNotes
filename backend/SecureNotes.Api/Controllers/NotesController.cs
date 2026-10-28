using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notes")]
public sealed class NotesController(INotesRepository notes) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery, StringLength(200)] string? search,
        [FromQuery] Guid? categoryId,
        CancellationToken cancellationToken)
    {
        var result = await notes.ListNotesAsync(CurrentUserId, search, categoryId, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var note = await notes.GetNoteAsync(CurrentUserId, id, cancellationToken);
        return note is null ? NotFound() : Ok(note);
    }

    [HttpPost]
    public async Task<IActionResult> Create(NoteWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "A note title is required." });
        }

        if (!IsValidSourceUrl(request.SourceUrl))
        {
            return BadRequest(new { error = "Source URL must use HTTP or HTTPS." });
        }

        try
        {
            var note = await notes.CreateNoteAsync(ToModel(request, Guid.NewGuid()), cancellationToken);
            return note is null ? Problem("The note was not created.") : Created($"/api/notes/{note.NoteId}", note);
        }
        catch (SqlException exception) when (exception.Number is 50010 or 50011)
        {
            return NotFound(new { error = "The selected category or parent note was not found." });
        }
    }

    [HttpPost("{id:guid}/update")]
    public async Task<IActionResult> Update(Guid id, NoteWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "A note title is required." });
        }

        if (!IsValidSourceUrl(request.SourceUrl))
        {
            return BadRequest(new { error = "Source URL must use HTTP or HTTPS." });
        }

        try
        {
            var note = await notes.UpdateNoteAsync(ToModel(request, id), cancellationToken);
            return note is null ? NotFound() : Ok(note);
        }
        catch (SqlException exception) when (exception.Number is 50010 or 50011)
        {
            return NotFound(new { error = "The selected category or parent note was not found." });
        }
    }

    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await notes.DeleteNoteAsync(CurrentUserId, id, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    [HttpGet("{id:guid}/items")]
    public async Task<IActionResult> ListItems(Guid id, CancellationToken cancellationToken)
    {
        if (await notes.GetNoteAsync(CurrentUserId, id, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await notes.ListItemsAsync(CurrentUserId, id, cancellationToken));
    }

    [HttpPost("{id:guid}/items")]
    public async Task<IActionResult> CreateItem(Guid id, NoteItemWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "A checklist item title is required." });
        }

        try
        {
            var item = await notes.CreateItemAsync(
                CurrentUserId,
                id,
                Guid.NewGuid(),
                request.Title.Trim(),
                cancellationToken);
            return item is null ? NotFound() : Created($"/api/notes/{id}/items/{item.NoteItemId}", item);
        }
        catch (SqlException exception) when (exception.Number == 50011)
        {
            return NotFound();
        }
    }

    [HttpPost("{id:guid}/items/{itemId:guid}/update")]
    public async Task<IActionResult> UpdateItem(
        Guid id,
        Guid itemId,
        NoteItemWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "A checklist item title is required." });
        }

        var item = await notes.UpdateItemAsync(
            CurrentUserId,
            id,
            itemId,
            request.Title.Trim(),
            request.IsCompleted,
            cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("{id:guid}/items/{itemId:guid}/delete")]
    public async Task<IActionResult> DeleteItem(Guid id, Guid itemId, CancellationToken cancellationToken)
    {
        return await notes.DeleteItemAsync(CurrentUserId, id, itemId, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated principal has no valid user ID.");

    private NoteWriteModel ToModel(NoteWriteRequest request, Guid? id = null) => new(
        CurrentUserId,
        id,
        request.Title.Trim(),
        request.Content,
        request.CategoryId,
        request.ParentId,
        string.IsNullOrWhiteSpace(request.SourceUrl) ? null : request.SourceUrl.Trim(),
        request.Color,
        request.Importance,
        request.IsPinned,
        request.Position);

    private static bool IsValidSourceUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public sealed record NoteWriteRequest(
        [Required, StringLength(200, MinimumLength = 1)] string Title,
        [Required, StringLength(20000)] string Content,
        Guid? CategoryId,
        Guid? ParentId,
        [StringLength(2048)] string? SourceUrl,
        [Required, RegularExpression("^#[0-9A-Fa-f]{6}$")] string Color,
        [Range(0, 4)] byte Importance,
        bool IsPinned,
        int Position);

    public sealed record NoteItemWriteRequest(
        [Required, StringLength(1000, MinimumLength = 1)] string Title,
        bool IsCompleted);
}
