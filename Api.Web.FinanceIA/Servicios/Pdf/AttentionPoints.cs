// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Globalization;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// "Puntos de atención" on the first page: at most <see cref="MaxPoints"/> bullets, most severe first, each a
/// fact the report states elsewhere in full. Pure — the caller gathers the facts, this ranks and words them.
/// </summary>
internal static class AttentionPoints
{
    internal const int MaxPoints = 5;

    internal const string None = "Sin puntos de atención.";

    /// <summary>A reliability reason as the verdict carries it; the code and ratio keys say which fact it states.</summary>
    internal sealed record ReliabilityReason(string Severity, string Message, string? Code = null, IReadOnlyList<string>? RatioKeys = null);

    /// <summary>
    /// An alert of either channel, already reduced to what the bullet prints. <paramref name="Figure"/> is the
    /// catalogue alert's measured value as printed (<see cref="CatalogueMetricFormat.Inline"/>); model alerts carry none.
    /// </summary>
    internal sealed record Alert(string? Level, string? Code, string? Title, string? Figure = null);

    /// <summary>Where a bullet came from: a page-1 fact, a catalogue alert or a model alert.</summary>
    internal enum BulletSource
    {
        Fact,
        Catalogue,
        Model,
    }

    /// <summary>One bullet as printed, with its source and — for an alert — the alert's code.</summary>
    internal sealed record Bullet(string Text, BulletSource Source, string? Code);

    /// <summary>The bullets in print order, with their sources.</summary>
    internal sealed record Selection(IReadOnlyList<Bullet> Items)
    {
        public IReadOnlyList<string> Bullets => Items.Select(item => item.Text).ToList();
    }

    /// <summary>The codes the export log names when page 1 may print one finding twice under an unlisted spelling.</summary>
    internal sealed record UnlistedOverlapCodes(IReadOnlyList<string> CatalogueCodes, IReadOnlyList<string> ModelCodes);

    /// <summary>Everything the selection reads. Every member is optional: a missing fact adds no bullet.</summary>
    internal sealed record Facts
    {
        public string? ReliabilityLevel { get; init; }
        public IReadOnlyList<ReliabilityReason> ReliabilityReasons { get; init; } = [];

        /// <summary>The after-floor figure when a critical-ratio floor actually bound the score.</summary>
        public decimal? FloorAppliedTo { get; init; }
        public IReadOnlyList<string> CriticalRatioLabels { get; init; } = [];

        /// <summary>The score-breakdown keys of the ratios behind the floor (e.g. <c>endeudamiento</c>).</summary>
        public IReadOnlyList<string> CriticalRatioKeys { get; init; } = [];

        /// <summary>Composite-level adjustments (which one, label, 1000-scale delta) — only those that took points count.</summary>
        public IReadOnlyList<(ScoreAnatomy.AdjustmentKey Key, string Label, decimal DeltaNosis)> Adjustments { get; init; } = [];

        public int? BcraSituacion { get; init; }

        /// <summary>Situación steps, current minus comparison: positive = worsening.</summary>
        public int? BcraTrendDelta { get; init; }

        public int? ChequesRechazados { get; init; }
        public int? ChequesImpagos { get; init; }

        /// <summary>The model channel (<c>RiskAlerts</c>).</summary>
        public IReadOnlyList<Alert> Alerts { get; init; } = [];

        /// <summary>The deterministic channel (<c>CatalogueAlerts</c>), its title the tenant's catalogue name as stored.</summary>
        public IReadOnlyList<Alert> CatalogueAlerts { get; init; } = [];

        /// <summary>The tenant's AlertRuleCatalogue display names by code, for the alert titles; empty when unknown.</summary>
        public IReadOnlyDictionary<string, string> AlertDisplayNames { get; init; } = new Dictionary<string, string>();
    }

    private sealed record Candidate(int Rank, int Order, string Text, BulletSource Source, string? Code);

    // A code as the log may print it: the closed shape alert codes have; anything else is model-written text.
    private static readonly Regex LoggableCode = new("^[A-Z0-9_]{1,64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal const string NonCode = "<non-code>";

    /// <summary>The bullets to print, most severe first, capped at <paramref name="cap"/>; empty when none apply.</summary>
    /// <remarks>
    /// <para>
    /// Both alert channels are candidates: critical at the top tier, high below the provisional reasons, medium and low
    /// not at all. At equal severity a catalogue alert comes first — it is deterministic. A model alert that states the
    /// same finding as a catalogue alert (<see cref="AttentionOverlaps.SameFinding"/>) is not printed; the catalogue alert
    /// takes its rank when that is higher.
    /// </para>
    /// <para>
    /// One bullet per fact: an alert whose code <see cref="AttentionOverlaps"/> maps to a fact that produced a bullet is
    /// not printed — the fact's line, which carries the figures, takes the alert's rank when that is higher.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> Select(Facts facts, int cap = MaxPoints) => Evaluate(facts, cap).Bullets;

    /// <summary>
    /// The codes to log when the first <paramref name="printed"/> bullets hold a catalogue alert AND a model alert
    /// whose code no <see cref="AttentionOverlaps"/> list names — the case where one finding may be printed twice under
    /// a spelling not yet listed. Null otherwise. Codes only, never titles; a code that is not code-shaped is
    /// <see cref="NonCode"/>.
    /// </summary>
    internal static UnlistedOverlapCodes? UnlistedOverlap(Selection selection, int printed)
    {
        ArgumentNullException.ThrowIfNull(selection);

        List<Bullet> shown = selection.Items.Take(Math.Max(0, printed)).ToList();
        List<string> catalogue = shown
            .Where(item => item.Source == BulletSource.Catalogue)
            .Select(item => Loggable(item.Code))
            .ToList();
        List<string> model = shown
            .Where(item => item.Source == BulletSource.Model && !AttentionOverlaps.IsListed(item.Code))
            .Select(item => Loggable(item.Code))
            .ToList();

        return catalogue.Count > 0 && model.Count > 0 ? new UnlistedOverlapCodes(catalogue, model) : null;
    }

    internal static string Loggable(string? code)
    {
        string trimmed = code?.Trim() ?? string.Empty;
        return LoggableCode.IsMatch(trimmed) ? trimmed : NonCode;
    }

    /// <summary>As <see cref="Select"/>, with each bullet's source.</summary>
    internal static Selection Evaluate(Facts facts, int cap = MaxPoints)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // The facts that state a figure, built first so an alert repeating one can give way to it.
        var factBullets = new Dictionary<AttentionFact, (int Rank, string Text)>();

        foreach ((ScoreAnatomy.AdjustmentKey key, string label, decimal delta) in facts.Adjustments)
        {
            if (key == ScoreAnatomy.AdjustmentKey.Ventas && delta < 0m && !factBullets.ContainsKey(AttentionFact.Ventas))
                factBullets[AttentionFact.Ventas] = (70, AdjustmentText(label, delta));
        }

        if (BcraBullet(facts.BcraSituacion, facts.BcraTrendDelta) is { } bcra)
            factBullets[AttentionFact.Bcra] = bcra;

        if (facts.ChequesRechazados is > 0 and var rechazados)
        {
            int impagos = facts.ChequesImpagos ?? 0;
            string cheques = rechazados == 1 ? "1 cheque rechazado" : $"{Count(rechazados)} cheques rechazados";
            string estado = impagos switch
            {
                0 => "todos regularizados",
                1 => "1 impago",
                _ => $"{Count(impagos)} impagos"
            };
            factBullets[AttentionFact.Cheques] = (impagos > 0 ? 55 : 40, $"{cheques} ({estado}).");
        }

        // The critical-floor line, a fact when it names Endeudamiento.
        string? floorText = null;
        if (facts.FloorAppliedTo is { } floorTo)
        {
            string ratios = facts.CriticalRatioLabels.Count == 0
                ? string.Empty
                : $" ({string.Join(", ", facts.CriticalRatioLabels)})";
            floorText = $"Piso por ratio crítico: el score se llevó a {ScoreAnatomy.FormatFigure(floorTo)}{ratios}.";

            if (AttentionOverlaps.FloorIsCriticalFloorFact(facts.CriticalRatioKeys))
                factBullets[AttentionFact.CriticalFloor] = (90, floorText);
        }

        // The reliability reasons are listed only for a verdict with caveats or provisional. One that states a ratio
        // fact is that fact's line (the first such reason; the floor line, when it is the fact, wins).
        string level = (facts.ReliabilityLevel ?? string.Empty).Trim().ToLowerInvariant();
        bool listReasons = level is ScoreReliabilityLevels.WithCaveats or ScoreReliabilityLevels.Provisional;
        IReadOnlyList<ReliabilityReason> reasons = listReasons ? facts.ReliabilityReasons : [];

        var reasonFact = new Dictionary<int, AttentionFact>();
        for (int i = 0; i < reasons.Count; i++)
        {
            if (AttentionOverlaps.FactOfReason(reasons[i].Code, reasons[i].RatioKeys) is { } fact && !factBullets.ContainsKey(fact))
            {
                factBullets[fact] = (ReasonRank(reasons[i]), reasons[i].Message);
                reasonFact[i] = fact;
            }
        }

        // An alert repeating a fact that has a bullet lends it its rank and is not printed itself.
        bool Absorbed(Alert alert, int alertRank)
        {
            if (AttentionOverlaps.FactOf(alert.Code) is not { } fact || !factBullets.TryGetValue(fact, out var bullet))
                return false;

            if (alertRank > bullet.Rank)
                factBullets[fact] = (alertRank, bullet.Text);
            return true;
        }

        // A model alert stating a catalogue alert's finding gives way to it, lending its rank.
        List<(Alert Alert, int Rank)> catalogue = facts.CatalogueAlerts
            .Where(a => a is not null)
            .Select(a => (a, AlertRank(a.Level)))
            .ToList();
        var model = new List<(Alert Alert, int Rank)>();
        foreach (Alert alert in facts.Alerts.Where(a => a is not null))
        {
            int rank = AlertRank(alert.Level);
            int same = catalogue.FindIndex(c => AttentionOverlaps.SameFinding(c.Alert.Code, alert.Code));
            if (same < 0)
            {
                model.Add((alert, rank));
                continue;
            }

            if (rank > catalogue[same].Rank)
                catalogue[same] = (catalogue[same].Alert, rank);
        }

        List<(Alert Alert, int Rank)> catalogueKept = catalogue.Where(c => c.Rank > 0 && !Absorbed(c.Alert, c.Rank)).ToList();
        List<(Alert Alert, int Rank)> modelKept     = model.Where(m => m.Rank > 0 && !Absorbed(m.Alert, m.Rank)).ToList();

        var candidates = new List<Candidate>();
        int order = 0;
        void Add(int rank, string? text, BulletSource source = BulletSource.Fact, string? code = null)
        {
            if (!string.IsNullOrWhiteSpace(text))
                candidates.Add(new Candidate(rank, order++, text.Trim(), source, code));
        }

        void AddFact(AttentionFact fact)
        {
            if (factBullets.TryGetValue(fact, out var bullet))
                Add(bullet.Rank, bullet.Text);
        }

        void AddAlerts(int tier)
        {
            foreach ((Alert alert, _) in catalogueKept.Where(c => c.Rank == tier))
                Add(tier, CatalogueTitle(alert), BulletSource.Catalogue, alert.Code);
            foreach ((Alert alert, _) in modelKept.Where(m => m.Rank == tier))
                Add(tier, AlertTitles.Resolve(alert.Code, alert.Title, facts.AlertDisplayNames), BulletSource.Model, alert.Code);
        }

        // Critical alerts outrank everything; a provisional verdict is next — it says the score itself may move.
        AddAlerts(CriticalRank);

        for (int i = 0; i < reasons.Count; i++)
        {
            if (reasonFact.TryGetValue(i, out AttentionFact fact))
                AddFact(fact);
            else
                Add(ReasonRank(reasons[i]), reasons[i].Message);
        }

        if (floorText is not null)
        {
            if (factBullets.TryGetValue(AttentionFact.CriticalFloor, out var floorBullet) && floorBullet.Text == floorText)
                AddFact(AttentionFact.CriticalFloor);
            else
                Add(90, floorText);
        }

        AddFact(AttentionFact.Bcra);

        AddAlerts(HighRank);

        AddFact(AttentionFact.Ventas);
        foreach ((ScoreAnatomy.AdjustmentKey key, string label, decimal delta) in facts.Adjustments)
        {
            if (key != ScoreAnatomy.AdjustmentKey.Ventas && delta < 0m)
                Add(70, AdjustmentText(label, delta));
        }

        AddFact(AttentionFact.Cheques);

        List<Bullet> bullets = candidates
            .OrderByDescending(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Order)
            .DistinctBy(candidate => candidate.Text, StringComparer.Ordinal)
            .Take(Math.Max(0, cap))
            .Select(candidate => new Bullet(candidate.Text, candidate.Source, candidate.Code))
            .ToList();

        return new Selection(bullets);
    }

    private const int CriticalRank = 100;
    private const int HighRank     = 80;

    // Critical and high alerts are candidates; medium and low are not (0).
    private static int AlertRank(string? level) => PdfLabels.AlertSeverity(level).Rank switch
    {
        4 => CriticalRank,
        3 => HighRank,
        _ => 0
    };

    private static int ReasonRank(ReliabilityReason reason) => reason.Severity switch
    {
        ScoreReliabilitySeverities.Provisional => 95,
        ScoreReliabilitySeverities.None        => 95,
        _                                      => 30
    };

    // A catalogue alert's title is the tenant's catalogue name as stored; only a blank one falls back to the code.
    // Its figure, when it states one, follows the title: "Endeudamiento elevado — 84,4 % (umbral 50,0 %)".
    private static string? CatalogueTitle(Alert alert)
    {
        string? title;
        if (!string.IsNullOrWhiteSpace(alert.Title))
        {
            title = alert.Title.Trim();
        }
        else
        {
            string code = alert.Code?.Trim() ?? string.Empty;
            title = code.Length == 0 ? null : AlertTitles.Humanize(code);
        }

        return title is not null && !string.IsNullOrWhiteSpace(alert.Figure)
            ? $"{title} — {alert.Figure.Trim()}"
            : title;
    }

    /// <summary>
    /// The one BCRA line: the situación when it is 2 or worse, with the worsening trend merged into it ("Situación
    /// BCRA 2 — Con seguimiento especial, empeorando 1 nivel."); the trend alone when the situación is unknown or
    /// normal; nothing when neither applies.
    /// </summary>
    private static (int Rank, string Text)? BcraBullet(int? situacion, int? trendDelta)
    {
        int? worse = trendDelta is > 0 ? trendDelta : null;

        if (situacion is >= 2 and var sit)
        {
            string head = $"Situación BCRA {Count(sit)} — {PdfLabels.BcraSituacion(sit)}";
            string text = worse is { } levels ? $"{head}, empeorando {Levels(levels)}." : $"{head}.";
            return (sit >= 3 ? 85 : 60, text);
        }

        return worse is { } alone ? (60, $"Situación BCRA empeorando ({Levels(alone)}).") : null;
    }

    private static string AdjustmentText(string label, decimal deltaNosis) =>
        $"{label}: {ScoreAnatomy.FormatDelta(deltaNosis)} pts sobre el score.";

    private static string Levels(int count) => count == 1 ? "1 nivel" : $"{Count(count)} niveles";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
