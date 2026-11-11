// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>The inventory half of <see cref="LegajoReconciliation"/>.</summary>
public static partial class LegajoReconciliation
{
    /// <summary>Two ownership percentages further apart than this disagree.</summary>
    private const decimal OwnershipTolerance = 0.01m;

    private const string BpPrefix = "BP";

    /// <summary>The Bienes Personales rubro of securities, shares and funds.</summary>
    private const string TitulosYAcciones = "TitulosYAcciones";
    private const string MbPrefix = "MB";

    /// <summary>The match-key kinds that pair an asset across documents.</summary>
    private static readonly HashSet<string> MatchingKinds =
    [
        LegajoMatchKeyKinds.Partida, LegajoMatchKeyKinds.Dominio, LegajoMatchKeyKinds.Cuit, LegajoMatchKeyKinds.Cbu
    ];

    /// <summary>One printed asset or debt of one document, before pairing.</summary>
    private sealed record Row(
        string Origin,
        Guid DocumentId,
        string Kind,
        string MatchKeyKind,
        string? MatchKey,
        string? Description,
        decimal? OwnershipPct,
        decimal? Value,
        string? Currency,
        bool IsActivityAsset,
        bool IsHousing,
        string? InvestmentClass)
    {
        public bool Matches => MatchKey is not null && MatchingKinds.Contains(MatchKeyKind);
    }

    private static List<LegajoItem> BuildItems(
        string tenantId, Guid personLegajoId, IReadOnlyList<Readable> usable,
        HashSet<Guid> contributed, HashSet<Guid> olderSnapshot)
    {
        // The inventory is a snapshot: the latest Bienes Personales (fiscal year, then rectificativa, then
        // upload) and the latest manifestación (measurement date, then upload). Older ones are skipped.
        var bp = Latest(usable.Where(r => r.DocumentType == DocumentTypes.BienesPersonales), r =>
            (LegajoJsonValues.Integer(LegajoJsonPointer.Get(r.Tree, "/header/fiscalYear/value")) ?? 0,
             RectificativaRank(LegajoJsonValues.Integer(LegajoJsonPointer.Get(r.Tree, "/header/rectificativaNumber/value")),
                               Boolean(LegajoJsonPointer.Get(r.Tree, "/header/isRectificativa/value"))),
             0), olderSnapshot);

        var mb = Latest(usable.Where(r => r.DocumentType == DocumentTypes.ManifestacionBienes), r =>
            (LegajoJsonValues.Date(LegajoJsonPointer.Get(r.Tree, "/header/measurementDate/value")) is { } d ? d.DayNumber : 0,
             0, 0), olderSnapshot);

        var bpRows = bp is null ? [] : BienesPersonalesRows(bp);
        var mbRows = mb is null ? [] : ManifestacionRows(mb);

        if (bpRows.Count > 0) contributed.Add(bp!.Input.DocumentId);
        if (mbRows.Count > 0) contributed.Add(mb!.Input.DocumentId);

        var items = new List<LegajoItem>();
        var keys  = new HashSet<string>(StringComparer.Ordinal);

        // ── Keyed rows: a pair forms only when the key is unique on BOTH sides ───
        var bpByKey = bpRows.Where(r => r.Matches).GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var mbByKey = mbRows.Where(r => r.Matches).GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var key in bpByKey.Keys.Union(mbByKey.Keys).Order(StringComparer.Ordinal))
        {
            var fromBp = bpByKey.GetValueOrDefault(key) ?? [];
            var fromMb = mbByKey.GetValueOrDefault(key) ?? [];

            if (fromBp.Count <= 1 && fromMb.Count <= 1)
            {
                items.Add(Item(tenantId, personLegajoId, Unique(keys, key), fromBp.SingleOrDefault(), fromMb.SingleOrDefault()));
                continue;
            }

            // The same key twice in one document: ambiguous, so nothing is paired.
            for (var i = 0; i < fromBp.Count; i++)
                items.Add(Item(tenantId, personLegajoId, Unique(keys, $"{key}#{BpPrefix}{i + 1}"), fromBp[i], null));
            for (var i = 0; i < fromMb.Count; i++)
                items.Add(Item(tenantId, personLegajoId, Unique(keys, $"{key}#{MbPrefix}{i + 1}"), null, fromMb[i]));
        }

        // ── Unkeyed rows: never paired ──────────────────────────────────────────
        foreach (var row in bpRows.Where(r => !r.Matches).Concat(mbRows.Where(r => !r.Matches)))
        {
            var key = $"{row.Origin}:{row.Kind}:{NormalizeDescription(row.Description)}";
            items.Add(row.Origin == BpPrefix
                ? Item(tenantId, personLegajoId, Unique(keys, key), row, null)
                : Item(tenantId, personLegajoId, Unique(keys, key), null, row));
        }

        return items;
    }

    /// <summary>The newest input by <paramref name="rank"/> then upload; every other one is recorded as an older snapshot.</summary>
    private static Readable? Latest(
        IEnumerable<Readable> candidates, Func<Readable, (int, int, int)> rank, HashSet<Guid> olderSnapshot)
    {
        var ordered = candidates
            .OrderByDescending(rank)
            .ThenByDescending(r => r.Input.UploadedAt)
            .ThenByDescending(r => r.Input.DocumentId)
            .ToList();

        foreach (var older in ordered.Skip(1))
            olderSnapshot.Add(older.Input.DocumentId);

        return ordered.FirstOrDefault();
    }

    private static List<Row> BienesPersonalesRows(Readable bp)
    {
        var rows = new List<Row>();
        foreach (var row in bp.Rows("/items"))
        {
            var (keyKind, key) = MatchKeyOf(row["matchKey"]);
            var rubro = LegajoJsonValues.String(row["rubro"]);
            var investmentClass = rubro == TitulosYAcciones ? InvestmentClassOf(row["investmentClass"]) : null;
            var kind = KindOfRubro(rubro, keyKind, investmentClass);
            rows.Add(new Row(
                Origin:          BpPrefix,
                DocumentId:      bp.Input.DocumentId,
                Kind:            kind,
                MatchKeyKind:    keyKind,
                MatchKey:        key,
                Description:     Text(row["description"]),
                OwnershipPct:    Ownership(row["ownershipPct"]),
                Value:           bp.Pesos(row["fiscalValue"]),
                Currency:        Currency(row["currency"]),
                IsActivityAsset: false,
                IsHousing:       Boolean(row["isHousing"]) == true,
                // A row that stays a Participacion carries no class, so a pair cannot inherit it either.
                InvestmentClass: kind == LegajoItemKinds.Inversion ? investmentClass : null));
        }

        return rows;
    }

    private static List<Row> ManifestacionRows(Readable mb)
    {
        var rows = new List<Row>();
        var annexRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in mb.Rows("/annexItems"))
        {
            if (Text(LegajoJsonValues.String(row["annexRef"])) is { } annexRef)
                annexRefs.Add(annexRef);

            var (keyKind, key) = MatchKeyOf(row["matchKey"]);
            var kind = LegajoJsonValues.String(row["kind"]) is { } k && LegajoItemKinds.Known.Contains(k) ? k : LegajoItemKinds.Otro;
            rows.Add(new Row(
                Origin:          MbPrefix,
                DocumentId:      mb.Input.DocumentId,
                Kind:            kind,
                MatchKeyKind:    keyKind,
                MatchKey:        key,
                Description:     Text(row["description"]),
                OwnershipPct:    Ownership(row["ownershipPct"]),
                Value:           mb.Pesos(row["marketValue"]),
                Currency:        Currency(row["currency"]),
                IsActivityAsset: Boolean(row["isActivityAsset"]) == true,
                IsHousing:       Boolean(row["isHousing"]) == true,
                InvestmentClass: kind == LegajoItemKinds.Inversion ? InvestmentClassOf(row["investmentClass"]) : null));
        }

        // A line of the statement whose detail is in no extracted annex is an item of its own: debts come
        // from the manifestación even when it lists them only as lines. A line pointing at an annex that
        // was extracted is that annex's items and is not counted twice.
        foreach (var line in mb.Rows("/lines"))
        {
            if (Text(LegajoJsonValues.String(line["annexRef"])) is { } annexRef && annexRefs.Contains(annexRef))
                continue;

            var section = LegajoJsonValues.String(line["section"]);
            var isDebt  = section is V.PasivoCorriente or V.PasivoNoCorriente;
            rows.Add(new Row(
                Origin:          MbPrefix,
                DocumentId:      mb.Input.DocumentId,
                Kind:            isDebt ? LegajoItemKinds.Deuda : LegajoItemKinds.Otro,
                MatchKeyKind:    LegajoMatchKeyKinds.Ninguno,
                MatchKey:        null,
                Description:     Text(line["label"]),
                OwnershipPct:    null,
                Value:           mb.Pesos(line["amount"]),
                Currency:        null,
                IsActivityAsset: false,
                IsHousing:       false,
                InvestmentClass: null));
        }

        return rows;
    }

    /// <summary>The item for a pair (either side may be missing), with the precedence rules applied.</summary>
    private static LegajoItem Item(string tenantId, Guid personLegajoId, string itemKey, Row? bp, Row? mb)
    {
        var any  = bp ?? mb ?? throw new ArgumentException("An item needs at least one row.");
        var kind = mb?.Kind ?? bp!.Kind;

        var reasons = new List<string>();
        if (bp is not null && mb is not null)
        {
            if (bp.OwnershipPct is { } b && mb.OwnershipPct is { } m && Math.Abs(b - m) > OwnershipTolerance)
                reasons.Add(LegajoItemConflictReasons.OwnershipDiffers);
            if (mb.Value is { } market && bp.Value is { } fiscal && market < fiscal)
                reasons.Add(LegajoItemConflictReasons.BelowFiscalFloor);
            if (bp.Currency is { } bc && mb.Currency is { } mc && !string.Equals(bc, mc, StringComparison.Ordinal))
                reasons.Add(LegajoItemConflictReasons.CurrencyDiffers);
        }

        var status = (bp, mb) switch
        {
            (not null, not null) => reasons.Count > 0 ? LegajoReconciliationStatuses.Conflict : LegajoReconciliationStatuses.Matched,
            (not null, null)     => LegajoReconciliationStatuses.OnlyInBienesPersonales,
            _                    => LegajoReconciliationStatuses.OnlyInManifestacion
        };

        return new LegajoItem
        {
            TenantId              = tenantId,
            PersonLegajoId        = personLegajoId,
            ItemKey               = itemKey,
            Side                  = kind == LegajoItemKinds.Deuda ? LegajoItemSides.Pasivo : LegajoItemSides.Activo,
            Kind                  = kind,
            // Only an investment is classified (CK_LegajoItems_InvestmentClass); the manifestación wins, as for the kind.
            InvestmentClass       = kind == LegajoItemKinds.Inversion ? mb?.InvestmentClass ?? bp?.InvestmentClass : null,
            MatchKeyKind          = any.MatchKeyKind,
            MatchKey              = any.MatchKey,
            // Existence and ownership: Bienes Personales.
            Description           = bp?.Description ?? mb?.Description,
            OwnershipPct          = bp?.OwnershipPct ?? mb?.OwnershipPct,
            // Value: the manifestación, with Bienes Personales beside it as the floor.
            MarketValue           = mb?.Value,
            MarketValueDocumentId = mb?.DocumentId,
            FiscalValue           = bp?.Value,
            FiscalValueDocumentId = bp?.DocumentId,
            Currency              = mb?.Currency ?? bp?.Currency,
            IsActivityAsset       = mb?.IsActivityAsset ?? false,
            IsHousing             = (bp?.IsHousing ?? false) || (mb?.IsHousing ?? false),
            LinkedCuit            = any.MatchKeyKind == LegajoMatchKeyKinds.Cuit && any.MatchKey is { Length: 11 } digits
                ? LegajoCuit.Format(digits)
                : null,
            ReconciliationStatus  = status,
            ConflictReasons       = reasons.Count > 0 ? string.Join(",", reasons) : null
        };
    }

    /// <summary>The match key of a row: its kind and the normalized value (null when there is none).</summary>
    private static (string Kind, string? Key) MatchKeyOf(JsonNode? matchKey)
    {
        var kind = LegajoJsonValues.String(matchKey?["kind"]);
        if (kind is null || !LegajoMatchKeyKinds.Known.Contains(kind) || kind == LegajoMatchKeyKinds.Ninguno)
            return (LegajoMatchKeyKinds.Ninguno, null);

        var key = NormalizeKey(kind, LegajoJsonValues.String(matchKey?["value"]));
        return key is null ? (LegajoMatchKeyKinds.Ninguno, null) : (kind, key);
    }

    /// <summary>
    /// A match key reduced to what identifies it: digits for a CUIT (exactly 11) or a CBU; upper-case
    /// letters and digits for a partida, a dominio or another identifier. Null when nothing is left.
    /// </summary>
    public static string? NormalizeKey(string kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (kind == LegajoMatchKeyKinds.Cuit)
            return LegajoCuit.Digits(value);

        var normalized = kind == LegajoMatchKeyKinds.Cbu
            ? new string(value.Where(char.IsAsciiDigit).ToArray())
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        if (normalized.Length == 0)
            return null;

        return normalized.Length <= 60 ? normalized : normalized[..60];
    }

    private static string KeyOf(Row row) => $"{row.MatchKeyKind}:{row.MatchKey}";

    /// <summary><paramref name="key"/>, or with a #n suffix when it is already taken.</summary>
    private static string Unique(HashSet<string> taken, string key)
    {
        var candidate = key;
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{key}#{n.ToString(CultureInfo.InvariantCulture)}";

        return candidate;
    }

    /// <summary>
    /// A Bienes Personales rubro mapped onto the inventory kinds. A TitulosYAcciones row is an investment, except
    /// that one with a CUIT match key keeps the rule of bp-1.0 — a participation — unless bp-1.1 gave it a CONCRETE
    /// class (<see cref="LegajoInvestmentClasses.IsConcrete"/>: a bono, a listed share, an ON, an FCI). Otra or no class
    /// with a CUIT stays a Participacion: a doubt never moves an item to the lower investment haircut. Without a
    /// CUIT the row is an Inversion whatever its class, Otra included.
    /// </summary>
    private static string KindOfRubro(string? rubro, string matchKeyKind, string? investmentClass) => rubro switch
    {
        "Inmuebles"        => LegajoItemKinds.Inmueble,
        "Rodados"          => LegajoItemKinds.Rodado,
        "Depositos"        => LegajoItemKinds.Deposito,
        "DineroEfectivo"   => LegajoItemKinds.Efectivo,
        "Creditos"         => LegajoItemKinds.Credito,
        TitulosYAcciones   => matchKeyKind == LegajoMatchKeyKinds.Cuit && !LegajoInvestmentClasses.IsConcrete(investmentClass)
                                  ? LegajoItemKinds.Participacion
                                  : LegajoItemKinds.Inversion,
        _                  => LegajoItemKinds.Otro
    };

    /// <summary>The printed class when it is one of <see cref="LegajoInvestmentClasses.Known"/>; null otherwise.</summary>
    private static string? InvestmentClassOf(JsonNode? node)
        => LegajoJsonValues.String(node) is { } value && LegajoInvestmentClasses.Known.Contains(value) ? value : null;

    private static decimal? Ownership(JsonNode? node)
        => LegajoJsonValues.Decimal(node) is { } pct and >= 0 and <= 100 ? Math.Round(pct, 4) : null;

    private static string? Currency(JsonNode? node)
        => Text(LegajoJsonValues.String(node))?.ToUpperInvariant() is { Length: <= 10 } c ? c : null;

    private static string? Text(JsonNode? node) => Text(LegajoJsonValues.String(node));

    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    /// <summary>A description folded for an item key: upper-case, single spaces, at most 200 characters.</summary>
    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "-";

        var sb = new StringBuilder(description.Length);
        var space = false;
        foreach (var c in description.Trim().ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
                sb.Append(' ');
            sb.Append(c);
            space = false;
        }

        return sb.Length <= 200 ? sb.ToString() : sb.ToString(0, 200);
    }
}
