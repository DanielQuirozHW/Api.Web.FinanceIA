// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <c>extractionIntegrity.verified</c>, <c>notVerifiedReasons</c>, the unit fields and
/// <c>fiscalYear</c>, as read from a persisted BalanceJson. <c>passed</c> keeps its meaning: verified
/// is a separate statement about whether the structural checks could run at all.
/// </summary>
public sealed class ExtractionIntegrityReaderTests
{
    private static string Blob(string checks, string skipped = "[]", bool passed = true, string extra = "") =>
        "{\"assets\": 100, " + extra +
        " \"integrity\": {\"passed\": " + (passed ? "true" : "false") + ", \"checks\": " + checks +
        ", \"skipped\": " + skipped +
        ", \"affectedRatios\": [], \"amountsUnit\": \"miles\", \"amountsUnitAssumed\": false}}";

    private const string AllThreeRan =
        """
        [{"name": "assets_components", "passed": true},
         {"name": "liabilities_components", "passed": true},
         {"name": "accounting_equation", "passed": true}]
        """;

    [Fact]
    public void All_three_structural_checks_ran_is_verified()
    {
        var dto = ExtractionIntegrityReader.Read(Blob(AllThreeRan))!;

        Assert.True(dto.Verified);
        Assert.Empty(dto.NotVerifiedReasons);
        Assert.True(dto.Passed);
    }

    [Fact]
    public void A_failed_structural_check_still_ran_so_the_balance_is_verified_and_not_passed()
    {
        var checks =
            """
            [{"name": "assets_components", "passed": false},
             {"name": "liabilities_components", "passed": true},
             {"name": "accounting_equation", "passed": true}]
            """;

        var dto = ExtractionIntegrityReader.Read(Blob(checks, passed: false))!;

        Assert.True(dto.Verified);
        Assert.False(dto.Passed);
        Assert.Equal(["assets_components"], dto.FailedChecks);
    }

    [Fact]
    public void One_skipped_structural_check_is_not_verified_with_its_reason_while_passed_stays_true()
    {
        var checks =
            """
            [{"name": "liabilities_components", "passed": true},
             {"name": "accounting_equation", "passed": true}]
            """;

        var dto = ExtractionIntegrityReader.Read(Blob(checks, skipped: """["assets_components"]"""))!;

        Assert.False(dto.Verified);
        Assert.Equal(["assets_components_skipped"], dto.NotVerifiedReasons);
        Assert.True(dto.Passed);   // unchanged semantics: a skipped check does not count against passed
    }

    [Fact]
    public void A_report_whose_checks_were_all_skipped_passes_vacuously_and_is_not_verified()
    {
        var dto = ExtractionIntegrityReader.Read(Blob("[]",
            skipped: """["assets_components", "liabilities_components", "accounting_equation"]"""))!;

        Assert.True(dto.Passed);
        Assert.False(dto.Verified);
        Assert.Equal(
            ["assets_components_skipped", "liabilities_components_skipped", "accounting_equation_skipped"],
            dto.NotVerifiedReasons);
    }

    [Fact]
    public void A_report_produced_by_BalanceIntegrityService_over_nothing_extracted_is_not_verified()
    {
        // The real service, end to end through the persisted JSON shape: everything skipped.
        var report = new BalanceIntegrityService()
            .Evaluate(new ExtractedBalance { Current = new BalanceAccounts() }).Report;
        var json = System.Text.Json.JsonSerializer.Serialize(new { integrity = report });

        var dto = ExtractionIntegrityReader.Read(json)!;

        Assert.True(report.Passed);   // the report's own semantics are unchanged
        Assert.False(dto.Verified);
        Assert.Equal(3, dto.NotVerifiedReasons.Count);
    }

    [Fact]
    public void A_null_report_is_unavailable_not_verified_and_not_passed()
    {
        var dto = ExtractionIntegrityReader.Read("""{"assets": 1, "integrity": null}""")!;

        Assert.False(dto.Passed);
        Assert.False(dto.Verified);
        Assert.Equal([ExtractionIntegrityResponse.IntegrityUnavailableReason], dto.NotVerifiedReasons);
        Assert.Empty(dto.FailedChecks);
        Assert.Empty(dto.AffectedRatios);
        Assert.Null(dto.AmountsUnit);
        Assert.Null(dto.AmountsUnitAssumed);
        Assert.Null(dto.SecondPassRan);
    }

    [Fact]
    public void A_blob_with_no_integrity_key_stays_unknown()
    {
        Assert.Null(ExtractionIntegrityReader.Read("""{"assets": 1, "fiscal_year_detected": 2024}""", 2024));
    }

    [Fact]
    public void The_unit_fields_come_from_the_report()
    {
        var dto = ExtractionIntegrityReader.Read(
            """{"integrity": {"passed": true, "checks": [], "skipped": [], "affectedRatios": [], "amountsUnit": "pesos", "amountsUnitAssumed": true}}""")!;

        Assert.Equal("pesos", dto.AmountsUnit);
        Assert.True(dto.AmountsUnitAssumed);
    }

    [Theory]
    [InlineData("\"document_kind\": \"trial_balance\",", "trial_balance")]
    [InlineData("\"document_kind\": \" Trial_Balance \",", "trial_balance")]
    [InlineData("\"document_kind\": \"financial_statements\",", null)]
    [InlineData("\"document_kind\": 3,", null)]
    [InlineData("", null)]
    public void The_document_kind_comes_from_the_blob_root(string member, string? expected)
    {
        const string reportJson = "\"integrity\": {\"passed\": true, \"checks\": [], \"skipped\": [], \"affectedRatios\": []}}";
        var report      = ExtractionIntegrityReader.Read("{" + member + reportJson)!;
        var unavailable = ExtractionIntegrityReader.Read("{" + member + "\"integrity\": null}")!;

        Assert.Equal(expected, report.DocumentKind);
        Assert.Equal(expected, unavailable.DocumentKind);
    }

    // ── fiscalYear ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"fiscal_year_detected\": 2024,", 2024, "match")]
    [InlineData("\"fiscal_year_detected\": 2023,", 2023, "mismatch")]
    [InlineData("\"fiscal_year_detected\": 2025,", 2025, "mismatch")]
    [InlineData("\"fiscal_year_detected\": null,", null, "not_detected")]
    [InlineData("", null, "not_detected")]
    public void The_fiscal_year_read_is_compared_with_the_requested_one(string extra, int? detected, string status)
    {
        var dto = ExtractionIntegrityReader.Read(Blob(AllThreeRan, extra: extra), requestedFiscalYear: 2024)!;

        var fiscalYear = Assert.IsType<ExtractionFiscalYearResponse>(dto.FiscalYear);
        Assert.Equal(2024, fiscalYear.Requested);
        Assert.Equal(detected, fiscalYear.Detected);
        Assert.Equal(status, fiscalYear.Status);
    }

    [Fact]
    public void Without_a_requested_fiscal_year_there_is_no_fiscal_year_block()
    {
        var dto = ExtractionIntegrityReader.Read(Blob(AllThreeRan, extra: "\"fiscal_year_detected\": 2024,"))!;

        Assert.Null(dto.FiscalYear);
    }

    [Fact]
    public void A_null_report_still_carries_the_fiscal_year_check()
    {
        var dto = ExtractionIntegrityReader.Read("""{"fiscal_year_detected": 2023, "integrity": null}""", 2024)!;

        Assert.Equal("mismatch", dto.FiscalYear!.Status);
    }

    // ── integrity.extraction.embeddedAttachment ──────────────────────────────────

    [Fact]
    public void An_unread_embedded_attachment_is_read_from_the_extraction_node()
    {
        const string blob =
            """{"assets":100,"integrity":{"passed":true,"checks":[],"extraction":{"secondPassRan":false,"keptPass":"first","embeddedAttachment":"unread"}}}""";

        var dto = ExtractionIntegrityReader.Read(blob)!;

        Assert.Equal("unread", dto.EmbeddedAttachment);
        Assert.Equal("first", dto.KeptPass);
    }

    [Fact]
    public void No_embedded_attachment_key_reads_as_null()
    {
        const string blob =
            """{"assets":100,"integrity":{"passed":true,"checks":[],"extraction":{"secondPassRan":false,"keptPass":"first"}}}""";

        Assert.Null(ExtractionIntegrityReader.Read(blob)!.EmbeddedAttachment);
        Assert.Null(ExtractionIntegrityReader.Read(Blob(AllThreeRan))!.EmbeddedAttachment);
        Assert.Null(ExtractionIntegrityReader.Read(blob)!.EmbeddedAttachmentSource);
        Assert.Null(ExtractionIntegrityReader.Read(Blob(AllThreeRan))!.EmbeddedAttachmentSource);
    }

    [Fact]
    public void The_embedded_attachment_source_is_read_from_the_extraction_node()
    {
        const string blob =
            """{"assets":100,"integrity":{"passed":true,"checks":[],"extraction":{"keptPass":"first","embeddedAttachment":"unread","embeddedAttachmentSource":"orphaned_filespec"}}}""";

        var dto = ExtractionIntegrityReader.Read(blob)!;

        Assert.Equal("unread", dto.EmbeddedAttachment);
        Assert.Equal("orphaned_filespec", dto.EmbeddedAttachmentSource);
    }

    [Fact]
    public void The_embedded_attachment_source_is_camelCase_on_the_wire()
    {
        var dto  = new ExtractionIntegrityResponse
        {
            Passed = true, FailedChecks = [], AffectedRatios = [], Verified = true, NotVerifiedReasons = [],
            EmbeddedAttachmentSource = "orphaned_filespec",
        };

        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"embeddedAttachmentSource\":\"orphaned_filespec\"", json);
    }

    // ── presentationOnly (reliability policy 5) ─────────────────────────────────

    private const string NetProfitFailed =
        """
        [{"name": "assets_components", "passed": true},
         {"name": "liabilities_components", "passed": true},
         {"name": "accounting_equation", "passed": true},
         {"name": "net_profit_reconciliation", "passed": false}]
        """;

    private const string NetProfitAndItemsFailed =
        """
        [{"name": "assets_components", "passed": true},
         {"name": "liabilities_components", "passed": true},
         {"name": "accounting_equation", "passed": true},
         {"name": "current_assets_items", "passed": false},
         {"name": "net_profit_reconciliation", "passed": false}]
        """;

    // The figures the check read: expected 9.876.058 (15.193.935 − 5.317.877), reported 12.280.839 — a gap.
    private const string PresentationGapFigures =
        "\"net_profit\": 12280839, \"result_before_tax\": 15193935, \"income_tax\": -5317877,";

    // The same statement with the net profit read with the wrong sign.
    private const string SignFlipFigures =
        "\"net_profit\": -9876058, \"result_before_tax\": 15193935, \"income_tax\": -5317877,";

    private const string NetProfitFailedWithSignFlip =
        """
        [{"name": "assets_components", "passed": true},
         {"name": "liabilities_components", "passed": true},
         {"name": "accounting_equation", "passed": true},
         {"name": "net_profit_reconciliation", "passed": false, "signFlip": true}]
        """;

    [Fact]
    public void Only_net_profit_failed_is_presentation_only_and_the_report_stays_truthful()
    {
        var dto = ExtractionIntegrityReader.Read(Blob(NetProfitFailed, passed: false, extra: PresentationGapFigures))!;

        Assert.True(dto.PresentationOnly);
        Assert.False(dto.NetProfitSignFlip);
        Assert.False(dto.Passed);
        Assert.Equal(["net_profit_reconciliation"], dto.FailedChecks);
        Assert.True(dto.Verified);
    }

    [Fact]
    public void A_recorded_sign_flip_is_a_misread_not_a_presentation()
    {
        // The record wins, even over figures that would not say so.
        var dto = ExtractionIntegrityReader.Read(Blob(NetProfitFailedWithSignFlip, passed: false, extra: PresentationGapFigures))!;

        Assert.True(dto.NetProfitSignFlip);
        Assert.False(dto.PresentationOnly);
        Assert.Equal(["net_profit_reconciliation"], dto.FailedChecks);
    }

    [Fact]
    public void A_blob_without_the_record_is_rejudged_from_its_stored_figures()
    {
        var flipped = ExtractionIntegrityReader.Read(Blob(NetProfitFailed, passed: false, extra: SignFlipFigures))!;
        Assert.True(flipped.NetProfitSignFlip);
        Assert.False(flipped.PresentationOnly);

        var gap = ExtractionIntegrityReader.Read(Blob(NetProfitFailed, passed: false, extra: PresentationGapFigures))!;
        Assert.False(gap.NetProfitSignFlip);
        Assert.True(gap.PresentationOnly);
    }

    [Fact]
    public void A_blob_without_the_record_or_the_figures_is_unknown_and_not_presentation_only()
    {
        var dto = ExtractionIntegrityReader.Read(Blob(NetProfitFailed, passed: false))!;

        Assert.Null(dto.NetProfitSignFlip);
        Assert.False(dto.PresentationOnly);
    }

    [Fact]
    public void A_passed_net_profit_check_carries_no_sign_flip_verdict()
        => Assert.Null(ExtractionIntegrityReader.Read(Blob(AllThreeRan, extra: SignFlipFigures))!.NetProfitSignFlip);

    [Fact]
    public void Net_profit_beside_a_structure_failure_is_not_presentation_only()
        => Assert.False(ExtractionIntegrityReader.Read(Blob(NetProfitAndItemsFailed, passed: false, extra: PresentationGapFigures))!.PresentationOnly);

    [Fact]
    public void A_passed_or_unavailable_report_is_not_presentation_only()
    {
        Assert.False(ExtractionIntegrityReader.Read(Blob(AllThreeRan))!.PresentationOnly);
        Assert.False(ExtractionIntegrityReader.Read("""{"integrity": null}""")!.PresentationOnly);
    }
    // ── column mix (reliability policy 6) ────────────────────────────────────────
    // Synthetic: the current column reconciles to 650.000 (1.000.000 − 350.000); the comparative column to 520.000
    // (800.000 − 280.000). A mixed reading took 800.000 / −280.000 beside the current 650.000.

    private static string NetProfitFailedWithColumnMix(bool columnMix) =>
        """
        [{"name": "assets_components", "passed": true},
         {"name": "liabilities_components", "passed": true},
         {"name": "accounting_equation", "passed": true},
         {"name": "net_profit_reconciliation", "passed": false, "columnMix": 
        """ + (columnMix ? "true" : "false") + "}]";

    private const string MixedFigures =
        "\"net_profit\": 650000, \"result_before_tax\": 800000, \"income_tax\": -280000, \"prior_year\": {\"net_profit\": 520000},";

    [Fact]
    public void A_recorded_column_mix_is_a_misread_not_a_presentation()
    {
        var dto = ExtractionIntegrityReader.Read(Blob(NetProfitFailedWithColumnMix(true), passed: false, extra: MixedFigures))!;

        Assert.True(dto.NetProfitColumnMix);
        Assert.False(dto.NetProfitSignFlip);
        Assert.False(dto.PresentationOnly);
        Assert.Equal(["net_profit_reconciliation"], dto.FailedChecks);
    }

    [Fact]
    public void A_recorded_no_column_mix_stays_a_presentation()
    {
        var dto = ExtractionIntegrityReader.Read(Blob(NetProfitFailedWithColumnMix(false), passed: false, extra: PresentationGapFigures))!;

        Assert.False(dto.NetProfitColumnMix);
        Assert.True(dto.PresentationOnly);
    }

    [Fact]
    public void A_blob_without_the_column_mix_record_is_not_rejudged_even_when_its_prior_year_would_match()
    {
        // Written before policy 6: no "columnMix" on the check, although the stored prior_year WOULD reveal the mix.
        // The verdict stays the one it always had (presentation), deliberately.
        var dto = ExtractionIntegrityReader.Read(Blob(NetProfitFailed, passed: false, extra: MixedFigures))!;

        Assert.Null(dto.NetProfitColumnMix);
        Assert.False(dto.NetProfitSignFlip);
        Assert.True(dto.PresentationOnly);
    }

    [Fact]
    public void A_passed_net_profit_check_carries_no_column_mix_verdict()
    {
        const string passedWithRecord =
            """
            [{"name": "assets_components", "passed": true},
             {"name": "liabilities_components", "passed": true},
             {"name": "accounting_equation", "passed": true},
             {"name": "net_profit_reconciliation", "passed": true, "columnMix": true}]
            """;

        Assert.Null(ExtractionIntegrityReader.Read(Blob(passedWithRecord, extra: MixedFigures))!.NetProfitColumnMix);
    }

    [Fact]
    public void The_column_mix_member_is_additive_on_the_wire()
    {
        var mixed  = ExtractionIntegrityReader.Read(Blob(NetProfitFailedWithColumnMix(true), passed: false, extra: MixedFigures))!;
        // The API serialises responses with the web defaults (camelCase).
        var json   = System.Text.Json.JsonSerializer.SerializeToNode(
            mixed, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.AsObject();

        // Every member the Portal reads today is still there, with the meaning it reads.
        Assert.False(json["presentationOnly"]!.GetValue<bool>());
        Assert.False(json["netProfitSignFlip"]!.GetValue<bool>());
        Assert.True(json["netProfitColumnMix"]!.GetValue<bool>());
    }
}
