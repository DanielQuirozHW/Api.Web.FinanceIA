// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>The chat's "CIFRAS DEL BALANCE" block: the extracted balance figures, read at prompt time.</summary>
public sealed partial class AnalysisController
{
    /// <summary>The block header, emitted on every prompt whatever the balance holds.</summary>
    internal const string ChatBalanceFiguresHeader = "CIFRAS DEL BALANCE (leídas del documento):";

    /// <summary>A rubro label longer than this is cut at a word boundary and closed with "…".</summary>
    internal const int MaxRubroLabelChars = 120;

    /// <summary>Lines printed per rubro list; the rest are counted on one closing line.</summary>
    internal const int MaxRubrosPerList = 40;

    /// <summary>
    /// The fixed rules that follow the figures, emitted in both branches. Without them the model, given a
    /// liabilities total and no cost of sales, called the whole liability "deuda" and estimated inventory
    /// from the gap between two ratios — both presented as data in a client demo. The line they draw:
    /// arithmetic on figures present in this prompt is allowed and said to be a calculation; inferring a rubro
    /// from ratios, or filling a figure that is in no block, is not. Prior-year TOTALS (and inventory) are
    /// present, so averages over them are computable; prior-year detailed rubros are not.
    /// </summary>
    internal const string ChatBalanceFiguresRules =
        "(Estas cifras son las que la lectura automática tomó del documento, con las correcciones manuales " +
        "que haya registrado un analista. Si CALIDAD DE LOS DATOS indica que la lectura del balance no superó " +
        "los controles de consistencia, decilo cada vez que las uses. En este análisis NO están disponibles el " +
        "costo de ventas, las compras, las amortizaciones y depreciaciones del ejercicio ni los rubros " +
        "detallados del ejercicio anterior (créditos por ventas, deudas comerciales, deudas bancarias y demás): " +
        "si una pregunta los necesita —días de inventario, días de pago a proveedores, EBITDA— indicá " +
        "exactamente qué cifra falta y pedile al analista que la aporte; nunca la aproximes. Una cifra que " +
        "figura en los dos ejercicios, bajo \"Ejercicio cerrado:\" y bajo \"Ejercicio anterior:\", se puede " +
        "promediar. Está permitido calcular con cifras que estén en los bloques de este prompt (las cifras del " +
        "balance, los ratios, el resumen ejecutivo), siempre que digas que es un cálculo y qué cifras usaste. " +
        "Nunca infieras un rubro a partir de ratios (por ejemplo, los bienes de cambio como diferencia entre la " +
        "liquidez corriente y la prueba ácida) ni completes con una estimación o un valor verosímil una cifra " +
        "que no está en ningún bloque. La deuda financiera es la deuda bancaria, financiera y concursal que " +
        "figura en los rubros, nunca el pasivo total; si la naturaleza de un rubro no surge clara de su " +
        "nombre, decilo.)";

    /// <summary>The closing-year scalars, in print order. Labels from <see cref="BalanceSuggestionLabels.FieldLabels"/>.</summary>
    private static readonly string[] ChatBalanceCurrentKeys =
    [
        "assets", "current_assets", "non_current_assets",
        "liabilities", "current_liabilities", "non_current_liabilities",
        "equity", "net_revenue", "inventory", "prepaid_expenses",
        "operating_result", "financial_expenses", "result_before_tax", "income_tax", "net_profit",
    ];

    /// <summary>The prior-year scalars: <c>prior_year</c> is a <see cref="BalanceAccounts"/>, with no tax lines.</summary>
    private static readonly string[] ChatBalancePriorKeys =
    [
        "assets", "current_assets", "non_current_assets",
        "liabilities", "current_liabilities", "non_current_liabilities",
        "equity", "net_revenue", "inventory", "prepaid_expenses",
        "operating_result", "financial_expenses", "net_profit",
    ];

    private static readonly string[] ChatBalanceItemArrays =
        ["current_assets_items", "current_liabilities_items", "non_current_liabilities_items"];

    /// <summary>
    /// Renders the "CIFRAS DEL BALANCE" block of the chat system prompt from the persisted
    /// <c>AnalysisResults.BalanceJson</c>: the unit, the closing-year scalars, the three rubro lists and the
    /// prior-year scalars, then <see cref="ChatBalanceFiguresRules"/>. Lines are joined with "\n" and the
    /// block holds no blank line (blank lines are this prompt's block delimiter); no trailing newline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHICH FIGURES. The blob the detail screen, the scoring and the re-score read. The extraction persists
    /// it flat (the model's <c>balance_check</c> is unwrapped to the root), already in PESOS
    /// (<c>amounts_scale</c> applied once; <c>amounts_unit</c> keeps the printed unit), and — for a trial
    /// balance — with the normalised figures the integrity checks evaluated. A manual correction or an
    /// accepted suggestion rewrites this same column in place, so the corrected figures are what is read.
    /// </para>
    /// <para>
    /// Never throws. A null, malformed or non-object blob, or one with no closing-year figure and no rubro,
    /// prints "No disponible" under the header — the header and the rules are always there, the same reason
    /// LECTURA DEL FLUJO keeps its header: an absent block reads to the model as an absent concept.
    /// </para>
    /// <para>
    /// K5: rubro labels are printed as read and can carry the company's name. They are not redacted here:
    /// ChatAsync runs the WHOLE system prompt through EntityIdentifierRedactor before it leaves. Labels are
    /// cut at a word boundary, never mid-token, so a clamp cannot split a name or CUIT out of the
    /// redactor's reach.
    /// </para>
    /// </remarks>
    internal static string BuildChatBalanceFiguresBlock(string? balanceJson)
    {
        var lines = new List<string> { ChatBalanceFiguresHeader };

        var body = TryRenderChatBalanceBody(balanceJson);
        if (body is null)
            lines.Add("No disponible");
        else
            lines.AddRange(body);

        lines.Add(ChatBalanceFiguresRules);
        return string.Join("\n", lines);
    }

    /// <summary>The block's body lines, or null when there are no figures to show.</summary>
    private static List<string>? TryRenderChatBalanceBody(string? balanceJson)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return null;

        JsonObject root;
        try
        {
            // Pattern-matched: `null`, a scalar and an array all parse, and indexing them throws.
            if (JsonNode.Parse(balanceJson) is not JsonObject parsed)
                return null;
            root = parsed;
        }
        catch (JsonException)
        {
            return null;
        }

        var current = ChatBalanceScalarLines(root, ChatBalanceCurrentKeys);
        var lists   = ChatBalanceItemArrays.Select(name => ChatBalanceItemLines(root[name])).ToList();

        if (current.Count == 0 && lists.All(l => l.Count == 0))
            return null;

        var body = new List<string> { ChatBalanceUnitLine(root) };

        if (root["document_kind"] is JsonValue kindValue
            && kindValue.TryGetValue<string>(out var kind)
            && TrialBalanceNormalizer.IsTrialBalance(kind))
        {
            body.Add(
                "Documento: balance de sumas y saldos; las cifras ya están normalizadas al esquema de estados " +
                "contables (signos y resultado del ejercicio).");
        }

        body.Add("Ejercicio cerrado:");
        body.AddRange(current.Count > 0 ? current : ["No disponible"]);

        for (var i = 0; i < ChatBalanceItemArrays.Length; i++)
        {
            var heading = BalanceSuggestionLabels.ItemGroupLabels[ChatBalanceItemArrays[i]];
            body.Add(char.ToUpperInvariant(heading[0]) + heading[1..] + ":");
            body.AddRange(lists[i].Count > 0 ? lists[i] : ["No disponible"]);
        }

        var prior = root["prior_year"] is JsonObject priorYear
            ? ChatBalanceScalarLines(priorYear, ChatBalancePriorKeys)
            : [];
        body.Add("Ejercicio anterior:");
        body.AddRange(prior.Count > 0 ? prior : ["No disponible"]);

        return body;
    }

    /// <summary>States the unit the figures are in (pesos once scaled) and, when it differs, the printed one.</summary>
    private static string ChatBalanceUnitLine(JsonObject root)
    {
        // No amounts_scale: a row from before prompt 2.18, whose figures were stored as the model returned them.
        if (ChatBalanceNumber(root["amounts_scale"]) is not { } scale)
            return "Unidad de los importes: no registrada para este análisis; las cifras están tal como se leyeron del documento.";

        var printed = root["amounts_unit"] is JsonValue unitValue && unitValue.TryGetValue<string>(out var u)
            ? u
            : null;

        if (string.IsNullOrWhiteSpace(printed))
            return "Importes en pesos (el documento no indicó la unidad; se tomaron como pesos).";

        if (scale > 1d)
        {
            var unit = BalanceAmountsUnit.Resolve(printed).Unit switch
            {
                BalanceAmountsUnit.Millones => "millones de pesos",
                BalanceAmountsUnit.Miles    => "miles de pesos",
                _                           => printed.Trim(),
            };
            return $"Importes en pesos: el documento los expresa en {unit} y acá ya están multiplicados por " +
                   $"{ChatBalanceAmount(scale)} (el resumen ejecutivo puede citarlos en {unit}, como están impresos).";
        }

        return "Importes en pesos, como los expresa el documento.";
    }

    /// <summary>"- Label: amount" for every key of <paramref name="keys"/> holding a number, in order.</summary>
    private static List<string> ChatBalanceScalarLines(JsonObject source, IEnumerable<string> keys)
    {
        var lines = new List<string>();
        foreach (var key in keys)
        {
            if (ChatBalanceNumber(source[key]) is { } value)
                lines.Add($"- {BalanceSuggestionLabels.FieldLabels[key]}: {ChatBalanceAmount(value)}");
        }
        return lines;
    }

    /// <summary>"- label: amount" per rubro with a numeric amount, capped at <see cref="MaxRubrosPerList"/>.</summary>
    private static List<string> ChatBalanceItemLines(JsonNode? node)
    {
        var lines = new List<string>();
        if (node is not JsonArray items)
            return lines;

        var omitted = 0;
        foreach (var item in items)
        {
            if (item is not JsonObject line || ChatBalanceNumber(line["amount"]) is not { } amount)
                continue;

            if (lines.Count == MaxRubrosPerList)
            {
                omitted++;
                continue;
            }

            var label = line["label"] is JsonValue labelValue && labelValue.TryGetValue<string>(out var text)
                ? text
                : null;
            lines.Add($"- {ClampRubroLabel(label)}: {ChatBalanceAmount(amount)}");
        }

        if (omitted > 0)
            lines.Add(omitted == 1
                ? $"- (y 1 rubro más no listado; se muestran los primeros {MaxRubrosPerList})"
                : $"- (y {omitted} rubros más no listados; se muestran los primeros {MaxRubrosPerList})");

        return lines;
    }

    /// <summary>
    /// One line, whitespace collapsed (a newline inside a label would open a new prompt block), at most
    /// <see cref="MaxRubroLabelChars"/> characters plus "…", cut at a space so no token is split.
    /// </summary>
    internal static string ClampRubroLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "(sin nombre)";

        var sb = new StringBuilder(label.Length);
        var pendingSpace = false;
        foreach (var c in label)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }

        var clean = sb.ToString();
        if (clean.Length == 0)
            return "(sin nombre)";
        if (clean.Length <= MaxRubroLabelChars)
            return clean;

        // Cut at the last space that keeps the text within the cap; a single unbroken token is cut at the cap.
        var cut = clean.LastIndexOf(' ', MaxRubroLabelChars);
        var kept = cut > 0 ? clean[..cut] : clean[..MaxRubroLabelChars];
        return kept + "…";
    }

    /// <summary>A JSON number as a double; null for anything else (string, bool, null, object, missing).</summary>
    private static double? ChatBalanceNumber(JsonNode? node) =>
        node is JsonValue value
        && value.GetValueKind() == JsonValueKind.Number
        && value.TryGetValue<double>(out var number)
        && double.IsFinite(number)
            ? number
            : null;

    /// <summary>Plain invariant shortest round-trip form, as the JSON blocks beside it print numbers.</summary>
    private static string ChatBalanceAmount(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
