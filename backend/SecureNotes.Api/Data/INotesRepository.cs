using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public interface INotesRepository
{
    Task<IReadOnlyList<Note>> ListNotesAsync(Guid userId, string? search, Guid? categoryId, CancellationToken cancellationToken);
    Task<Note?> GetNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken);
    Task<Note?> CreateNoteAsync(NoteWriteModel note, CancellationToken cancellationToken);
    Task<Note?> UpdateNoteAsync(NoteWriteModel note, CancellationToken cancellationToken);
    Task<bool> DeleteNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NoteCategory>> ListCategoriesAsync(Guid userId, CancellationToken cancellationToken);
    Task<NoteCategory?> CreateCategoryAsync(Guid userId, string name, CancellationToken cancellationToken);
    Task<NoteCategory?> UpdateCategoryAsync(Guid userId, Guid categoryId, string name, CancellationToken cancellationToken);
    Task<bool> DeleteCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NoteItem>> ListItemsAsync(Guid userId, Guid noteId, CancellationToken cancellationToken);
    Task<NoteItem?> CreateItemAsync(
        Guid userId,
        Guid noteId,
        Guid noteItemId,
        string title,
        CancellationToken cancellationToken);
    Task<NoteItem?> UpdateItemAsync(Guid userId, Guid noteId, Guid itemId, string title, bool isCompleted, CancellationToken cancellationToken);
    Task<bool> DeleteItemAsync(Guid userId, Guid noteId, Guid itemId, CancellationToken cancellationToken);
}

public sealed record NoteWriteModel(
    Guid UserId,
    Guid? NoteId,
    string Title,
    string Content,
    Guid? CategoryId,
    Guid? ParentId,
    string? SourceUrl,
    string Color,
    byte Importance,
    bool IsPinned,
    int Position);
