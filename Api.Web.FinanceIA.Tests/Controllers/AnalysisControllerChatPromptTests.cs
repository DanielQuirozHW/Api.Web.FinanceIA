// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-27

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers <see cref="AnalysisController.BuildBcraChatSummary"/> — the seam that strips
/// <c>cheques.detalle</c> and <c>deuda.historialMontosPorSituacion</c> out of the persisted BCRA
/// blob before it goes into the chat system prompt, which is rebuilt and re-sent to Claude on
/// EVERY turn of every conversation.
///
/// <para>
/// Three things are load-bearing. The first is the omission: <c>detalle</c> is a flat per-cheque
/// array (~295 bytes a row, hundreds to thousands of rows on a cheque-heavy CUIT) that was being
/// re-billed per turn, joined by the per-situación split of the debt series, which exists only so
/// the portal can stack a bar chart. The second — the one easiest to regress silently — is that
/// NOTHING ELSE is lost, which is why the deuda/macro/cambiario tests assert equality against the
/// input's own subtree rather than spot-checking a few fields. The third is that CUIT and company
/// name never reach the prompt at all: that is a hard rule, not an optimisation.
/// </para>
///
/// <para>
/// The fixtures are SERIALISED FROM THE REAL MODELS, never written as JSON literals. The helper
/// removes keys by name, so renaming <see cref="BcraChequesData.Detalle"/>'s
/// <c>[JsonPropertyName]</c> would silently put the whole array back into every prompt; building
/// the fixture through the same attribute turns that into a red test instead. Do not rewrite these
/// fixtures as inline JSON.
/// </para>
///
/// <para>
/// Both helpers are reached directly as internal statics via the assembly's InternalsVisibleTo —
/// no controller construction, no stubs, no DI.
/// </para>
///
/// <para>
/// This file also pins the shape of the LECTURA DEL FLUJO block: that it is ADDITIVE to the raw
/// cash-flow JSON (which still ships, still first, still with its own disclaimer), that its header
/// is emitted UNCONDITIONALLY so the prompt shape never varies, and that the reading is rendered
/// VERBATIM. The narrative arrives as an argument, so the block's rendering is testable here with
/// no DI; the CALL-SITE wiring and its fault isolation are pinned separately in
/// <c>AnalysisControllerChatCashFlowTests</c>, which needs a real controller.
/// </para>
///
/// <para>
/// And it pins the ADJUNTOS DEL ANALISTA block, whose absence was the endpoint's worst failure: an
/// attached spreadsheet is ephemeral, so on a follow-up turn the model held nothing and — never
/// having been told the channel existed — reconstructed the cash flow, reporting invented monthly
/// disbursements as if read from the file. The two branches must therefore read as opposite
/// statements of FACT rather than as two shades of one caution, which is what
/// <c>The_two_attachment_branches_are_mutually_exclusive</c> exists to keep true. That the flag
/// actually leaves the call site is pinned in <c>AnalysisControllerChatAttachmentTurnTests</c>.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatPromptTests
{
    private const string Cuit        = "30123456789";
    private const string RazonSocial = "ACME SERVICIOS SRL";

    private static readonly DateTime Fetched = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static BcraData MakeBcraData(List<BcraChequeDetalle>? detalle) => new()
    {
        Deuda = new BcraDeudaData
        {
            Identificacion       = 30123456789,
            Denominacion         = RazonSocial,
            SituacionMaxima      = 3,
            MontoTotal           = 4820.75m,
            EntidadesInformantes = new List<string> { "BANCO NACION", "BANCO GALICIA" },
            PeriodoConsultado    = "202506",
            HistorialSituaciones = new List<BcraSituacionPeriodo>
            {
                new() { Periodo = "202506", SituacionMaxima = 3 },
                new() { Periodo = "202505", SituacionMaxima = 2 }
            },
            HistorialMontos = new List<BcraMontoPeriodo>
            {
                new() { Periodo = "202506", MontoTotal = 4820.75m },
                new() { Periodo = "202505", MontoTotal = 3910.00m }
            },
            HistorialMontosPorSituacion = new List<BcraMontoSituacionPeriodo>
            {
                // Bands that sum to the montoTotal above them, as the mapper guarantees.
                new()
                {
                    Periodo = "202506", MontoSituacion1 = 0m, MontoSituacion23 = 4820.75m
                },
                new()
                {
                    Periodo = "202505", MontoSituacion1 = 100m, MontoSituacion23 = 3810.00m
                }
            },
            EntidadesDetalle = new List<BcraEntidadDeudaDetalle>
            {
                new() { Entidad = "BANCO NACION",  Situacion = 3, Monto = 4000.00m, DiasAtrasoPago = 95 },
                new() { Entidad = "BANCO GALICIA", Situacion = 2, Monto =  820.75m, DiasAtrasoPago = 45 }
            },
            MaxDiasAtraso = 95,
            Estado        = BcraDeudaConsultaEstado.DebtFound,
            FetchedAt     = Fetched
        },
        Macro     = new BcraMacroData     { InflacionMensual  = 2.1,    TasaReferencia = 29.5, FetchedAt = Fetched },
        Cambiario = new BcraCambiarioData { TipoCambioOficial = 1185.5,                        FetchedAt = Fetched },
        Cheques   = new BcraChequesData
        {
            Identificacion       = 30123456789,
            Denominacion         = RazonSocial,
            TotalRechazados      = detalle?.Count ?? 0,
            TotalImpagos         = 2,
            MontoTotalRechazado  = 255000.50m,
            MontoTotalImpago     = 175000.50m,
            FechaPrimerRechazo   = new DateTime(2025, 3, 15),
            FechaUltimoRechazo   = new DateTime(2025, 9,  2),
            TieneProcesoJudicial = true,
            EnRevision           = false,
            Causales             = new List<BcraChequeCausalResumen>
            {
                new()
                {
                    Causal          = "SIN FONDOS SUFICIENTES",
                    Cantidad        = 2,
                    Monto           = 230000.50m,
                    CantidadImpagos = 2
                }
            },
            Detalle   = detalle,
            Estado    = BcraChequesConsultaEstado.ChequesFound,
            FetchedAt = Fetched
        }
    };

    private static List<BcraChequeDetalle> MakeDetalle(int count) =>
        Enumerable.Range(0, count).Select(i => new BcraChequeDetalle
        {
            Causal        = "SIN FONDOS SUFICIENTES",
            Entidad       = 11,
            NroCheque     = 12345670 + i,
            FechaRechazo  = new DateTime(2025, 3, 15).AddDays(i),
            Monto         = 25000m + i,
            FechaPago     = null,
            EstadoMulta   = "IMPAGA",
            DenomJuridica = RazonSocial,
            Pagado        = false
        }).ToList();

    /// <summary>
    /// Serialised with the DEFAULT options, exactly as the controller persists it (line 335) —
    /// so what these tests feed the helper is byte-identical to what production reads back.
    /// </summary>
    private static string Blob(int chequeCount) =>
        JsonSerializer.Serialize(MakeBcraData(MakeDetalle(chequeCount)));

    /// <summary>
    /// A BalanceJson blob shaped as ClaudeService persists it: scalars plus a cash_flow node
    /// serialised from the REAL CashFlowData, so renaming a [JsonPropertyName] breaks this test
    /// instead of silently changing what the prompt ships. Never write this as a JSON literal.
    /// </summary>
    private static string BalanceBlobWith(CashFlowData cashFlow) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["assets"]      = 1_000_000d,
            ["liabilities"] =   400_000d,
            ["cash_flow"]   = cashFlow,
        });

    /// <summary>The JSON line, with the trailing elision note (when present) stripped off.</summary>
    private static string JsonPart(string summary) =>
        summary.Split('\n')[0];

    // ── The elision ───────────────────────────────────────────────────────────

    [Fact]
    public void Elides_per_cheque_detail_but_keeps_cheque_aggregates()
    {
        var summary = AnalysisController.BuildBcraChatSummary(Blob(3));

        // The array, and everything only it carries, is gone.
        Assert.DoesNotContain("\"detalle\"", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("nroCheque",   summary, StringComparison.Ordinal);
        Assert.DoesNotContain("12345670",    summary, StringComparison.Ordinal);
        Assert.DoesNotContain("denomJuridica", summary, StringComparison.Ordinal);

        // Every aggregate the note promises the model is still there.
        Assert.Contains("totalRechazados",        summary, StringComparison.Ordinal);
        Assert.Contains("montoTotalRechazado",    summary, StringComparison.Ordinal);
        Assert.Contains("fechaPrimerRechazo",     summary, StringComparison.Ordinal);
        Assert.Contains("causales",               summary, StringComparison.Ordinal);
        Assert.Contains("SIN FONDOS SUFICIENTES", summary, StringComparison.Ordinal);
        Assert.Contains("tieneProcesoJudicial",   summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Cheque_aggregate_values_survive_unchanged()
    {
        var summary = AnalysisController.BuildBcraChatSummary(Blob(3));
        var cheques = JsonNode.Parse(JsonPart(summary))!["cheques"]!;

        Assert.Equal(3,          (int)cheques["totalRechazados"]!);
        Assert.Equal(2,          (int)cheques["totalImpagos"]!);
        Assert.Equal(255000.50m, (decimal)cheques["montoTotalRechazado"]!);
        Assert.True((bool)cheques["tieneProcesoJudicial"]!);
        Assert.Single(cheques["causales"]!.AsArray());
        Assert.Null(cheques["detalle"]);
    }

    [Fact]
    public void Summary_is_a_fraction_of_the_raw_blob_on_a_realistic_row()
    {
        var blob    = Blob(500);
        var summary = AnalysisController.BuildBcraChatSummary(blob);

        // The whole reason this seam exists: the blob is re-billed on every chat turn.
        Assert.True(summary.Length < blob.Length / 4,
            $"expected a large reduction, got {blob.Length} -> {summary.Length}");
    }

    // ── PII never reaches a Claude prompt ─────────────────────────────────────

    [Fact]
    public void Drops_the_cuit_and_the_company_name_from_both_blocks()
    {
        var summary = AnalysisController.BuildBcraChatSummary(Blob(3));

        Assert.DoesNotContain(Cuit,             summary, StringComparison.Ordinal);
        Assert.DoesNotContain(RazonSocial,      summary, StringComparison.Ordinal);
        Assert.DoesNotContain("identificacion", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("denominacion",   summary, StringComparison.Ordinal);
    }

    // ── Everything else passes through untouched ──────────────────────────────

    [Fact]
    public void Deuda_passes_through_byte_for_byte_apart_from_the_pii_keys_and_the_chart_split()
    {
        var blob    = Blob(3);
        var summary = AnalysisController.BuildBcraChatSummary(blob);

        var before = JsonNode.Parse(blob)!["deuda"]!.AsObject();
        before.Remove("identificacion");
        before.Remove("denominacion");
        before.Remove("historialMontosPorSituacion");

        var after = JsonNode.Parse(JsonPart(summary))!["deuda"]!;

        // Not a spot check: deuda is the credit core and entidadesDetalle stays whole.
        Assert.Equal(before.ToJsonString(), after.ToJsonString());
    }

    [Fact]
    public void Drops_the_stacked_chart_split_but_keeps_the_series_it_was_split_from()
    {
        var summary = AnalysisController.BuildBcraChatSummary(Blob(3));

        var deuda = JsonNode.Parse(JsonPart(summary))!["deuda"]!;

        // 24 rows of three decimals apiece, re-billed on every turn of every conversation, for a
        // split that only a stacked bar chart reads.
        Assert.Null(deuda["historialMontosPorSituacion"]);
        Assert.DoesNotContain("montoSituacion", summary, StringComparison.Ordinal);

        // Scoped, not collateral: the totals series it was split out of, and the severity history
        // beside it, both still reach the model — that is why it costs the prompt nothing to lose.
        Assert.Equal(2, deuda["historialMontos"]!.AsArray().Count);
        Assert.Equal(2, deuda["historialSituaciones"]!.AsArray().Count);
    }

    [Fact]
    public void Macro_and_cambiario_pass_through_byte_for_byte()
    {
        var blob    = Blob(3);
        var summary = AnalysisController.BuildBcraChatSummary(blob);

        var before = JsonNode.Parse(blob)!;
        var after  = JsonNode.Parse(JsonPart(summary))!;

        Assert.Equal(before["macro"]!.ToJsonString(),     after["macro"]!.ToJsonString());
        Assert.Equal(before["cambiario"]!.ToJsonString(), after["cambiario"]!.ToJsonString());
    }

    [Fact]
    public void Keeps_camel_case_keys_and_the_numeric_estado()
    {
        var summary = AnalysisController.BuildBcraChatSummary(Blob(3));

        // No naming policy and no enum converter are registered, and JsonNode re-emits the raw
        // token text — so keys must not turn PascalCase nor the enum into a string.
        Assert.Contains("\"totalRechazados\":", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalRechazados", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ChequesFound",    summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DebtFound",       summary, StringComparison.Ordinal);
    }

    // ── The elision note ──────────────────────────────────────────────────────

    [Fact]
    public void Emits_the_elision_note_so_the_model_never_claims_it_lacks_cheque_data()
    {
        var summary = AnalysisController.BuildBcraChatSummary(Blob(3));

        Assert.Contains("cheques.detalle", summary, StringComparison.Ordinal);
        Assert.Contains("NO afirmes que no tenés información de cheques",
            summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_detalle_emits_no_note()
    {
        // null detalle means "legacy stored data" — nothing was withheld, so claiming the analyst
        // can see rows that do not exist would be false.
        var summary = AnalysisController.BuildBcraChatSummary(
            JsonSerializer.Serialize(MakeBcraData(null)));

        Assert.DoesNotContain("cheques.detalle", summary, StringComparison.Ordinal);
        Assert.Contains("totalRechazados",       summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_detalle_emits_no_note()
    {
        var summary = AnalysisController.BuildBcraChatSummary(
            JsonSerializer.Serialize(MakeBcraData(new List<BcraChequeDetalle>())));

        Assert.DoesNotContain("\"detalle\"",     summary, StringComparison.Ordinal);
        Assert.DoesNotContain("cheques.detalle", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Blob_without_a_cheques_node_is_unannotated()
    {
        var data = MakeBcraData(null);
        data.Cheques = null;

        var summary = AnalysisController.BuildBcraChatSummary(JsonSerializer.Serialize(data));

        Assert.DoesNotContain("cheques.detalle", summary, StringComparison.Ordinal);
        Assert.Contains("situacionMaxima",       summary, StringComparison.Ordinal);
    }

    // ── Degradation, never a throw ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Returns_no_disponible_when_blob_absent(string? blob) =>
        Assert.Equal("No disponible", AnalysisController.BuildBcraChatSummary(blob));

    [Theory]
    [InlineData("{ not json")]       // malformed       -> JsonException
    [InlineData("{\"cheques\": ")]   // truncated       -> JsonException
    [InlineData("null")]             // literal null    -> parses to a null node
    [InlineData("42")]               // bare scalar     -> JsonValue
    [InlineData("\"texto\"")]        // bare string     -> JsonValue
    [InlineData("[1,2,3]")]          // top-level array -> indexing it would THROW
    public void Degrades_to_no_disponible_without_throwing(string blob) =>
        Assert.Equal("No disponible", AnalysisController.BuildBcraChatSummary(blob));

    [Fact]
    public void Empty_object_survives_as_itself() =>
        Assert.Equal("{}", AnalysisController.BuildBcraChatSummary("{}"));

    // ── End to end, through the prompt the chat endpoint actually sends ───────

    [Fact]
    public void Chat_system_prompt_carries_the_summary_not_the_raw_blob()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult { BcraDataJson = Blob(3) }, Array.Empty<string>(),
            hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains("CONTEXTO BCRA:",  prompt, StringComparison.Ordinal);
        Assert.Contains("totalRechazados", prompt, StringComparison.Ordinal);
        Assert.Contains("cheques.detalle", prompt, StringComparison.Ordinal);

        // The whole point: the per-cheque array never reaches the model, nor does the PII.
        Assert.DoesNotContain("nroCheque", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(RazonSocial, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Cuit,        prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_system_prompt_still_says_no_disponible_when_bcra_is_absent()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Array.Empty<string>(), hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains("CONTEXTO BCRA:\nNo disponible",
            prompt, StringComparison.Ordinal);

        // No cheque rows were withheld here, so the note must not appear either.
        Assert.DoesNotContain("cheques.detalle", prompt, StringComparison.Ordinal);
    }

    // ── LECTURA DEL FLUJO block ───────────────────────────────────────────────

    private static readonly string[] Reading =
    {
        "El flujo operativo fue positivo: la operación generó $ 558.367.419.",
        "En actividades de inversión se aplicaron $ 756.406.949.",
    };

    [Fact]
    public void The_reading_is_rendered_verbatim_one_sentence_per_line()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment: false, reliability: null, fiscalYear: null);

        // Verbatim, in order, one per line: no bullets, no rewording, no lead sentence of our own.
        // Byte identity with the service's output is the point — the model must reason over the
        // audited text the analyst sees, not a prompt-side paraphrase of it.
        Assert.Contains(
            "LECTURA DEL FLUJO (determinística):\n" +
            Reading[0] + "\n" +
            Reading[1] + "\n",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_survives_when_there_is_no_reading()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Array.Empty<string>(), hasAttachment: false, reliability: null, fiscalYear: null);

        // Never omitted: a stable prompt shape, and an explicit "No disponible" the model can act
        // on rather than a silently missing concept it would answer around.
        Assert.Contains(
            "LECTURA DEL FLUJO (determinística):\nNo disponible",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_entry_never_becomes_a_stray_block_delimiter()
    {
        // Empty lines are this prompt's ONLY block delimiter, so a blank sentence would make
        // everything after it read as an unlabelled new block. An all-blank reading must still
        // land on "No disponible", not on an empty region under the header.
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), new[] { "   ", "" }, hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains(
            "LECTURA DEL FLUJO (determinística):\nNo disponible",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_raw_cash_flow_json_block_survives_untouched_beside_the_reading()
    {
        var cashFlow = new CashFlowData
        {
            Present   = true,
            Operating =  558_367_419,
            Investing = -756_406_949,
        };

        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult { BalanceJson = BalanceBlobWith(cashFlow) }, Reading,
            hasAttachment: false, reliability: null, fiscalYear: null);

        // ADDITIVE: the raw block is still the serialised CashFlowData, still followed by its own
        // disclaimer, and still rendered BEFORE the reading.
        Assert.Contains(
            "FLUJO DE EFECTIVO:" + "\n" + JsonSerializer.Serialize(cashFlow),
            prompt, StringComparison.Ordinal);
        Assert.Contains("NO se utilizó en el cálculo del score de riesgo",
            prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf("FLUJO DE EFECTIVO:", StringComparison.Ordinal) <
            prompt.IndexOf("LECTURA DEL FLUJO", StringComparison.Ordinal),
            "The reading must be rendered AFTER the raw cash-flow JSON.");
    }

    [Theory]
    [InlineData("{ not json")]  // malformed        -> JsonException
    [InlineData("null")]        // literal null     -> ValueKind.Null
    [InlineData("42")]          // bare scalar      -> ValueKind.Number
    [InlineData("\"texto\"")]   // bare string      -> ValueKind.String
    [InlineData("[1,2,3]")]     // top-level array  -> ValueKind.Array
    public void A_non_object_balance_blob_degrades_without_throwing(string balanceJson)
    {
        // The RAW cash-flow block is the ONE TryExtractCashFlow call site with no try/catch of its
        // own, and ChatAsync reaches it after the analyst's turn is already persisted. Every blob
        // below the first one PARSES cleanly, so a JsonException-only catch does not cover them:
        // reading a property off a non-object root throws InvalidOperationException, which used to
        // escape as a 500 and strand the user message. Mirrors the BCRA block's
        // Degrades_to_no_disponible_without_throwing above.
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult { BalanceJson = balanceJson }, Reading, hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains(
            "FLUJO DE EFECTIVO:\nNo disponible",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reading_block_states_it_is_rule_based_and_not_a_scoring_input()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment: false, reliability: null, fiscalYear: null);

        // "determinística" in the header invites the model to read this as a computed scoring
        // signal, and the rounded figures invite it to report a discrepancy against the exact
        // doubles one block above. All three corrections are load-bearing.
        Assert.Contains("motor de reglas determinístico", prompt, StringComparison.Ordinal);
        Assert.Contains("redondeadas a pesos enteros",    prompt, StringComparison.Ordinal);
        Assert.Contains("TAMPOCO se utilizó en el cálculo del score de riesgo",
            prompt, StringComparison.Ordinal);
    }

    // ── ADJUNTOS DEL ANALISTA (planillas) ─────────────────────────────────────
    //
    // A spreadsheet the analyst attaches reaches the model ONCE: it rides the final user turn as a
    // second text block and is never written to ChatMessages.Content — which is what `history` is
    // rebuilt from — so it is gone from `history` on the very next turn. (Script 055 stores it in its
    // own column so the analyst can read it back; nothing rebuilds a prompt from that column.)
    // The model cannot perceive that loss — nothing in the prompt used to say the channel existed —
    // so asked a follow-up about the sheet it reconstructed one, reporting invented monthly
    // disbursements as if read from the file. These tests pin the block that stops it.

    private const string AttachmentHeader  = "ADJUNTOS DEL ANALISTA (planillas):";
    private const string AttachmentPresent = "En ESTE turno el analista adjuntó una planilla";
    private const string AttachmentAbsent  = "En ESTE turno NO recibiste ninguna planilla adjunta.";

    [Fact]
    public void Without_an_attachment_the_prompt_forbids_reconstructing_a_cash_flow()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains(AttachmentHeader + "\n" + AttachmentAbsent,
            prompt, StringComparison.Ordinal);

        // A prohibition, not a caution. The named categories are the ones actually observed being
        // fabricated — a monthly disbursement schedule with dates and amounts — so they are spelled
        // out rather than left to "datos".
        Assert.Contains("tenés PROHIBIDO inventar, inferir, reconstruir o estimar un flujo de fondos",
            prompt, StringComparison.Ordinal);
        Assert.Contains("desembolsos mensuales", prompt, StringComparison.Ordinal);

        // And an explicit ACTION for the case it cannot answer: say the file did not arrive and ask
        // for it back. Without this the model has a prohibition but no permitted move, which is the
        // pressure that produces an "ilustrativo" example instead.
        Assert.Contains("no recibiste el archivo en este turno", prompt, StringComparison.Ordinal);
        Assert.Contains("que lo vuelva a adjuntar",              prompt, StringComparison.Ordinal);
        Assert.Contains("No ofrezcas un ejemplo, una estimación ni un flujo \"ilustrativo\"",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void With_an_attachment_the_prompt_says_one_arrived_this_turn()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment: true, reliability: null, fiscalYear: null);

        Assert.Contains(AttachmentHeader + "\n" + AttachmentPresent,
            prompt, StringComparison.Ordinal);

        // Where it is: a separate block on the user turn, BELOW the system prompt — matching how
        // ClaudeService.ChatAsync actually assembles the payload.
        Assert.Contains("en un bloque separado del mensaje del analista",
            prompt, StringComparison.Ordinal);

        // Provenance must stay visible in the answer: an attachment figure and an analysis figure
        // are different evidence, and merging them silently is its own species of fabrication.
        Assert.Contains("decí explícitamente que proviene", prompt, StringComparison.Ordinal);

        // Still ephemeral, and the model is told so on the very turn it can read the file — this is
        // the only turn on which that warning can reach it before the data disappears.
        Assert.Contains("existe sólo en este turno", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_attachment_header_and_the_general_rule_survive_in_both_branches(bool hasAttachment)
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment, reliability: null, fiscalYear: null);

        // Same invariant as The_header_survives_when_there_is_no_reading: the prompt's SHAPE never
        // varies turn to turn, so an absent block can never be read as an absent concept.
        Assert.Contains(AttachmentHeader, prompt, StringComparison.Ordinal);

        // The closing rule is unconditional too — it is what covers every figure that is neither in
        // the analysis blocks nor in this turn's attachment.
        Assert.Contains(
            "nunca presentes como dato una cifra que no esté en los bloques de arriba o en el " +
            "adjunto de este turno",
            prompt, StringComparison.Ordinal);
        Assert.Contains("Siempre es correcto decir que un dato no está disponible",
            prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_two_attachment_branches_are_mutually_exclusive(bool hasAttachment)
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment, reliability: null, fiscalYear: null);

        // The branches must read as opposite statements of FACT, never as two shades of the same
        // caution. A future rewording that let both — or neither — appear would leave the model
        // guessing whether it holds a spreadsheet, which is the exact ambiguity the flag removes.
        Assert.Equal(hasAttachment,  prompt.Contains(AttachmentPresent, StringComparison.Ordinal));
        Assert.Equal(!hasAttachment, prompt.Contains(AttachmentAbsent,  StringComparison.Ordinal));
    }

    [Fact]
    public void The_attachment_block_never_displaces_the_existing_closing_paragraph()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment: false, reliability: null, fiscalYear: null);

        // ADDITIVE: the original style/anti-invention paragraph is untouched and still last.
        Assert.Contains("No inventes datos que no estén en el análisis.",
            prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf(AttachmentHeader, StringComparison.Ordinal) <
            prompt.IndexOf("Respondé siempre en español", StringComparison.Ordinal),
            "The attachment block must be rendered BEFORE the closing paragraph.");
    }

    // ── Idioma: es-AR sin anglicismos ─────────────────────────────────────────
    //
    // The chat was the only surface in the product that said "Leverage": the PDF export and the
    // scoring narrative both read "Apalancamiento" from their own glossaries, and this controller's
    // RatioKeySpanish — a third, hand-maintained copy of the same eight keys — had been left
    // untranslated on that one entry. The analyst therefore saw the two documents disagree on the
    // name of a ratio. These two tests pin both halves of the fix: the label the prompt is BUILT
    // with, and the instruction that keeps the model from re-introducing the English term on its own.

    [Fact]
    public void The_breakdown_block_names_the_leverage_ratio_in_Spanish()
    {
        // A breakdown blob in the shape the controller persists. Invariant literals so a
        // comma-decimal test host cannot produce a blob that fails to parse into a false pass.
        const string blob = """
            {
              "scoreRatios": 21.00, "scoreBcra": 51.00, "scoreMacro": 30.00,
              "breakdown": {
                "leverage": { "score": 18.00, "status": "Critico" },
                "endeudamiento": { "score": 44.00, "status": "Atencion" }
              },
              "scoreBeforeFloor": 20.00, "appliedFloor": 25.00,
              "criticalRatios": ["leverage"]
            }
            """;

        string block = AnalysisController.FormatScoreBreakdownBlock(blob, finalScore: 25.00m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        // Both consumers of the glossary: the per-ratio line and the critical-floor callout.
        Assert.Contains("Apalancamiento: ", block, StringComparison.Ordinal);
        Assert.Contains("en nivel crítico: Apalancamiento.", block, StringComparison.Ordinal);

        // Must match ClaudeService's glossary exactly — that is the whole point of the fix.
        Assert.Equal(
            "Apalancamiento",
            global::Api.Web.FinanceIA.Servicios.ClaudeService.RatioDisplayLabel["leverage"]);

        // The raw key survives only where a label is genuinely missing, never for this one.
        Assert.DoesNotContain("Leverage", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_closing_paragraph_pins_es_AR_and_names_the_anglicisms_to_avoid()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), Reading, hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains("español rioplatense (es-AR)", prompt, StringComparison.Ordinal);

        // Naming the offenders is what makes the instruction actionable; a bare "escribí en
        // español" had already failed to stop "leverage" from coming back in the reply.
        Assert.Contains("\"apalancamiento\" y no \"leverage\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"cash flow\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"patrimonio neto\" y no \"equity\"", prompt, StringComparison.Ordinal);

        // ADDITIVE: it rides the existing closing paragraph rather than a new block of its own.
        Assert.True(
            prompt.IndexOf("Respondé siempre en español", StringComparison.Ordinal) <
            prompt.IndexOf("español rioplatense (es-AR)", StringComparison.Ordinal),
            "The es-AR sentence must sit inside the existing closing paragraph.");
    }

    /// <summary>
    /// Score credibility 1b-2a: the chat's breakdown block reads the keys it knows. A breakdown carrying
    /// <c>reliabilityFacts</c> renders the same prose as one without, and none of the facts reaches the
    /// prompt. (Until 1b-3 the fixture also carried a per-ratio <c>measured:false</c> and pinned it as
    /// ignored; 1b-3 renders that ratio "no medido" on purpose — see AnalysisControllerChatReliabilityTests —
    /// so the key left this fixture.)
    /// </summary>
    [Fact]
    public void The_breakdown_block_ignores_the_reliability_facts()
    {
        const string before = """
            {"scoreRatios":21.00,"scoreBcra":51.00,"scoreMacro":30.00,
             "breakdown":{"leverage":{"score":50.00,"status":"Atencion","excluded":false,"effectiveWeight":0.10}},
             "scoreBeforeFloor":20.00,"appliedFloor":0,"criticalRatios":[]}
            """;
        const string after = """
            {"scoreRatios":21.00,"scoreBcra":51.00,"scoreMacro":30.00,
             "breakdown":{"leverage":{"score":50.00,"status":"Atencion","excluded":false,"effectiveWeight":0.10}},
             "scoreBeforeFloor":20.00,"appliedFloor":0,"criticalRatios":[],
             "reliabilityFacts":{"version":1,"bcraSituacionUnreadable":false,"macroImputed":true,"sectorFallback":true,"sectorRaw":"Agropecuario","configSource":"defaults","configHash":"0f1e2d3c4b5a6978","ratiosSource":"computed","fiscalYear":2024,"lufe":{"ventasEstado":"carried_forward","ventasUltimoPeriodo":"202608"}}}
            """;

        string? expected = AnalysisController.FormatScoreBreakdownBlock(before, finalScore: 20.00m);
        string? actual   = AnalysisController.FormatScoreBreakdownBlock(after,  finalScore: 20.00m);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("Agropecuario", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("carried_forward", actual, StringComparison.Ordinal);
    }
}
