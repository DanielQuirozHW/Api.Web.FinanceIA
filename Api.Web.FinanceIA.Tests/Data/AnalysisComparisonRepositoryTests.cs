// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="AnalysisComparisonRepository"/>: which analysis is "the previous one",
/// and tenant isolation of both reads. On the EF Core in-memory provider, so the joins, the filters and
/// the ordering are exercised rather than a stub's answer.
/// </summary>
public sealed class AnalysisComparisonRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private readonly FinanciaDbContext _db = new(
        new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysis-comparison-{Guid.NewGuid()}")
            .Options);

    private readonly Guid _entityId = Guid.NewGuid();

    private static DateTime Day(int day) => new(2026, 10, day, 12, 0, 0, DateTimeKind.Utc);

    private (Analysis Analysis, AnalysisResult Result) Seed(
        DateTime createdAt,
        string status = AnalysisStatuses.Approved,
        DateTime? reviewedAt = null,
        DateTime? preApprovedAt = null,
        decimal? score = 40m,
        string tenantId = Tenant,
        Guid? entityId = null,
        string kind = AnalysisKinds.Balance,
        string? bcraDataJson = null,
        string? balanceJson = """{"assets":1000}""")
    {
        var analysis = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = tenantId,
            EntityId      = entityId ?? _entityId,
            DocumentId    = Guid.NewGuid(),
            AnalysisKind  = kind,
            RequestedBy   = Guid.NewGuid(),
            Status        = status,
            FiscalYear    = 2025,
            AiModel       = "claude-sonnet-4-6",
            AiTemperature = 0.00m,
            PromptVersion = "2.20",
            CreatedAt     = createdAt,
            ReviewedAt    = reviewedAt,
            PreApprovedAt = preApprovedAt,
        };

        var result = new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = analysis.AnalysisId,
            TenantId         = tenantId,
            AiSuggestedScore = score,
            AiSuggestedLevel = score is null ? null : "Medium",
            BalanceJson      = balanceJson,
            RatiosJson       = """{"current_ratio":1.5}""",
            BcraDataJson     = bcraDataJson,
            AiInputHash      = "in",
            AiOutputHash     = "out",
            ValidationStatus = "Valid",
            EnrichmentStatus = "Complete",
            GeneratedAt      = createdAt.AddMinutes(2),
        };

        _db.Analyses.Add(analysis);
        _db.AnalysisResults.Add(result);
        return (analysis, result);
    }

    private async Task<AnalysisComparisonSnapshot?> PreviousOfAsync(Analysis current)
    {
        await _db.SaveChangesAsync();
        return await new AnalysisComparisonRepository(_db).GetPreviousSnapshotAsync(
            Tenant, current.EntityId, current.AnalysisKind, current.AnalysisId, current.CreatedAt, CancellationToken.None);
    }

    [Fact]
    public async Task Previous_is_the_latest_decided_not_the_latest_created()
    {
        var (_, decidedLast)  = Seed(Day(1), reviewedAt: Day(10));
        Seed(Day(2), status: AnalysisStatuses.Rejected, reviewedAt: Day(5));
        var (current, _)      = Seed(Day(20), status: AnalysisStatuses.PendingReview, score: 20m);

        var previous = await PreviousOfAsync(current);

        Assert.Equal(decidedLast.ResultId, previous!.ResultId);
        Assert.Equal(AnalysisStatuses.Approved, previous.Status);
        Assert.Equal("""{"assets":1000}""", previous.BalanceJson);
        Assert.Equal("""{"current_ratio":1.5}""", previous.RatiosJson);
    }

    [Fact]
    public async Task A_pre_approval_counts_and_is_dated_by_its_pre_approval()
    {
        Seed(Day(1), reviewedAt: Day(5));
        var (_, preApproved) = Seed(Day(2), status: AnalysisStatuses.PreApproved, preApprovedAt: Day(8));
        var (current, _)     = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        var previous = await PreviousOfAsync(current);

        Assert.Equal(preApproved.ResultId, previous!.ResultId);
        Assert.Equal(Day(8), previous.DecidedAt);
    }

    [Fact]
    public async Task Without_a_decision_time_the_creation_time_orders_them()
    {
        Seed(Day(1));
        var (_, newer)   = Seed(Day(3));
        var (current, _) = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Equal(newer.ResultId, (await PreviousOfAsync(current))!.ResultId);
    }

    [Fact]
    public async Task Only_analyses_created_strictly_before_the_current_one_count()
    {
        var (_, earlier) = Seed(Day(1), reviewedAt: Day(2));
        Seed(Day(10), reviewedAt: Day(30)); // same CreatedAt as the current: not before it
        Seed(Day(15), reviewedAt: Day(16)); // created after
        var (current, _) = Seed(Day(10), status: AnalysisStatuses.PendingReview);

        Assert.Equal(earlier.ResultId, (await PreviousOfAsync(current))!.ResultId);
    }

    [Fact]
    public async Task Other_entities_tenants_kinds_undecided_and_unscored_analyses_are_excluded()
    {
        Seed(Day(1), entityId: Guid.NewGuid(), reviewedAt: Day(2));
        Seed(Day(1), tenantId: Other, reviewedAt: Day(2));
        Seed(Day(1), kind: AnalysisKinds.PersonaHumana, reviewedAt: Day(2));
        Seed(Day(1), status: AnalysisStatuses.PendingReview);
        Seed(Day(1), status: "Processing");
        Seed(Day(1), status: "Queued");
        Seed(Day(1), status: "Reviewed", reviewedAt: Day(2));
        Seed(Day(1), reviewedAt: Day(2), score: null);
        var (current, _) = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Null(await PreviousOfAsync(current));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"amounts_scale":"units","assets":null}""")]
    [InlineData("not json")]
    public async Task A_scored_analysis_whose_balance_was_not_read_is_never_the_previous(string? balanceJson)
    {
        Seed(Day(1), reviewedAt: Day(2), status: AnalysisStatuses.Rejected, balanceJson: balanceJson);
        var (current, _) = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Null(await PreviousOfAsync(current));
    }

    [Theory]
    [InlineData("""{"assets":0}""")]
    [InlineData("""{"equity":-5}""")]
    [InlineData("""{"net_revenue":10}""")]
    [InlineData("""{"net_profit":1,"assets":null}""")]
    public async Task One_read_balance_figure_is_enough_to_be_the_previous(string balanceJson)
    {
        var (_, previous) = Seed(Day(1), reviewedAt: Day(2), balanceJson: balanceJson);
        var (current, _)  = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Equal(previous.ResultId, (await PreviousOfAsync(current))?.ResultId);
    }

    [Fact]
    public async Task A_newer_unread_rejection_is_skipped_for_the_older_read_analysis()
    {
        var (_, read) = Seed(Day(1), reviewedAt: Day(2));
        Seed(Day(5), reviewedAt: Day(6), status: AnalysisStatuses.Rejected, balanceJson: "{}");
        var (current, _) = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Equal(read.ResultId, (await PreviousOfAsync(current))?.ResultId);
    }

    // ── Persona humana ──────────────────────────────────────────────────────

    private AnalysisParticipant Participant(
        Analysis analysis, string role = ParticipantRoles.Solicitante, string? tenantId = null, Guid? fiadorId = null)
    {
        var participant = new AnalysisParticipant
        {
            ParticipantId     = Guid.NewGuid(),
            TenantId          = tenantId ?? analysis.TenantId,
            AnalysisId        = analysis.AnalysisId,
            PersonLegajoId    = Guid.NewGuid(),
            Role              = role,
            AmountAtCharge    = 1_000_000m,
            Currency          = "ARS",
            InstallmentAmount = role == ParticipantRoles.Solicitante ? 50_000m : null,
            FiltersJson       = "[]",
            IndicatorsJson    = "[]",
            CoverageJson      = "{}",
            EvaluationVersion = "0.10",
            CuotasPorAnio     = role == ParticipantRoles.Solicitante ? (byte)12 : null,
            FiadorId          = fiadorId ?? (role == ParticipantRoles.Fiador ? Guid.NewGuid() : null),
            GuaranteeType     = role == ParticipantRoles.Fiador ? "Fianza" : null,
            CreatedAt         = analysis.CreatedAt,
        };

        _db.AnalysisParticipants.Add(participant);
        return participant;
    }

    private (Analysis Analysis, AnalysisResult Result) SeedPersona(
        DateTime createdAt,
        string status = AnalysisStatuses.Approved,
        DateTime? reviewedAt = null,
        bool withEvaluation = true,
        string tenantId = Tenant)
    {
        var seeded = Seed(createdAt, status, reviewedAt, score: null, tenantId: tenantId,
            kind: AnalysisKinds.PersonaHumana, balanceJson: null);
        if (withEvaluation)
            Participant(seeded.Analysis);
        return seeded;
    }

    [Fact]
    public async Task The_persona_previous_is_the_latest_decided_with_a_stored_evaluation()
    {
        var (_, older) = SeedPersona(Day(1), reviewedAt: Day(2));
        SeedPersona(Day(3), reviewedAt: Day(4), withEvaluation: false);
        SeedPersona(Day(5), status: AnalysisStatuses.PendingReview);
        var (current, _) = SeedPersona(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Equal(older.ResultId, (await PreviousOfAsync(current))?.ResultId);
    }

    [Fact]
    public async Task A_persona_previous_needs_no_score_and_ignores_company_analyses()
    {
        Seed(Day(5), reviewedAt: Day(6));
        var (_, persona) = SeedPersona(Day(1), status: AnalysisStatuses.Rejected, reviewedAt: Day(2));
        var (current, _) = SeedPersona(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Equal(persona.ResultId, (await PreviousOfAsync(current))?.ResultId);
    }

    [Fact]
    public async Task A_persona_evaluation_of_another_tenant_does_not_make_an_analysis_comparable()
    {
        var (other, _) = SeedPersona(Day(1), reviewedAt: Day(2), withEvaluation: false);
        Participant(other, tenantId: Other);
        Participant(other, role: ParticipantRoles.Fiador);
        var (current, _) = SeedPersona(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Null(await PreviousOfAsync(current));
    }

    [Fact]
    public async Task Person_facts_are_the_solicitante_terms_and_the_fiador_ids_of_this_tenant()
    {
        var (analysis, _) = SeedPersona(Day(1), withEvaluation: false);
        var solicitante = Participant(analysis);
        solicitante.FiltersJson = """[{"id":"bcra_situacion"}]""";
        var fiador = Participant(analysis, role: ParticipantRoles.Fiador);
        Participant(analysis, role: ParticipantRoles.Fiador, tenantId: Other);
        await _db.SaveChangesAsync();
        var sut = new AnalysisComparisonRepository(_db);

        var facts = await sut.GetPersonFactsAsync(analysis.AnalysisId, Tenant, CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Equal(solicitante.FiltersJson, facts.FiltersJson);
        Assert.Equal("0.10", facts.EvaluationVersion);
        Assert.Equal(1_000_000m, facts.AmountAtCharge);
        Assert.Equal("ARS", facts.Currency);
        Assert.Equal(50_000m, facts.InstallmentAmount);
        Assert.Equal((byte)12, facts.CuotasPorAnio);
        Assert.Equal([fiador.FiadorId!.Value], facts.FiadorIds);

        Assert.Null(await sut.GetPersonFactsAsync(analysis.AnalysisId, Other, CancellationToken.None));
        Assert.Null(await sut.GetPersonFactsAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task The_current_analysis_is_never_its_own_previous()
    {
        var (current, _) = Seed(Day(10), reviewedAt: Day(11));

        Assert.Null(await PreviousOfAsync(current));
    }

    [Fact]
    public async Task A_result_whose_analysis_row_belongs_to_another_tenant_is_not_previous()
    {
        // A result row stamped with this tenant over another tenant's analysis: both tables are filtered.
        var (foreignAnalysis, foreignResult) = Seed(Day(1), tenantId: Other, reviewedAt: Day(2));
        foreignResult.TenantId = Tenant;
        var (current, _) = Seed(Day(20), status: AnalysisStatuses.PendingReview);

        Assert.Equal(Other, foreignAnalysis.TenantId);
        Assert.Null(await PreviousOfAsync(current));
    }

    [Fact]
    public async Task Snapshot_is_tenant_scoped()
    {
        var (_, mine)   = Seed(Day(1));
        var (_, theirs) = Seed(Day(1), tenantId: Other);
        await _db.SaveChangesAsync();
        var sut = new AnalysisComparisonRepository(_db);

        Assert.NotNull(await sut.GetSnapshotByResultIdAsync(mine.ResultId, Tenant, CancellationToken.None));
        Assert.Null(await sut.GetSnapshotByResultIdAsync(theirs.ResultId, Tenant, CancellationToken.None));
        Assert.Null(await sut.GetSnapshotByResultIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_reads_the_bcra_scalars_and_the_header_columns()
    {
        const string bcra =
            """{"deuda":{"estado":0,"situacionMaxima":3,"montoTotal":1234.5},"cheques":{"estado":0,"totalRechazados":2,"montoTotalRechazado":9000}}""";
        var (analysis, result) = Seed(Day(1), reviewedAt: Day(3), bcraDataJson: bcra);
        await _db.SaveChangesAsync();

        var snapshot = await new AnalysisComparisonRepository(_db)
            .GetSnapshotByResultIdAsync(result.ResultId, Tenant, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(analysis.AnalysisId, snapshot.AnalysisId);
        Assert.Equal(_entityId, snapshot.EntityId);
        Assert.Equal(AnalysisKinds.Balance, snapshot.AnalysisKind);
        Assert.Equal(2025, snapshot.FiscalYear);
        Assert.Equal("2.20", snapshot.PromptVersion);
        Assert.Equal(Day(3), snapshot.DecidedAt);
        Assert.Equal(40m, snapshot.AiSuggestedScore);
        Assert.Equal("0", snapshot.BcraDeudaEstadoRaw);
        Assert.Equal("3", snapshot.BcraDeudaSituacionMaximaRaw);
        Assert.Equal("1234.5", snapshot.BcraDeudaMontoTotalRaw);
        Assert.Equal("0", snapshot.BcraChequesEstadoRaw);
        Assert.Equal("2", snapshot.BcraChequesTotalRechazadosRaw);
        Assert.Equal("9000", snapshot.BcraChequesMontoTotalRechazadoRaw);
    }

    // ── T-SQL shape (SqlServer provider, never contacted: ToQueryString only) ──

    private static FinanciaDbContext NewSqlServerContext() => new(
        new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options);

    [Fact]
    public void The_previous_lookup_names_no_blob_and_filters_the_tenant_on_both_tables()
    {
        using var ctx = NewSqlServerContext();

        var sql = new AnalysisComparisonRepository(ctx)
            .PreviousResultIdQuery(Tenant, _entityId, AnalysisKinds.Balance, Guid.NewGuid(), Day(10))
            .ToQueryString();

        // BalanceJson is inspected in the WHERE clause, through JSON_VALUE only — never selected.
        var withoutJsonValueCalls = Regex.Replace(sql, @"JSON_VALUE\([^)]*\)", "<extracted>");
        foreach (var blob in new[] { "BalanceJson", "RatiosJson", "ScoreBreakdownJson", "BcraDataJson", "ReviewNotes" })
            Assert.DoesNotContain(blob, withoutJsonValueCalls, StringComparison.Ordinal);
        foreach (var path in new[] { "$.assets", "$.equity", "$.net_revenue", "$.net_profit" })
            Assert.Contains(path, sql, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(sql, @"JSON_VALUE\(\[a0\]\.\[BalanceJson\]").Count);

        Assert.Matches(@"\[a\]\.\[TenantId\] = ", sql);
        Assert.Matches(@"\[a0\]\.\[TenantId\] = ", sql);
        Assert.Contains("COALESCE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalysisParticipants", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_persona_previous_lookup_names_no_blob_and_filters_the_tenant_on_every_table()
    {
        using var ctx = NewSqlServerContext();

        var sql = new AnalysisComparisonRepository(ctx)
            .PreviousResultIdQuery(Tenant, _entityId, AnalysisKinds.PersonaHumana, Guid.NewGuid(), Day(10))
            .ToQueryString();

        foreach (var blob in new[] { "BalanceJson", "RatiosJson", "ScoreBreakdownJson", "BcraDataJson", "ReviewNotes",
                                     "FiltersJson", "IndicatorsJson", "CoverageJson", "DocumentVersionsJson", "JSON_VALUE" })
            Assert.DoesNotContain(blob, sql, StringComparison.Ordinal);

        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("[AnalysisParticipants]", sql, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(sql, @"\]\.\[TenantId\] = ").Count);
        Assert.DoesNotContain("AiSuggestedScore", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_persona_facts_read_only_the_solicitante_row_of_this_tenant()
    {
        using var ctx = NewSqlServerContext();

        var sql = new AnalysisComparisonRepository(ctx).PersonFactsQuery(Guid.NewGuid(), Tenant).ToQueryString();

        Assert.Contains("[TenantId] = ", sql, StringComparison.Ordinal);
        Assert.Contains("N'Solicitante'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ScoreJson", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_snapshot_reads_bcra_only_through_json_value_with_camel_case_paths()
    {
        using var ctx = NewSqlServerContext();

        var sql = new AnalysisComparisonRepository(ctx).SnapshotQuery(Guid.NewGuid(), Tenant).ToQueryString();

        foreach (var path in new[] { "$.deuda.estado", "$.deuda.situacionMaxima", "$.deuda.montoTotal",
                                     "$.cheques.estado", "$.cheques.totalRechazados", "$.cheques.montoTotalRechazado" })
            Assert.Contains(path, sql, StringComparison.Ordinal);

        Assert.Equal(6, Regex.Matches(sql, @"JSON_VALUE\(").Count);
        Assert.Equal(6, Regex.Matches(sql, "BcraDataJson").Count);
        var withoutJsonValueCalls = Regex.Replace(sql, @"JSON_VALUE\([^)]*\)", "<extracted>");
        Assert.DoesNotContain("BcraDataJson", withoutJsonValueCalls, StringComparison.Ordinal);

        foreach (var snapshot in new[] { "LufeDataJson", "FiscalDataJson", "CasfogDataJson", "NosisDataJson", "MacroDataJson" })
            Assert.DoesNotContain(snapshot, sql, StringComparison.Ordinal);
    }
}
