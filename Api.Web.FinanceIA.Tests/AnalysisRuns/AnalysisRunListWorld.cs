// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// An in-memory FinanciaDbContext and the rows GET /api/analyses/runs reads, for the repository and
/// service tests. Every helper writes through and saves, so a test reads what it seeded.
/// </summary>
internal sealed class AnalysisRunListWorld : IDisposable
{
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";

    /// <summary>The instant the tests treat as "now".</summary>
    public static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    public AnalysisRunListWorld()
    {
        Db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysis-runs-{Guid.NewGuid()}")
            .Options);
    }

    public FinanciaDbContext Db { get; }

    public void Dispose() => Db.Dispose();

    public Entity AddEntity(string tenantId, string name = "Metalúrgica Del Sur S.A.", Guid? personLegajoId = null)
    {
        var entity = new Entity
        {
            EntityId       = Guid.NewGuid(),
            TenantId       = tenantId,
            CUIT           = "30-12345678-9",
            BusinessName   = name,
            PersonLegajoId = personLegajoId
        };
        Db.Entities.Add(entity);
        Save();
        return entity;
    }

    public Document AddDocument(
        string    tenantId,
        Entity    entity,
        string    status,
        DateTime? processedAt   = null,
        DateTime? uploadedAt    = null,
        string    documentType  = DocumentTypes.Balance,
        string    fileHash      = "hash-default",
        int       attempts      = 0,
        string?   lastError     = null,
        DateTime? lastFailedAt  = null,
        bool      isDeleted     = false,
        Guid?     personLegajoId = null,
        string?   legajoStatus  = null,
        string    fileName      = "balance-2024.pdf",
        int?      fiscalYear    = null)
    {
        var document = new Document
        {
            DocumentId             = Guid.NewGuid(),
            TenantId               = tenantId,
            EntityId               = entity.EntityId,
            UploadedBy             = Guid.NewGuid(),
            OriginalFileName       = fileName,
            StoredFileName         = "stored.pdf",
            StoragePath            = "/tmp/stored.pdf",
            MimeType               = "application/pdf",
            FileHashSHA256         = fileHash,
            DocumentType           = documentType,
            ProcessingStatus       = status,
            ProcessedAt            = processedAt,
            UploadedAt             = uploadedAt ?? Now.AddDays(-1),
            ExtractionAttempts     = attempts,
            LastExtractionError    = lastError,
            LastExtractionFailedAt = lastFailedAt,
            IsDeleted              = isDeleted,
            PersonLegajoId         = personLegajoId,
            LegajoStatus           = legajoStatus,
            FiscalYear             = fiscalYear
        };
        Db.Documents.Add(document);
        Save();
        return document;
    }

    /// <summary>A balance analysis of <paramref name="document"/> and its result; returns the ResultId.</summary>
    public Guid AddAnalysis(Document document, DateTime createdAt, string kind = AnalysisKinds.Balance)
    {
        var analysis = new Analysis
        {
            AnalysisId   = Guid.NewGuid(),
            TenantId     = document.TenantId,
            DocumentId   = document.DocumentId,
            EntityId     = document.EntityId,
            AnalysisKind = kind,
            RequestedBy  = Guid.NewGuid(),
            Status       = "PendingReview",
            CreatedAt    = createdAt
        };
        var result = new AnalysisResult
        {
            ResultId   = Guid.NewGuid(),
            AnalysisId = analysis.AnalysisId,
            TenantId   = document.TenantId
        };
        Db.Analyses.Add(analysis);
        Db.AnalysisResults.Add(result);
        Save();
        return result.ResultId;
    }

    public void AddFailureAudit(string tenantId, Guid documentId, DateTime timestamp, Guid? userId, string? newValueJson,
        string action = "Analysis.Failed", string entityType = "Document")
    {
        Db.AuditLogs.Add(new AuditLog
        {
            TenantId     = tenantId,
            UserId       = userId,
            Action       = action,
            EntityType   = entityType,
            EntityId     = documentId.ToString(),
            NewValueJson = newValueJson,
            Severity     = "Error",
            Timestamp    = timestamp
        });
        Save();
    }

    public User AddUser(string tenantId, string displayName)
    {
        var user = new User
        {
            UserId      = Guid.NewGuid(),
            TenantId    = tenantId,
            FirebaseUid = Guid.NewGuid().ToString(),
            Email       = $"{Guid.NewGuid():N}@example.com",
            DisplayName = displayName,
            Role        = "Analyst",
            IsActive    = true
        };
        Db.Users.Add(user);
        Save();
        return user;
    }

    /// <summary>
    /// A persona humana legajo, linked to a fresh entity; with active consent when
    /// <paramref name="consentActive"/> (recorded, and its AutorizacionDatos document live).
    /// </summary>
    public Entity AddPersonEntity(string tenantId, bool consentActive)
    {
        var legajoId = Guid.NewGuid();
        var entity   = AddEntity(tenantId, "Persona Humana", legajoId);

        Guid? consentDocumentId = null;
        if (consentActive)
        {
            consentDocumentId = AddDocument(
                tenantId, entity, "Pending",
                documentType:   DocumentTypes.AutorizacionDatos,
                personLegajoId: legajoId,
                legajoStatus:   LegajoStatuses.Ready).DocumentId;
        }

        Db.PersonLegajos.Add(new PersonLegajo
        {
            PersonLegajoId    = legajoId,
            TenantId          = tenantId,
            CUIT              = "20-12345678-9",
            FullName          = "Persona Humana",
            ConsentRecordedAt = consentActive ? Now.AddDays(-10) : null,
            ConsentDocumentId = consentDocumentId,
            CreatedAt         = Now.AddDays(-10),
            UpdatedAt         = Now.AddDays(-10)
        });
        Save();
        return entity;
    }

    private void Save()
    {
        Db.ChangeTracker.DetectChanges();
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }
}
