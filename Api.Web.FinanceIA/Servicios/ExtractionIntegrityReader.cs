// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Reads <see cref="ExtractionIntegrityResponse"/> out of a persisted <c>BalanceJson</c>, at read
/// time. Never throws: a missing, malformed or pre-2.18 blob yields null.
/// </summary>
/// <remarks>
/// The <c>integrity</c> key takes three shapes:
/// <list type="bullet">
/// <item>absent: an analysis extracted before prompt 2.18 → null (unknown);</item>
/// <item>JSON null: written by ClaudeService when the integrity evaluation itself threw → a verdict
/// that is NOT verified (<c>integrity_unavailable</c>) and not passed, since nothing was proven;</item>
/// <item>an object: the report.</item>
/// </list>
/// </remarks>
internal static class ExtractionIntegrityReader
{
    /// <param name="balanceJson">The persisted <c>AnalysisResults.BalanceJson</c>.</param>
    /// <param name="requestedFiscalYear">
    /// <c>Analysis.FiscalYear</c>, compared with the blob's <c>fiscal_year_detected</c>. Null leaves
    /// <see cref="ExtractionIntegrityResponse.FiscalYear"/> null.
    /// </param>
    public static ExtractionIntegrityResponse? Read(string? balanceJson, int? requestedFiscalYear = null)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(balanceJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("integrity", out var integrity))
                return null;

            if (integrity.ValueKind == JsonValueKind.Null)
            {
                return new ExtractionIntegrityResponse
                {
                    Passed             = false,
                    FailedChecks       = [],
                    AffectedRatios     = [],
                    Verified           = false,
                    NotVerifiedReasons = [ExtractionIntegrityResponse.IntegrityUnavailableReason],
                    DocumentKind       = ReadDocumentKind(root),
                    FiscalYear         = ReadFiscalYear(root, requestedFiscalYear),
                };
            }

            if (integrity.ValueKind != JsonValueKind.Object
                || !integrity.TryGetProperty("passed", out var passed)
                || passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var failed = new List<string>();
            var ran    = new HashSet<string>(StringComparer.Ordinal);
            var recordedSignFlip = false;
            bool? recordedColumnMix = null;
            if (integrity.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    if (check.ValueKind != JsonValueKind.Object
                        || !check.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                        || name.GetString() is not { Length: > 0 } checkName)
                    {
                        continue;
                    }

                    ran.Add(checkName);

                    if (check.TryGetProperty("passed", out var checkPassed) && checkPassed.ValueKind == JsonValueKind.False
                        && !failed.Contains(checkName, StringComparer.Ordinal))
                    {
                        failed.Add(checkName);
                    }

                    if (string.Equals(checkName, BalanceIntegrityService.NetProfitReconciliationCheck, StringComparison.Ordinal)
                        && check.TryGetProperty("signFlip", out var signFlip) && signFlip.ValueKind == JsonValueKind.True)
                    {
                        recordedSignFlip = true;
                    }

                    // Only a FAILED check's record counts; the check is written once per report.
                    if (string.Equals(checkName, BalanceIntegrityService.NetProfitReconciliationCheck, StringComparison.Ordinal)
                        && check.TryGetProperty("passed", out var mixPassed) && mixPassed.ValueKind == JsonValueKind.False
                        && check.TryGetProperty("columnMix", out var columnMix)
                        && columnMix.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        recordedColumnMix = columnMix.GetBoolean();
                    }
                }
            }

            // A check RAN when it is listed under checks (passed or failed); one listed only under
            // skipped, or nowhere, did not.
            var notVerified = BalanceIntegrityService.VerificationChecks
                .Where(c => !ran.Contains(c))
                .Select(c => c + ExtractionIntegrityResponse.SkippedReasonSuffix)
                .ToList();

            bool?   secondPassRan      = null;
            string? keptPass           = null;
            string? embeddedAttachment = null;
            string? embeddedAttachmentSource = null;
            if (integrity.TryGetProperty("extraction", out var extraction) && extraction.ValueKind == JsonValueKind.Object)
            {
                if (extraction.TryGetProperty("secondPassRan", out var secondRan) && secondRan.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    secondPassRan = secondRan.GetBoolean();
                if (extraction.TryGetProperty("keptPass", out var kept) && kept.ValueKind == JsonValueKind.String)
                    keptPass = kept.GetString();
                if (extraction.TryGetProperty("embeddedAttachment", out var attachment) && attachment.ValueKind == JsonValueKind.String)
                    embeddedAttachment = attachment.GetString();
                if (extraction.TryGetProperty("embeddedAttachmentSource", out var source) && source.ValueKind == JsonValueKind.String)
                    embeddedAttachmentSource = source.GetString();
            }

            string? amountsUnit = integrity.TryGetProperty("amountsUnit", out var unit) && unit.ValueKind == JsonValueKind.String
                ? unit.GetString()
                : null;
            bool? amountsUnitAssumed = integrity.TryGetProperty("amountsUnitAssumed", out var assumed)
                                       && assumed.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? assumed.GetBoolean()
                : null;

            bool? netProfitSignFlip = failed.Contains(BalanceIntegrityService.NetProfitReconciliationCheck, StringComparer.Ordinal)
                ? recordedSignFlip ? true : JudgeSignFlip(root)
                : null;

            // Recorded only — never re-judged from the stored prior_year: a blob written before the verdict existed
            // stays unknown, so its verdict stays the one it always had.
            bool? netProfitColumnMix = failed.Contains(BalanceIntegrityService.NetProfitReconciliationCheck, StringComparer.Ordinal)
                ? recordedColumnMix
                : null;

            return new ExtractionIntegrityResponse
            {
                Passed             = passed.GetBoolean(),
                FailedChecks       = failed,
                PresentationOnly   = !passed.GetBoolean()
                                     && BalanceIntegrityService.FailsOnlyOnPresentation(failed, netProfitSignFlip, netProfitColumnMix),
                NetProfitSignFlip  = netProfitSignFlip,
                NetProfitColumnMix = netProfitColumnMix,
                AffectedRatios     = Strings(integrity, "affectedRatios"),
                SecondPassRan      = secondPassRan,
                KeptPass           = keptPass,
                EmbeddedAttachment = embeddedAttachment,
                EmbeddedAttachmentSource = embeddedAttachmentSource,
                Verified           = notVerified.Count == 0,
                NotVerifiedReasons = notVerified,
                AmountsUnit        = amountsUnit,
                AmountsUnitAssumed = amountsUnitAssumed,
                DocumentKind       = ReadDocumentKind(root),
                FiscalYear         = ReadFiscalYear(root, requestedFiscalYear),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A failed net_profit_reconciliation with no signFlip record — written before it was recorded, or checked and not
    // a flip — is judged by the SAME rule over the figures the check read: the blob's root net_profit,
    // result_before_tax and income_tax (pesos; the tolerance is relative anyway). Any of them missing: unknown.
    private static bool? JudgeSignFlip(JsonElement root)
        => Number(root, "net_profit") is { } netProfit
           && Number(root, "result_before_tax") is { } resultBeforeTax
           && Number(root, "income_tax") is { } incomeTax
            ? BalanceIntegrityService.IsNetProfitSignFlip(netProfit, resultBeforeTax, incomeTax)
            : null;

    private static double? Number(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    // The root document_kind ClaudeService writes for a trial balance only (prompt 2.19); any other value is
    // not one this build knows, so it reads as unknown rather than as a trial balance.
    private static string? ReadDocumentKind(JsonElement root)
        => root.TryGetProperty("document_kind", out var kind) && kind.ValueKind == JsonValueKind.String
           && TrialBalanceNormalizer.IsTrialBalance(kind.GetString())
            ? TrialBalanceNormalizer.DocumentKind
            : null;

    private static ExtractionFiscalYearResponse? ReadFiscalYear(JsonElement root, int? requested)
    {
        if (requested is not { } requestedYear)
            return null;

        int? detected = root.TryGetProperty("fiscal_year_detected", out var year)
                        && year.ValueKind == JsonValueKind.Number
                        && year.TryGetInt32(out var detectedYear)
            ? detectedYear
            : null;

        return new ExtractionFiscalYearResponse
        {
            Requested = requestedYear,
            Detected  = detected,
            Status    = FiscalYearCheck.Evaluate(requestedYear, detected),
        };
    }

    private static List<string> Strings(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList()
            : [];
}
