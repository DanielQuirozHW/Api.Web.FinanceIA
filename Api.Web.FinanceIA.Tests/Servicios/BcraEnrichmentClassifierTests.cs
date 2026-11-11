// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-17

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers <see cref="BcraEnrichmentClassifier"/>, which decides whether an analysis's BCRA
/// enrichment completed, was never applicable, or degraded.
///
/// <para>
/// The distinction is load-bearing, not cosmetic. A degraded lookup makes ScoringService drop the
/// BCRA component and renormalise its 0.35 weight onto ratios and macro, which moves the composite
/// materially. Before this classifier existed, that row looked exactly like a fully-enriched one —
/// and looked exactly like an entity that simply had no CUIT, which is not penalised at all.
/// </para>
///
/// <para>
/// The case worth reading twice is <c>Classify_WhenPayloadArrivesButDeudaLookupFailed_IsDegraded</c>:
/// a non-null <see cref="BcraData"/> carrying macro data but a failed deuda block. That is the
/// shape a plain null-check reports as success, and the reason this type exists.
/// </para>
///
/// No stubs needed — the classifier is pure and static.
/// </summary>
public sealed class BcraEnrichmentClassifierTests
{
    private const string Cuit = "30712345678";

    // ── Fixture helpers ───────────────────────────────────────────────────────

    private static BcraData Deuda(BcraDeudaConsultaEstado estado) =>
        new() { Deuda = new BcraDeudaData { Estado = estado } };

    // The dangerous shape: macro succeeded, so BcraService returned a payload rather than null,
    // but the Central de Deudores block — the part that drives the score — failed.
    private static BcraData PartialPayloadWithFailedDeuda() =>
        new()
        {
            Deuda = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.LookupFailed },
            Macro = new BcraMacroData { InflacionMensual = 2.5 }
        };

    // ── Degraded: the entity itself never resolved ────────────────────────────
    //
    // Regression guard for a review finding on PR #132. The entity lookup in TriggerAsync is
    // fault-isolated and leaves `entity` null on failure, so Classify received a null CUIT and
    // returned NotApplicable — while ScoringService, seeing a null payload, excluded BCRA and
    // renormalised its 0.35 weight away. The row was penalised and claimed BCRA was never
    // applicable. Ordering entityUnresolved ahead of the CUIT check is the fix.

    [Fact]
    public void Classify_WhenEntityUnresolved_IsDegraded_DespiteNullCuit()
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: true, cuit: null, bcraLookupThrew: false, bcraData: null, chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesEntityUnresolved, result.Notes);

        // Spelled out because this exact value is the bug being fixed, not merely a wrong label:
        // the score IS missing the BCRA component here.
        Assert.NotEqual(BcraEnrichmentOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public void Classify_WhenEntityUnresolved_WinsOverEveryOtherSignal()
    {
        // Even with a CUIT in hand and a healthy-looking payload, an unresolved entity means the
        // enrichment cannot be trusted — nothing downstream had an entity to work from.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: true,
            cuit: Cuit,
            bcraLookupThrew: false,
            bcraData: Deuda(BcraDeudaConsultaEstado.DebtFound), chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesEntityUnresolved, result.Notes);
    }

    // ── NotApplicable: the entity resolved, and genuinely has no CUIT ─────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_WhenEntityResolvedWithNoCuit_IsNotApplicable_WithNoNotes(string? cuit)
    {
        // entityUnresolved: false is the whole point — the fix above must not over-correct and
        // swallow the legitimate case. Nothing was asked because there was nothing to ask.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: cuit, bcraLookupThrew: false, bcraData: null, chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.NotApplicable, result.Outcome);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void Classify_WhenEntityResolvedWithNoCuit_IsNotApplicableEvenThoughPayloadIsNull()
    {
        // A null payload with no CUIT on a RESOLVED entity is a structural absence, not a failure.
        // Marking it Degraded would claim a BCRA outage that never happened.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: null, bcraLookupThrew: false, bcraData: null, chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.NotApplicable, result.Outcome);
    }

    // ── Degraded: the three BCRA-side causes ──────────────────────────────────

    [Fact]
    public void Classify_WhenBcraLookupThrew_IsDegraded_WithThrewNotes()
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: true, bcraData: null, chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesThrew, result.Notes);
    }

    [Fact]
    public void Classify_WhenPayloadIsNull_IsDegraded_WithNoDataNotes()
    {
        // BcraService returns null only when deuda AND macro AND cambiario all failed.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false, bcraData: null, chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesNoData, result.Notes);
    }

    [Fact]
    public void Classify_WhenPayloadHasNoDeudaBlock_IsDegraded_WithDeudaUnavailableNotes()
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false, bcraData: new BcraData(), chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesDeudaUnavailable, result.Notes);
    }

    [Fact]
    public void Classify_WhenPayloadArrivesButDeudaLookupFailed_IsDegraded()
    {
        // THE case this type exists for. A plain `bcraData is not null` check calls this enriched;
        // it is not. The score was computed without the BCRA component.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false,
            bcraData: PartialPayloadWithFailedDeuda(), chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesDeudaUnavailable, result.Notes);
    }

    [Fact]
    public void Classify_BcraThrewTakesPrecedenceOverPayloadInspection()
    {
        // If the call threw, whatever is in the payload variable is not a trustworthy answer.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: true,
            bcraData: Deuda(BcraDeudaConsultaEstado.DebtFound), chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesThrew, result.Notes);
    }

    // ── Complete: both real answers ───────────────────────────────────────────

    [Theory]
    [InlineData(BcraDeudaConsultaEstado.DebtFound)]
    [InlineData(BcraDeudaConsultaEstado.NoDebtReported)]
    public void Classify_WhenBcraAnswered_IsComplete_WithNoNotes(BcraDeudaConsultaEstado estado)
    {
        // NoDebtReported is a real answer and a GOOD one — a CUIT with no reported debt. It must
        // never be conflated with a failed lookup, which is the confusion this whole change targets.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false, bcraData: Deuda(estado), chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Complete, result.Outcome);
        Assert.Null(result.Notes);
    }

    // ── Cheques (score credibility 1b-1, V4) ─────────────────────────────────
    //
    // A failed cheques lookup carries zero amounts, byte-identical to a clean record, so the engine
    // applies no penalty either way. That is a loss exactly when the penalty takes part in the score
    // (chequesCount: the tenant's effective bcra_cheques_enabled is on and BCRA is not excluded) —
    // then the enrichment is Degraded. With the penalty off, the score is exactly what it would have
    // been, and the enrichment stays Complete.

    private static BcraData DeudaWithCheques(BcraDeudaConsultaEstado deuda, BcraChequesData? cheques)
    {
        var payload = Deuda(deuda);
        payload.Cheques = cheques;
        return payload;
    }

    private static BcraChequesData Cheques(BcraChequesConsultaEstado estado) => new() { Estado = estado };

    [Theory]
    [InlineData(BcraDeudaConsultaEstado.DebtFound)]
    [InlineData(BcraDeudaConsultaEstado.NoDebtReported)]
    public void Classify_WhenChequesLookupFailedAndChequesCount_IsDegraded_WithChequesNotes(BcraDeudaConsultaEstado deuda)
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false,
            bcraData: DeudaWithCheques(deuda, Cheques(BcraChequesConsultaEstado.LookupFailed)),
            chequesCount: true);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesChequesUnavailable, result.Notes);
    }

    [Fact]
    public void Classify_WhenChequesBlockMissingAndChequesCount_IsDegraded()
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false,
            bcraData: DeudaWithCheques(BcraDeudaConsultaEstado.DebtFound, null),
            chequesCount: true);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesChequesUnavailable, result.Notes);
    }

    [Theory]
    [InlineData(BcraChequesConsultaEstado.LookupFailed)]
    [InlineData(BcraChequesConsultaEstado.ChequesFound)]
    [InlineData(BcraChequesConsultaEstado.NoChequesReported)]
    public void Classify_WhenChequesDoNotCount_IsComplete_WhateverTheChequesSay(BcraChequesConsultaEstado estado)
    {
        // bcra_cheques_enabled = 0 (or BCRA excluded): a cheques outage changes nothing in the score.
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false,
            bcraData: DeudaWithCheques(BcraDeudaConsultaEstado.DebtFound, Cheques(estado)),
            chequesCount: false);

        Assert.Equal(BcraEnrichmentOutcome.Complete, result.Outcome);
        Assert.Null(result.Notes);
    }

    [Theory]
    [InlineData(BcraChequesConsultaEstado.ChequesFound)]
    [InlineData(BcraChequesConsultaEstado.NoChequesReported)]
    public void Classify_WhenChequesAnsweredAndChequesCount_IsComplete(BcraChequesConsultaEstado estado)
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false,
            bcraData: DeudaWithCheques(BcraDeudaConsultaEstado.NoDebtReported, Cheques(estado)),
            chequesCount: true);

        Assert.Equal(BcraEnrichmentOutcome.Complete, result.Outcome);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void Classify_WhenDeudaAndChequesBothFailed_DeudaNoteWins()
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: Cuit, bcraLookupThrew: false,
            bcraData: DeudaWithCheques(BcraDeudaConsultaEstado.LookupFailed, Cheques(BcraChequesConsultaEstado.LookupFailed)),
            chequesCount: true);

        Assert.Equal(BcraEnrichmentOutcome.Degraded, result.Outcome);
        Assert.Equal(BcraEnrichmentClassifier.NotesDeudaUnavailable, result.Notes);
    }

    [Fact]
    public void Classify_WhenNoCuit_IsNotApplicable_EvenWithChequesCount()
    {
        var result = BcraEnrichmentClassifier.Classify(
            entityUnresolved: false, cuit: null, bcraLookupThrew: false, bcraData: null, chequesCount: true);

        Assert.Equal(BcraEnrichmentOutcome.NotApplicable, result.Outcome);
    }

    // ── Cross-cutting guarantees ──────────────────────────────────────────────

    [Fact]
    public void Classify_NotesAreNonNull_ExactlyWhenDegraded()
    {
        (bool Unresolved, string? Cuit, bool Threw, BcraData? Data)[] cases =
        [
            (true,  null,  false, null),                                          // Degraded
            (true,  Cuit,  false, Deuda(BcraDeudaConsultaEstado.DebtFound)),       // Degraded
            (false, null,  false, null),                                          // NotApplicable
            (false, "  ",  false, Deuda(BcraDeudaConsultaEstado.DebtFound)),       // NotApplicable
            (false, Cuit,  true,  null),                                          // Degraded
            (false, Cuit,  false, null),                                          // Degraded
            (false, Cuit,  false, new BcraData()),                                // Degraded
            (false, Cuit,  false, Deuda(BcraDeudaConsultaEstado.LookupFailed)),    // Degraded
            (false, Cuit,  false, Deuda(BcraDeudaConsultaEstado.DebtFound)),       // Complete
            (false, Cuit,  false, Deuda(BcraDeudaConsultaEstado.NoDebtReported)),  // Complete
        ];

        foreach (var (unresolved, cuit, threw, data) in cases)
        {
            var result = BcraEnrichmentClassifier.Classify(unresolved, cuit, threw, data, chequesCount: false);

            Assert.Equal(result.Outcome == BcraEnrichmentOutcome.Degraded, result.Notes is not null);
        }
    }

    [Fact]
    public void Classify_DegradedNotes_AreDistinctPerCause_AndFitTheColumn()
    {
        var entity  = BcraEnrichmentClassifier.Classify(true,  null, false, null, false).Notes;
        var threw   = BcraEnrichmentClassifier.Classify(false, Cuit, true,  null, false).Notes;
        var noData  = BcraEnrichmentClassifier.Classify(false, Cuit, false, null, false).Notes;
        var noDeuda = BcraEnrichmentClassifier.Classify(false, Cuit, false, new BcraData(), false).Notes;
        var cheques = BcraEnrichmentClassifier.Classify(
            false, Cuit, false,
            DeudaWithCheques(BcraDeudaConsultaEstado.DebtFound, Cheques(BcraChequesConsultaEstado.LookupFailed)),
            true).Notes;

        // Distinct, or an analyst cannot tell "the entity vanished" from "BCRA errored" from
        // "BCRA was silent".
        Assert.Equal(5, new HashSet<string?> { entity, threw, noData, noDeuda, cheques }.Count);

        // EnrichmentNotes is NVARCHAR(500); a longer value would raise a truncation error on INSERT
        // and take down analysis creation for the very failure it was describing.
        foreach (var notes in new[] { entity, threw, noData, noDeuda, cheques })
            Assert.InRange(notes!.Length, 1, 500);
    }
}
