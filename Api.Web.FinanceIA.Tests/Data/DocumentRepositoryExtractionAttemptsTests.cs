// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for the extraction-budget pair on <see cref="DocumentRepository"/>:
/// <c>SumExtractionAttemptsByHashAsync</c> (the budget already spent) and
/// <c>RecordExtractionFailureAsync</c> (spending one unit of it). Run against the EF Core
/// in-memory provider so the actual WHERE clause is exercised rather than a stub's return value.
///
/// <para>
/// The sum is keyed on FileHashSHA256, not on DocumentId, and that is the whole point: a
/// per-document budget resets the moment the analyst re-uploads the same bytes, which is the
/// obvious way around the guard. These tests pin that the siblings are counted, that another
/// tenant's failures never are — there is no EF global query filter, so this predicate is the
/// only thing keeping one client's spend out of another's budget — and that a blank hash is
/// never treated as a wildcard.
/// </para>
/// </summary>
public sealed class DocumentRepositoryExtractionAttemptsTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";
    private const string HashA  = "aaaa000000000000000000000000000000000000000000000000000000000000";
    private const string HashB  = "bbbb000000000000000000000000000000000000000000000000000000000000";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"documents-attempts-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static DocumentRepository BuildSut(FinanciaDbContext ctx)
        => new(ctx, NullLogger<DocumentRepository>.Instance);

    private static Document Row(
        int      attempts,
        string   hash       = HashA,
        string   tenantId   = Tenant,
        bool     isDeleted  = false,
        Guid?    documentId = null) => new()
    {
        DocumentId         = documentId ?? Guid.NewGuid(),
        TenantId           = tenantId,
        EntityId           = Guid.NewGuid(),
        UploadedBy         = Guid.NewGuid(),
        OriginalFileName   = "balance.pdf",
        StoredFileName     = "balance.pdf",
        StoragePath        = "tenants/x/balance.pdf",
        FileSizeBytes      = 1024,
        MimeType           = "application/pdf",
        FileHashSHA256     = hash,
        DocumentType       = "BalanceSheet",
        FiscalYear         = 2026,
        ProcessingStatus   = "Failed",
        UploadedAt         = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        RetentionExpiresAt = new DateTime(2030, 8, 1, 12, 0, 0, DateTimeKind.Utc),
        IsDeleted          = isDeleted,
        ExtractionAttempts = attempts
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Document[] documents)
    {
        ctx.Documents.AddRange(documents);
        ctx.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── SumExtractionAttemptsByHashAsync ────────────────────────────────────────

    [Fact]
    public async Task Sum_AddsUpEverySiblingSharingTheHash()
    {
        // The re-upload bypass: three rows, same bytes, budget already spent across them.
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(1), Row(1), Row(2));

        var sum = await BuildSut(ctx)
            .SumExtractionAttemptsByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal(4, sum);
    }

    [Fact]
    public async Task Sum_IgnoresADifferentFile()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(3, hash: HashA), Row(7, hash: HashB));

        var sum = await BuildSut(ctx)
            .SumExtractionAttemptsByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal(3, sum);
    }

    [Fact]
    public async Task Sum_IgnoresAnotherTenant()
    {
        // Same file content uploaded by two tenants: one tenant's failures must never spend the
        // other's budget.
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(2, tenantId: Tenant), Row(9, tenantId: Other));

        var sum = await BuildSut(ctx)
            .SumExtractionAttemptsByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal(2, sum);
    }

    [Fact]
    public async Task Sum_IgnoresDeletedDocuments()
    {
        // An analyst who removed the document is not still spending against it.
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(1), Row(5, isDeleted: true));

        var sum = await BuildSut(ctx)
            .SumExtractionAttemptsByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal(1, sum);
    }

    [Fact]
    public async Task Sum_ReturnsZeroWhenNothingMatches()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(4, hash: HashB));

        var sum = await BuildSut(ctx)
            .SumExtractionAttemptsByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal(0, sum);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sum_TreatsABlankHashAsZeroAndNotAsAWildcard(string? hash)
    {
        // A blank hash must never sum a whole tenant's failures onto one unrelated file.
        using var ctx = NewContext();
        await SeedAsync(ctx, Row(3, hash: HashA), Row(4, hash: HashB));

        var sum = await BuildSut(ctx)
            .SumExtractionAttemptsByHashAsync(hash, Tenant, CancellationToken.None);

        Assert.Equal(0, sum);
    }

    // ── GetLatestExtractionErrorByHashAsync ─────────────────────────────────────

    private static Document Failed(string? error, DateTime? failedAt, string hash = HashA, string tenantId = Tenant, bool isDeleted = false)
    {
        var row = Row(1, hash: hash, tenantId: tenantId, isDeleted: isDeleted);
        row.LastExtractionError    = error;
        row.LastExtractionFailedAt = failedAt;
        return row;
    }

    [Fact]
    public async Task LatestError_IsTheNewestFailureAmongTheSiblings()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Failed("claude_refusal",   new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
            Failed("claude_api_error", new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc)),
            Failed("claude_timeout",   new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)),
            Failed(null,               null));   // a fresh re-upload that never failed

        var latest = await BuildSut(ctx)
            .GetLatestExtractionErrorByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal("claude_api_error", latest);
    }

    [Fact]
    public async Task LatestError_IgnoresDeletedRowsOtherFilesAndOtherTenants()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Failed("claude_refusal",      new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
            Failed("claude_api_error",    new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), isDeleted: true),
            Failed("claude_timeout",      new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), hash: HashB),
            Failed("claude_server_error", new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), tenantId: Other));

        var latest = await BuildSut(ctx)
            .GetLatestExtractionErrorByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Equal("claude_refusal", latest);
    }

    [Fact]
    public async Task LatestError_IsNullWhenTheFileNeverFailed()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Failed(null, null));

        var latest = await BuildSut(ctx)
            .GetLatestExtractionErrorByHashAsync(HashA, Tenant, CancellationToken.None);

        Assert.Null(latest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LatestError_TreatsABlankHashAsNoFileAndNotAsAWildcard(string? hash)
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Failed("claude_refusal", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)));

        var latest = await BuildSut(ctx)
            .GetLatestExtractionErrorByHashAsync(hash, Tenant, CancellationToken.None);

        Assert.Null(latest);
    }

    // ── RecordExtractionFailureAsync is deliberately NOT exercised here ─────────
    //
    // It is an ExecuteUpdateAsync, which the in-memory provider does not implement — the same
    // reason CreditLineRepositoryTests covers neither SetStatusAsync nor RaiseAsync, and
    // FiadorRepositoryTests covers neither. The behaviour that matters to the billing rule is
    // pinned one layer up, in AnalysisControllerExtractionBudgetTests: that a failed extraction
    // records exactly one attempt, with a sanitized reason code, and writes no [dbo].[Analyses]
    // row for the monthly quota to count.
}
