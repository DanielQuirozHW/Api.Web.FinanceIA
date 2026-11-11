// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The decision review gate (score credibility 1b-5, calmed by fix/review-gate-calm). An Approved — a one-step
/// approval, a pre-approval and a confirmer's quorum vote alike — on a balance analysis whose score-reliability
/// verdict is <c>provisional</c> or <c>none</c> needs <c>acknowledgedCaveats: true</c> (409
/// <c>caveats_not_acknowledged</c>); <c>with_caveats</c> needs nothing, and a Rejected never needs the
/// acknowledgement. A decision on figures that failed integrity needs a note of 20+ characters (409
/// <c>figures_review_required</c>): the figures-review note on an approval, that or — when empty — the
/// <c>reviewNotes</c> on a rejection. A refused decision writes NOTHING: no status, no audit row, no notification,
/// no vote and no vote wipe. A return for rework, a persona humana analysis and a complete score are never
/// gated. The note rides the decision's existing audit row and never a log line.
/// </summary>
public sealed partial class AnalysisControllerDecideTests
{
    /// <summary>The analyst's free text the gate tests plant — it must reach the audit row and nothing else.</summary>
    private const string Note = "Verifiqué el pasivo corriente contra la hoja 4 del balance firmado.";

    /// <summary>A logger that keeps every rendered message and every structured value.</summary>
    private sealed class RecordingLogger : ILogger<AnalysisController>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Lines.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
        }
    }

    // ── Rows ────────────────────────────────────────────────────────────────────

    /// <summary>Makes the fixture result a real scored row with <paramref name="breakdownJson"/>, judged at its own date.</summary>
    private static void UseScoredRow(AnalysisResult result, Analysis analysis, decimal score, string breakdownJson)
    {
        result.AiSuggestedScore   = score;
        result.EnrichmentStatus   = "Complete";
        result.GeneratedAt        = ScoreReliabilityFixture.ScoreDate;
        result.RescoredAt         = null;
        result.ScoreBreakdownJson = breakdownJson;
        analysis.FiscalYear       = 2025;
    }

    private static void UseCompleteRow(AnalysisResult result, Analysis analysis)
        => UseScoredRow(result, analysis, 42.5m, ScoreReliabilityFixture.Json(facts: ScoreReliabilityFixture.CleanFacts()));

    /// <summary>with_caveats: one caveat (the inflation figure was imputed), integrity fine.</summary>
    private static void UseCaveatRow(AnalysisResult result, Analysis analysis)
        => UseScoredRow(result, analysis, 42.5m, ScoreReliabilityFixture.Json(
               facts: ScoreReliabilityFixture.CleanFacts() with { MacroImputed = true }));

    /// <summary>provisional, but no figure failed: integrity never verified (integrity_unverified).</summary>
    private static void UseProvisionalRow(AnalysisResult result, Analysis analysis)
        => UseScoredRow(result, analysis, 42.5m, ScoreReliabilityFixture.Json(
               facts: ScoreReliabilityFixture.CleanFacts() with
               {
                   Integrity = ScoreReliabilityFixture.CleanFacts().Integrity! with { Verified = false },
               }));

    /// <summary>none: there is no score at all (score_unavailable).</summary>
    private static void UseNoneRow(AnalysisResult result, Analysis analysis)
    {
        UseCompleteRow(result, analysis);
        result.AiSuggestedScore = null;
    }

    /// <summary>Applies the row a level name stands for: with_caveats, provisional or none.</summary>
    private static void UseLevelRow(string level, AnalysisResult result, Analysis analysis)
    {
        switch (level)
        {
            case ScoreReliabilityLevels.WithCaveats: UseCaveatRow(result, analysis);      break;
            case ScoreReliabilityLevels.Provisional: UseProvisionalRow(result, analysis); break;
            case ScoreReliabilityLevels.None:        UseNoneRow(result, analysis);        break;
            default: throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }

    /// <summary>provisional: the liquidez floor fixed the score from figures that failed integrity.</summary>
    private static void UseIntegrityFailedRow(AnalysisResult result, Analysis analysis)
        => UseScoredRow(result, analysis, 60m, ScoreReliabilityFixture.Json(
               ScoreReliabilityFixture.FlooredScoring("liquidez_corriente"),
               ScoreReliabilityFixture.CleanFacts() with
               {
                   Integrity = ScoreReliabilityFixture.FailedIntegrity(["liquidez_corriente", "prueba_acida"]),
               }));

    private static StubDecisionNotifier NotifierOf(AnalysisController sut)
    {
        var field = typeof(AnalysisController)
            .GetField("_decisionNotifier", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return Assert.IsType<StubDecisionNotifier>(field!.GetValue(sut));
    }

    private static JsonElement ReviewOf(string? newValueJson)
    {
        Assert.NotNull(newValueJson);
        return JsonDocument.Parse(newValueJson!).RootElement.GetProperty("ReliabilityReview");
    }

    /// <summary>A 409 of the gate, with its extensions, and proof that nothing was written.</summary>
    private static ProblemDetails AssertRefusedAndUntouched(
        IActionResult result, string errorCode, AnalysisController sut, StubAnalysisRepository analyses,
        StubAuditService audit, StubAnalysisApprovalQuorumService quorum)
    {
        var problem = CodedProblemOf(result, 409, errorCode);

        Assert.False(analyses.UpdateCalled);
        Assert.False(audit.LogCalled);
        Assert.Equal(0, NotifierOf(sut).CallCount);
        Assert.Equal(0, quorum.VoteCallCount);
        Assert.Equal(0, quorum.DiscardCallCount);
        Assert.Equal(0, quorum.VetoCallCount);
        return problem;
    }

    private static string[] ReasonCodesOf(ProblemDetails problem)
        => Assert.IsType<string[]>(problem.Extensions["reasonCodes"]);

    // ── Refusals ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ScoreReliabilityLevels.Provisional, Codes.IntegrityUnverified)]
    [InlineData(ScoreReliabilityLevels.None,        Codes.ScoreUnavailable)]
    public async Task Gate_Approval_WithoutAck_OnProvisionalOrNone_Is409_AndWritesNothing(string level, string reasonCode)
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        UseLevelRow(level, results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 400_000m, "ARS", acknowledgedCaveats: null), CancellationToken.None);

        var problem = AssertRefusedAndUntouched(result, "caveats_not_acknowledged", sut, analyses, audit, quorum);
        Assert.Equal(level, problem.Extensions["reliabilityLevel"]);
        Assert.Contains(reasonCode, ReasonCodesOf(problem));
        Assert.Equal(
            "Para decidir sobre un análisis con advertencias en la confiabilidad del score, confirmá que las revisaste.",
            problem.Detail);
        Assert.Equal("PendingReview", analyses.AnalysisToReturn!.Status);
    }

    [Fact]
    public async Task Gate_PreApproval_WithoutAck_Is409_AndNeitherWritesNorWipesVotes()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        UseProvisionalRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        // Over the analyst's limit: without the gate this would be a pre-approval, which wipes the votes first.
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 100_000m), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 400_000m, "ARS", acknowledgedCaveats: null), CancellationToken.None);

        AssertRefusedAndUntouched(result, "caveats_not_acknowledged", sut, analyses, audit, quorum);
        Assert.Null(analyses.AnalysisToReturn!.PreApprovedBy);
    }

    [Fact]
    public async Task Gate_ConfirmerVote_WithoutAck_Is409_AndCastsNoVote()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 900_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        UseProvisionalRow(results.ResultToReturn!, analyses.AnalysisToReturn);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.Empty(), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 750_000m, "ARS", acknowledgedCaveats: null), CancellationToken.None);

        AssertRefusedAndUntouched(result, "caveats_not_acknowledged", sut, analyses, audit, quorum);
        Assert.Equal("PreApproved", analyses.AnalysisToReturn.Status);
    }

    [Fact]
    public async Task Gate_IntegrityFailed_WithAckButNoNote_Is409FiguresReviewRequired()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit,
                           quorumService: quorum);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, Request("Approved"), CancellationToken.None);

        var problem = AssertRefusedAndUntouched(result, "figures_review_required", sut, analyses, audit, quorum);
        Assert.Equal(ScoreReliabilityLevels.Provisional, problem.Extensions["reliabilityLevel"]);
        Assert.Contains(Codes.IntegrityFailed, ReasonCodesOf(problem));
        Assert.Contains(Codes.CriticalFloorUnverified, ReasonCodesOf(problem));
        Assert.Equal(
            "Las cifras del balance no cierran. Para decidir, corregilas o dejá constancia de que las verificaste contra el documento.",
            problem.Detail);
    }

    [Fact]
    public async Task Gate_IntegrityFailed_WithoutAck_AsksForTheAckFirst()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", acknowledgedCaveats: null, figuresReviewNote: Note),
            CancellationToken.None);

        CodedProblemOf(result, 409, "caveats_not_acknowledged");
    }

    [Fact]
    public async Task Gate_NoteShorterThan20CharactersOnceTrimmed_Is409()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);
        var nineteen = "   " + new string('x', 19) + "   ";

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null, figuresReviewNote: nineteen), CancellationToken.None);

        var problem = CodedProblemOf(result, 409, "figures_review_required");
        Assert.Equal(RejectionReasonDetail, problem.Detail);
        Assert.False(analyses.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    // ── A rejection on figures that failed integrity: its reason can be the note ──

    private const string RejectionReasonDetail =
        "Las cifras del balance no cierran. Para rechazar, contá en el motivo por qué rechazás y cómo verificaste las cifras (mínimo 20 caracteres).";

    [Fact]
    public async Task Gate_IntegrityFailedRejection_WithAShortReasonAndNoNote_Is409_WithTheRejectionDetail()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit,
                           quorumService: quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Rejected", null, null, acknowledgedCaveats: null, reviewNotes: "  No cierra el pasivo  "),   // 19 once trimmed
            CancellationToken.None);

        var problem = AssertRefusedAndUntouched(result, "figures_review_required", sut, analyses, audit, quorum);
        Assert.Equal(RejectionReasonDetail, problem.Detail);
        Assert.Equal(ScoreReliabilityLevels.Provisional, problem.Extensions["reliabilityLevel"]);
        Assert.Contains(Codes.IntegrityFailed, ReasonCodesOf(problem));
    }

    [Fact]
    public async Task Gate_IntegrityFailedRejection_With20CharacterReason_Passes_WithoutAck_AndAuditsTheReasonAsTheNote()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);
        var reason = "  Rechazo: el pasivo no cierra contra la hoja 4.\n";

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Rejected", null, null, acknowledgedCaveats: null, reviewNotes: reason),
            CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Rejected", analyses.Updated!.Status);
        Assert.Equal("Analysis.Rejected", audit.Action);
        Assert.True(response.DecisionRequirements!.RejectionReasonRequired);

        var review = ReviewOf(audit.NewValueJson);
        Assert.Equal(reason.Trim(), review.GetProperty("FiguresReviewNote").GetString());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("AcknowledgedCaveats").ValueKind);
        // The reason is still the reviewer's notes, as it always was.
        Assert.Equal(reason, analyses.Updated.ReviewNotes);
    }

    [Fact]
    public async Task Gate_IntegrityFailedRejection_With20CharacterFiguresNote_Passes_AndTheNoteWinsOverTheReason()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Rejected", null, null, acknowledgedCaveats: null, figuresReviewNote: Note, reviewNotes: "Otro motivo cualquiera, largo."),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", analyses.Updated!.Status);
        Assert.Equal(Note, ReviewOf(audit.NewValueJson).GetProperty("FiguresReviewNote").GetString());
    }

    [Fact]
    public async Task Gate_IntegrityFailedRejection_WithA1001CharacterReasonAsTheNote_Is400OnReviewNotes_AndWritesNothing()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Rejected", null, null, reviewNotes: new string('x', 1001)),
            CancellationToken.None);

        var problem = CodedProblemOf(result, 400, "validation_error");
        var errors  = JsonSerializer.SerializeToElement(problem.Extensions["errors"]);
        Assert.Equal("reviewNotes", errors[0].GetProperty("field").GetString());
        Assert.False(analyses.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task Gate_UngatedRejection_KeepsAcceptingALongReason()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseCaveatRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);
        var longReason = new string('x', 1500);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null, reviewNotes: longReason), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(longReason, analyses.Updated!.ReviewNotes);
        Assert.Equal(JsonValueKind.Null, ReviewOf(audit.NewValueJson).GetProperty("FiguresReviewNote").ValueKind);
    }

    [Fact]
    public async Task Gate_IntegrityFailedApproval_WithOnlyAReason_Is409_TheReasonDoesNotCountForAnApproval()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit,
                           quorumService: quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", reviewNotes: Note), CancellationToken.None);

        var problem = AssertRefusedAndUntouched(result, "figures_review_required", sut, analyses, audit, quorum);
        Assert.Equal(
            "Las cifras del balance no cierran. Para decidir, corregilas o dejá constancia de que las verificaste contra el documento.",
            problem.Detail);
    }

    [Fact]
    public async Task Gate_NoteOf1001Characters_Is400OnFiguresReviewNote_AndWritesNothing()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", figuresReviewNote: new string('x', 1001)), CancellationToken.None);

        var problem = CodedProblemOf(result, 400, "validation_error");
        var errors  = JsonSerializer.SerializeToElement(problem.Extensions["errors"]);
        Assert.Equal("figuresReviewNote", errors[0].GetProperty("field").GetString());
        Assert.False(analyses.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    // ── Passes ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_Approval_WithAck_OnCaveats_Passes_AndTheAuditRowRecordsTheReview()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseCaveatRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, Request("Approved"), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.Equal("Analysis.Approved", audit.Action);
        Assert.False(response.DecisionRequirements!.AcknowledgeCaveats);
        Assert.False(response.DecisionRequirements.FiguresReviewNote);
        Assert.False(response.DecisionRequirements.RejectionReasonRequired);

        var review = ReviewOf(audit.NewValueJson);
        Assert.True(review.GetProperty("AcknowledgedCaveats").GetBoolean());
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, review.GetProperty("ReliabilityLevel").GetString());
        Assert.Equal([Codes.MacroImputed], review.GetProperty("ReasonCodes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("6", review.GetProperty("PolicyVersion").GetString());  // policy 6: a column mix is not a presentation
        Assert.Equal(JsonValueKind.Null, review.GetProperty("FiguresReviewNote").ValueKind);
    }

    [Fact]
    public async Task Gate_Approval_WithoutAck_OnCaveats_Passes_AndTheAuditRowRecordsTheLevel()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        UseCaveatRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m));

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 400_000m, "ARS", acknowledgedCaveats: null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.Equal("Analysis.Approved", audit.Action);

        var review = ReviewOf(audit.NewValueJson);
        Assert.Equal(JsonValueKind.Null, review.GetProperty("AcknowledgedCaveats").ValueKind);
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, review.GetProperty("ReliabilityLevel").GetString());
    }

    [Theory]
    [InlineData(ScoreReliabilityLevels.WithCaveats, null)]
    [InlineData(ScoreReliabilityLevels.WithCaveats, false)]
    [InlineData(ScoreReliabilityLevels.Provisional, null)]
    [InlineData(ScoreReliabilityLevels.Provisional, false)]
    [InlineData(ScoreReliabilityLevels.None,        null)]
    [InlineData(ScoreReliabilityLevels.None,        false)]
    public async Task Gate_Rejection_WithoutAck_Passes_AtEveryLevel(string level, bool? acknowledgedCaveats)
    {
        var (results, analyses) = Fixture("PendingReview");
        UseLevelRow(level, results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null, acknowledgedCaveats: acknowledgedCaveats),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", analyses.Updated!.Status);
        Assert.Equal("Analysis.Rejected", audit.Action);
        Assert.Equal(level, ReviewOf(audit.NewValueJson).GetProperty("ReliabilityLevel").GetString());
    }

    [Fact]
    public async Task Gate_IntegrityFailed_With20CharacterNote_Passes_AndStoresTheTrimmedNote()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);
        var twenty = new string('y', 20);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null, figuresReviewNote: "  " + twenty + "\n"),
            CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Rejected", analyses.Updated!.Status);
        Assert.Equal("Analysis.Rejected", audit.Action);
        Assert.True(response.DecisionRequirements!.FiguresReviewNote);

        var review = ReviewOf(audit.NewValueJson);
        Assert.Equal(twenty, review.GetProperty("FiguresReviewNote").GetString());
        Assert.Equal(ScoreReliabilityLevels.Provisional, review.GetProperty("ReliabilityLevel").GetString());
        Assert.Contains(Codes.CriticalFloorUnverified, review.GetProperty("ReasonCodes").EnumerateArray().Select(e => e.GetString()));
        // The note is the gate's record, not the reviewer's notes.
        Assert.Null(analyses.Updated.ReviewNotes);
    }

    [Fact]
    public async Task Gate_Confirmation_AuditRow_CarriesTheAmountsAndTheReview()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 900_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.Empty(), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 750_000m, "ARS", figuresReviewNote: Note), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, quorum.VoteCallCount);
        Assert.Equal("Analysis.Confirmed", audit.Action);

        var root = JsonDocument.Parse(audit.NewValueJson!).RootElement;
        Assert.True(root.TryGetProperty("Amount", out _));
        Assert.Equal(Note, root.GetProperty("ReliabilityReview").GetProperty("FiguresReviewNote").GetString());
    }

    [Fact]
    public async Task Gate_VoteBelowQuorum_ApprovalVotedRow_CarriesTheReview()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 900_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        UseCaveatRow(results.ResultToReturn!, analyses.AnalysisToReturn);
        var audit  = new StubAuditService();
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Outcome = ApprovalVoteOutcome.Pending,
            Tally   = StubAnalysisApprovalQuorumService.Quorum(2, caller.UserId),
        };
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.Empty(), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 750_000m, "ARS"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var (action, newValueJson) = Assert.Single(audit.Calls);
        Assert.Equal("Analysis.ApprovalVoted", action);
        var root = JsonDocument.Parse(newValueJson!).RootElement;
        Assert.Equal(1, root.GetProperty("Approved").GetInt32());
        Assert.True(root.GetProperty("ReliabilityReview").GetProperty("AcknowledgedCaveats").GetBoolean());
    }

    // ── Never gated ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_ReturnForRework_IsNeverGated()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy = Guid.NewGuid();
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit, caller,
                           StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("PendingReview", null, null, acknowledgedCaveats: null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PendingReview", analyses.Updated!.Status);
        Assert.Equal("Analysis.ReturnedToReview", audit.Action);
        Assert.Null(audit.NewValueJson);
    }

    [Fact]
    public async Task Gate_PersonaHumana_IsNeverGated_AndHasNoRequirements()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var persona = PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller,
                           StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m), personAnalyses: persona);
        var request = PersonaApproval(400_000m);
        request.AcknowledgedCaveats = null;

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, request, CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Null(response.ScoreReliability);
        Assert.Null(response.DecisionRequirements);
    }

    [Fact]
    public async Task Gate_CompleteScore_IsNeverGated_AndStillRecordsTheLevel()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseCompleteRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", acknowledgedCaveats: null), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(ScoreReliabilityLevels.Complete, response.ScoreReliability!.Level);
        Assert.False(response.DecisionRequirements!.AcknowledgeCaveats);
        Assert.False(response.DecisionRequirements.FiguresReviewNote);

        var review = ReviewOf(audit.NewValueJson);
        Assert.Equal(ScoreReliabilityLevels.Complete, review.GetProperty("ReliabilityLevel").GetString());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("AcknowledgedCaveats").ValueKind);
        Assert.Empty(review.GetProperty("ReasonCodes").EnumerateArray());
    }

    // ── The note never reaches a log ─────────────────────────────────────────────

    [Fact]
    public async Task Gate_TheNote_NeverReachesALogLine_RefusedOrAccepted()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var logger = new RecordingLogger();
        var audit  = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit,
                           logger: logger);

        // Refused (shorter than 20 once trimmed) …
        var shortNote = "Revisé hoja 4.";
        CodedProblemOf(await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", figuresReviewNote: shortNote), CancellationToken.None),
            409, "figures_review_required");

        // … then accepted.
        Assert.IsType<OkObjectResult>(await sut.DecideAsync(
            results.ResultToReturn.ResultId, Request("Approved", figuresReviewNote: Note), CancellationToken.None));

        Assert.NotEmpty(logger.Lines);
        Assert.Contains(logger.Lines, l => l.Contains("noteLength=14", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Contains($"figuresReviewNoteLength={Note.Length}", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(shortNote, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(Note, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains("hoja 4", StringComparison.Ordinal));

        // The audit row is where it lives.
        Assert.Equal(Note, ReviewOf(audit.NewValueJson).GetProperty("FiguresReviewNote").GetString());
    }

    [Fact]
    public async Task Gate_TheRejectionReason_NeverReachesALogLine_RefusedCappedOrAccepted()
    {
        var (results, analyses) = Fixture("PendingReview");
        UseIntegrityFailedRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var logger = new RecordingLogger();
        var audit  = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(), audit,
                           logger: logger);

        // Refused (shorter than 20 once trimmed) …
        var shortReason = "Pasivo mal leído.";
        CodedProblemOf(await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null, reviewNotes: shortReason), CancellationToken.None),
            409, "figures_review_required");

        // … over the cap …
        var longReason = "Motivo extenso " + new string('z', 1000);
        CodedProblemOf(await sut.DecideAsync(
            results.ResultToReturn.ResultId, Request("Rejected", null, null, reviewNotes: longReason), CancellationToken.None),
            400, "validation_error");

        // … then accepted.
        Assert.IsType<OkObjectResult>(await sut.DecideAsync(
            results.ResultToReturn.ResultId, Request("Rejected", null, null, reviewNotes: Note), CancellationToken.None));

        Assert.Contains(logger.Lines, l => l.Contains($"noteLength={shortReason.Length}", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Contains($"figuresReviewNoteLength={Note.Length}", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(shortReason, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains("Motivo extenso", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(Note, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains("hoja 4", StringComparison.Ordinal));
    }
}
