using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Data;

namespace SecureNotes.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/categories")]
public sealed class CategoriesController(INotesRepository notes) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await notes.ListCategoriesAsync(CurrentUserId, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CategoryWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "A category name is required." });
        }

        try
        {
            var category = await notes.CreateCategoryAsync(CurrentUserId, request.Name.Trim(), cancellationToken);
            return category is null ? Problem("The category was not created.") : Created("/api/categories", category);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            return Conflict(new { error = "A category with this name already exists." });
        }
    }

    [HttpPost("{id:guid}/update")]
    public async Task<IActionResult> Update(Guid id, CategoryWriteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "A category name is required." });
        }

        try
        {
            var category = await notes.UpdateCategoryAsync(CurrentUserId, id, request.Name.Trim(), cancellationToken);
            return category is null ? NotFound() : Ok(category);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            return Conflict(new { error = "A category with this name already exists." });
        }
    }

    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await notes.DeleteCategoryAsync(CurrentUserId, id, cancellationToken) ? NoContent() : NotFound();

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authenticated principal has no valid user ID.");

    public sealed record CategoryWriteRequest([Required, StringLength(100, MinimumLength = 1)] string Name);
}
