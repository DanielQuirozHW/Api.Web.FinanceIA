// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Abstraction for persisting uploaded document files.
/// Swap the registered implementation to change storage backends
/// (local disk → Azure Blob Storage) with a single DI change in Program.cs.
/// </summary>
public interface IDocumentStorageService
{
    /// <summary>
    /// Saves the file stream and returns the storage path where it was persisted.
    /// </summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="documentId">Document primary key used as the stored file name.</param>
    /// <param name="content">Readable stream of file bytes.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>The storage path (e.g. a local path or blob URI).</returns>
    Task<string> SaveAsync(string tenantId, Guid documentId, Stream content, CancellationToken ct);

    /// <summary>
    /// Reads the file at <paramref name="storagePath"/> and returns its bytes.
    /// </summary>
    /// <param name="storagePath">The storage path returned by <see cref="SaveAsync"/>.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<byte[]> ReadAsync(string storagePath, CancellationToken ct);
}
