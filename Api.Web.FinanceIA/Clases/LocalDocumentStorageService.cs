// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-08-10

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Local-disk implementation of <see cref="IDocumentStorageService"/>.
/// Stores files under {baseDirectory}/{tenantId}/{documentId}.pdf, where the base
/// directory is resolved (see <see cref="ResolveBaseDirectory"/>) from the
/// <c>Storage:DocumentsPath</c> setting, else <c>%HOME%\financia-uploads</c>
/// (persistent, instance-shared on Azure App Service), else a legacy temp path.
/// Replace with an Azure Blob Storage implementation and update the DI
/// registration in Program.cs when moving to production.
/// </summary>
public sealed class LocalDocumentStorageService : IDocumentStorageService
{
    /// <summary>Configuration key for an explicit storage base directory override.</summary>
    public const string StoragePathConfigKey = "Storage:DocumentsPath";

    // Last-resort fallback (neither the setting nor HOME present): the historical path,
    // kept so local dev and the existing tests keep working. Ephemeral on Azure — never
    // reached there because HOME is always present on App Service.
    private const string DefaultBaseDirectory = "/tmp/financia-uploads";

    private readonly string _baseDirectory;
    private readonly ILogger<LocalDocumentStorageService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="LocalDocumentStorageService"/>.
    /// </summary>
    public LocalDocumentStorageService(
        IConfiguration configuration,
        ILogger<LocalDocumentStorageService> logger)
    {
        _logger = logger;
        _baseDirectory = ResolveBaseDirectory(
            configuration[StoragePathConfigKey],
            configuration["HOME"]);
    }

    /// <summary>
    /// Pure resolution of the storage base directory (no I/O, no environment access):
    /// an explicit <c>Storage:DocumentsPath</c> wins; otherwise
    /// <c>{homeDirectory}\financia-uploads</c> when <c>HOME</c> is present
    /// (Azure App Service points <c>HOME</c> at persistent, instance-shared storage);
    /// otherwise the legacy temp path so local dev and tests keep working.
    /// </summary>
    /// <param name="configuredPath">Value of the <c>Storage:DocumentsPath</c> setting, if any.</param>
    /// <param name="homeDirectory">Value of the <c>HOME</c> environment variable, if any.</param>
    public static string ResolveBaseDirectory(string? configuredPath, string? homeDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        if (!string.IsNullOrWhiteSpace(homeDirectory))
            return Path.Combine(homeDirectory, "financia-uploads");

        return DefaultBaseDirectory;
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        string tenantId,
        Guid documentId,
        Stream content,
        CancellationToken ct)
    {
        var directory = Path.Combine(_baseDirectory, tenantId);
        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, $"{documentId}.pdf");

        await using var fileStream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await content.CopyToAsync(fileStream, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Document {DocumentId} saved locally at {FilePath} for tenant {TenantId}",
            documentId, filePath, tenantId);

        return filePath;
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(string storagePath, CancellationToken ct)
    {
        if (!File.Exists(storagePath))
            throw new FileNotFoundException($"Document not found at storage path.", storagePath);

        return await File.ReadAllBytesAsync(storagePath, ct).ConfigureAwait(false);
    }
}
