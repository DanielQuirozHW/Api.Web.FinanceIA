// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// The outcome of <see cref="BalanceCorrectionApplier.Apply"/>. Exactly one of three shapes: the stored
/// balance is unreadable; the corrections fail the balance rules (<see cref="Errors"/>); or the corrected
/// BalanceJson with what changed.
/// </summary>
public sealed record BalanceCorrectionApplication
{
    /// <summary>The stored BalanceJson is not a JSON object: nothing can be corrected.</summary>
    public bool Unreadable { get; init; }

    /// <summary>The balance-rule errors (<see cref="BalanceCorrectionValidator.ValidateAgainstBalance"/>).</summary>
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    /// <summary>The whole corrected BalanceJson, ready to persist; null on a refusal.</summary>
    public string? BalanceJson { get; init; }

    /// <summary>Each correction with the value right before it.</summary>
    public IReadOnlyList<AppliedBalanceCorrection> Applied { get; init; } = [];

    /// <summary>The re-evaluated integrity passed every check that ran; null when it could not be evaluated.</summary>
    public bool? IntegrityPassed { get; init; }

    /// <summary>The names of the re-evaluated checks that failed, in check order; null when it could not be evaluated.</summary>
    public IReadOnlyList<string>? IntegrityFailedChecks { get; init; }

    /// <summary>Whether a failed net_profit_reconciliation is a sign flip; null when it did not fail or could not be evaluated.</summary>
    public bool? IntegrityNetProfitSignFlip { get; init; }

    /// <summary>
    /// Whether the re-evaluated net_profit_reconciliation failed as a column mix (BalanceIntegrityService.NetProfitColumnMixOf):
    /// a correction re-judges the figures fresh, like the sign flip. Null when that check did not fail or could not be judged.
    /// </summary>
    public bool? IntegrityNetProfitColumnMix { get; init; }

    /// <summary>True when the integrity re-evaluation threw and <c>integrity</c> was written as null.</summary>
    public bool IntegrityUnavailable { get; init; }

    /// <summary>True when the corrected balance is ready to persist.</summary>
    public bool IsApplied => BalanceJson is not null;
}

/// <summary>
/// The outcome of <see cref="BalanceCorrectionApplier.DryRun"/>: the stored balance is unreadable, the
/// entries fail the balance rules, or the integrity report the corrected figures WOULD get (null when its
/// evaluation threw, which a caller must take as "integrity unavailable", never as a pass).
/// </summary>
public sealed record BalanceCorrectionDryRun
{
    /// <summary>The stored BalanceJson is not a JSON object.</summary>
    public bool Unreadable { get; init; }

    /// <summary>The balance-rule errors of the entries.</summary>
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    /// <summary>The re-evaluated report; null on a refusal or when the evaluation threw.</summary>
    public BalanceIntegrityReport? Report { get; init; }
}

/// <summary>
/// Applies a validated balance correction to a COPY of the persisted BalanceJson, in memory. Pure and
/// static: nothing is read from or written to the database here, so a refusal further down the re-score
/// (BCRA unavailable, a lost race) leaves the stored balance exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// It works on a <see cref="JsonNode"/> tree, so every key it does not own — <c>cash_flow</c> and its
/// reconciliation, <c>prior_year</c>, <c>balanced</c>, the labels — keeps its value. It owns three things:
/// the corrected figures; the top-level <c>manual_corrections</c> array, which gains one entry per
/// correction (<c>{path, original, corrected, by, at, reason, origin}</c>, where <c>original</c> is the FIRST
/// original of that path, so a figure corrected twice still says what was read); and the
/// <c>integrity</c> node, re-evaluated over the corrected figures by <see cref="IBalanceIntegrityService"/>.
/// </para>
/// <para>
/// The re-evaluated report keeps the reading's provenance (<c>extraction</c>), its unit and the
/// confidence the model reported; the effective confidence is re-capped exactly as ClaudeService caps it;
/// and <c>manuallyCorrected: true</c> marks it. Figures are in pesos throughout: amounts_scale was applied
/// at extraction, so nothing is scaled again — it only tells the checks which unit to quote.
/// </para>
/// </remarks>
public static class BalanceCorrectionApplier
{
    /// <summary>The top-level BalanceJson key holding the correction trail.</summary>
    public const string ManualCorrectionsKey = "manual_corrections";

    private const string IntegrityKey = "integrity";

    /// <summary>Applies <paramref name="command"/> to <paramref name="balanceJson"/>.</summary>
    /// <param name="balanceJson">The persisted AnalysisResults.BalanceJson.</param>
    /// <param name="command">The validated request.</param>
    /// <param name="by">Users.UserId of the analyst.</param>
    /// <param name="atUtc">The correction's instant — the re-score's RescoredAt.</param>
    /// <param name="integrity">The integrity checks the extraction runs.</param>
    public static BalanceCorrectionApplication Apply(
        string? balanceJson, BalanceCorrectionCommand command, Guid by, DateTime atUtc, IBalanceIntegrityService integrity)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(integrity);

        var root = Parse(balanceJson);
        if (root is null)
            return new BalanceCorrectionApplication { Unreadable = true };

        var errors = BalanceCorrectionValidator.ValidateAgainstBalance(root, command);
        if (errors.Count > 0)
            return new BalanceCorrectionApplication { Errors = errors };

        var trail   = root[ManualCorrectionsKey] as JsonArray;
        var history = trail?.DeepClone().AsArray() ?? [];
        var at      = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        var applied = new List<AppliedBalanceCorrection>(command.Corrections.Count);

        foreach (var entry in command.Corrections)
        {
            BalanceCorrectionValidator.TryReadStored(root, entry.Path, out var old);
            Write(root, entry.Path, entry.Value);
            applied.Add(new AppliedBalanceCorrection(entry.Path.Raw, old, entry.Value));

            history.Add(new JsonObject
            {
                ["path"]      = entry.Path.Raw,
                ["original"]  = FirstOriginal(trail, entry.Path.Raw) is (true, var first) ? first : Number(old),
                ["corrected"] = Number(entry.Value),
                ["by"]        = by.ToString(),
                ["at"]        = at,
                ["reason"]    = command.Reason,
                ["origin"]    = command.Origin,
            });
        }

        root[ManualCorrectionsKey] = history;

        bool? passed;
        IReadOnlyList<string>? failedChecks = null;
        bool? netProfitSignFlip = null;
        bool? netProfitColumnMix = null;
        var unavailable = false;
        try
        {
            var report = Reevaluate(root, integrity);
            root[IntegrityKey] = report.Node;
            passed            = report.Passed;
            failedChecks      = report.FailedChecks;
            netProfitSignFlip  = report.NetProfitSignFlip;
            netProfitColumnMix = report.NetProfitColumnMix;
        }
        catch (Exception ex) when (IsEvaluationFault(ex))
        {
            // The same fault isolation as the extraction: a report that cannot be built is written as
            // JSON null, which every reader takes as "integrity_unavailable" — never as a pass.
            root[IntegrityKey] = null;
            passed             = null;
            unavailable        = true;
        }

        return new BalanceCorrectionApplication
        {
            BalanceJson          = root.ToJsonString(),
            Applied              = applied,
            IntegrityPassed       = passed,
            IntegrityFailedChecks      = failedChecks,
            IntegrityNetProfitSignFlip = netProfitSignFlip,
            IntegrityNetProfitColumnMix = netProfitColumnMix,
            IntegrityUnavailable  = unavailable,
        };
    }

    /// <summary>
    /// DRY RUN of a correction, for the correction suggestion: the same parse, the same balance rules,
    /// the same write of the figures and the same integrity checks as <see cref="Apply"/>, over a copy held
    /// in memory — but no trail entry, no author, no reason, nothing to persist: only the report the
    /// corrected figures would get. With no entries it evaluates the stored figures themselves, the
    /// baseline a dry run is compared with.
    /// </summary>
    public static BalanceCorrectionDryRun DryRun(
        string? balanceJson, IReadOnlyList<BalanceCorrectionEntry> entries, IBalanceIntegrityService integrity)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(integrity);

        var root = Parse(balanceJson);
        if (root is null)
            return new BalanceCorrectionDryRun { Unreadable = true };

        var errors = BalanceCorrectionValidator.ValidateAgainstBalance(root, entries);
        if (errors.Count > 0)
            return new BalanceCorrectionDryRun { Errors = errors };

        foreach (var entry in entries)
            Write(root, entry.Path, entry.Value);

        try
        {
            return new BalanceCorrectionDryRun { Report = Evaluate(root, integrity).Report };
        }
        catch (Exception ex) when (IsEvaluationFault(ex))
        {
            return new BalanceCorrectionDryRun();
        }
    }

    // ── Private methods ───────────────────────────────────────────────────────────

    private static JsonObject? Parse(string? balanceJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(balanceJson) ? null : JsonNode.Parse(balanceJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The faults of a report that cannot be built — the same fault isolation as the extraction.
    private static bool IsEvaluationFault(Exception ex)
        => ex is JsonException or InvalidOperationException or NotSupportedException
            or ArgumentException or FormatException or OverflowException;

    private static void Write(JsonObject root, BalanceCorrectionPath path, double? value)
    {
        if (path.Index is { } index)
        {
            // ValidateAgainstBalance proved the line exists and is an object.
            var line = (JsonObject)((JsonArray)root[path.Key]!)[index]!;
            line["amount"] = Number(value);
            return;
        }

        root[path.Key] = Number(value);
    }

    // The `original` of the FIRST trail entry for this path, when there is one. A JSON null original is
    // a real answer ("the figure was not extracted") and is returned as found, with a null node.
    private static (bool Found, JsonNode? Original) FirstOriginal(JsonArray? trail, string path)
    {
        if (trail is null)
            return (false, null);

        foreach (var node in trail)
        {
            if (node is JsonObject entry
                && entry["path"] is JsonValue p && p.TryGetValue<string>(out var existing)
                && string.Equals(existing, path, StringComparison.Ordinal)
                && entry.TryGetPropertyValue("original", out var original))
            {
                return (true, original?.DeepClone());
            }
        }

        return (false, null);
    }

    private static JsonNode? Number(double? value) => value is { } v ? JsonValue.Create(v) : null;

    private static (JsonObject Node, bool Passed, IReadOnlyList<string> FailedChecks, bool? NetProfitSignFlip, bool? NetProfitColumnMix) Reevaluate(
        JsonObject root, IBalanceIntegrityService integrity)
    {
        var outcome = Evaluate(root, integrity);

        var previous           = root[IntegrityKey] as JsonObject;
        var amountsUnit        = String(previous?["amountsUnit"]) ?? String(root["amounts_unit"]) ?? BalanceAmountsUnit.Pesos;
        var amountsUnitAssumed = Bool(previous?["amountsUnitAssumed"]) ?? false;
        var reported           = Double(previous?["reportedConfidence"]);

        double? effective = reported is { } r
            ? outcome.Report.Passed
                ? r
                : (double)Math.Min((decimal)r, BalanceIntegrityService.ConfidenceCap)
            : null;

        var report = outcome.Report with
        {
            AmountsUnit         = amountsUnit,
            AmountsUnitAssumed  = amountsUnitAssumed,
            ConfidenceCapped    = reported is { } rc && effective is { } ec && ec < rc,
            ReportedConfidence  = reported,
            EffectiveConfidence = effective,
            Extraction          = null,   // re-attached below as stored, byte for byte
            ManuallyCorrected   = true,
        };

        var serialized = JsonSerializer.SerializeToNode(report)!.AsObject();
        var extraction = previous?["extraction"]?.DeepClone();

        // Rebuild in order so the provenance sits where an extraction writes it, before the marker.
        var node = new JsonObject();
        foreach (var (key, value) in serialized.ToList())
        {
            serialized.Remove(key);
            if (key == "manuallyCorrected" && extraction is not null)
                node["extraction"] = extraction;
            node[key] = value;
        }

        var failed = outcome.Report.Checks
            .Where(c => !c.Passed)
            .Select(c => c.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (node, outcome.Report.Passed, failed,
            BalanceIntegrityService.NetProfitSignFlipOf(outcome.Report.Checks),
            BalanceIntegrityService.NetProfitColumnMixOf(outcome.Report.Checks));
    }

    // The integrity checks over the figures of root, in pesos: amounts_scale only tells them which unit to quote.
    private static BalanceIntegrityOutcome Evaluate(JsonObject root, IBalanceIntegrityService integrity)
    {
        var current = root.Deserialize<BalanceAccounts>()
                      ?? throw new InvalidOperationException("The balance has no figures.");

        var scale = root["amounts_scale"] is JsonValue s && s.GetValueKind() == JsonValueKind.Number
                    && s.TryGetValue<double>(out var factor) && factor > 0d
            ? factor
            : 1d;

        var balance = new ExtractedBalance
        {
            Current                    = current with { AmountsScale = null },
            PriorYear                  = (root["prior_year"] as JsonObject)?.Deserialize<BalanceAccounts>(),
            CurrentAssetsItems         = Items(root, "current_assets_items"),
            CurrentLiabilitiesItems    = Items(root, "current_liabilities_items"),
            NonCurrentLiabilitiesItems = Items(root, "non_current_liabilities_items"),
            // Correctable inputs (BalanceCorrectionPath.TopLevelKeys): read AFTER Write, so a corrected value counts.
            ResultBeforeTax            = Double(root["result_before_tax"]),
            IncomeTax                  = Double(root["income_tax"]),
            AppliedScale               = scale,   // already in pesos: quoting unit only, nothing is scaled
        };

        return integrity.Evaluate(balance);
    }

    private static List<BalanceLineItem>? Items(JsonObject root, string key) =>
        root[key] is JsonArray array ? array.Deserialize<List<BalanceLineItem>>() : null;

    private static double? Double(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;

    private static string? String(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
}
