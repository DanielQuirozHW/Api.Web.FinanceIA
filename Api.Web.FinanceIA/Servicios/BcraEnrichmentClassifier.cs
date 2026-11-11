// Project : Api.Web.FinanceIA
// Created : 2026-08-17
// Modified: 2026-08-17

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The verdict on one analysis's BCRA enrichment: what happened, and — when it went wrong — a short
/// es-AR sentence an analyst can read. <see cref="Notes"/> is <c>null</c> for every outcome except
/// <see cref="BcraEnrichmentOutcome.Degraded"/>.
/// </summary>
public sealed record BcraEnrichmentAssessment(BcraEnrichmentOutcome Outcome, string? Notes);

/// <summary>
/// Decides whether an analysis's BCRA enrichment completed, was never applicable, or degraded.
///
/// <para>
/// This exists because <c>BcraService.GetBcraDataForAnalysisAsync</c> returns a non-null
/// <see cref="BcraData"/> whose <c>Deuda.Estado</c> is <c>LookupFailed</c> whenever the Central de
/// Deudores call fails but macro or cambiario succeeded — a payload that looks enriched, is
/// persisted as enriched, and yet costs the analysis the entire 0.35 BCRA component weight once
/// <c>ScoringService</c> renormalises around it. Callers that only null-check the payload cannot
/// see that. This classifier looks at <c>Estado</c>, so they can.
/// </para>
///
/// <para>
/// The ordering of its guards is load-bearing. An unresolved entity has a null CUIT, so testing the
/// CUIT first would classify a failed entity lookup as <c>NotApplicable</c> — a row denying it was
/// penalised while carrying the full penalty. <c>NotApplicable</c> is reserved for a RESOLVED entity
/// with no CUIT, where the exclusion is permanent rather than a retryable failure.
/// </para>
///
/// <para>
/// A failed CHEQUES lookup (score credibility 1b-1) is Degraded only when the cheques penalty takes part
/// in the score (<c>chequesCount</c>): it reads exactly like a clean record to the engine, so the score
/// may be missing that penalty. The caller learns whether it counts from the engine itself
/// (<c>ScoringResult.ChequesEvaluated</c>), which is why the pipeline and the re-score re-assess after
/// scoring (<c>AnalysisScoringStage.AssessAfterScoring</c>). No new outcome value: Degraded is one of
/// the three the DDL 039 check allows.
/// </para>
///
/// <para>
/// Pure and static: no I/O, no logging, no dependency on <c>Entity</c> (it takes the CUIT, not the
/// entity). That keeps it directly unit-testable without standing up a controller.
/// </para>
/// </summary>
public static class BcraEnrichmentClassifier
{
    /// <summary>
    /// The entity itself could not be resolved, so BCRA was never queried. Covers both a repository
    /// error and an EntityId that does not resolve for this tenant.
    /// </summary>
    public const string NotesEntityUnresolved =
        "No se pudo resolver la entidad, por lo que no se consultó el BCRA. El score no incluye ese componente.";

    /// <summary>An exception escaped the BCRA call itself.</summary>
    public const string NotesThrew =
        "La consulta al BCRA falló con un error inesperado.";

    /// <summary>Every BCRA source failed, so no payload came back at all.</summary>
    public const string NotesNoData =
        "No se pudo obtener información del BCRA: la consulta de deuda, macro y cambiario falló.";

    /// <summary>A payload came back, but the Central de Deudores part of it did not.</summary>
    public const string NotesDeudaUnavailable =
        "No se pudo consultar la Central de Deudores del BCRA.";

    /// <summary>
    /// The deuda answered, but the rejected-cheques lookup did not, while the cheques penalty counts for
    /// this tenant: the score may be missing a penalty it would otherwise carry.
    /// </summary>
    public const string NotesChequesUnavailable =
        "No se pudo consultar la base de cheques rechazados del BCRA: el score no incluye una posible penalidad por cheques.";

    /// <summary>
    /// Classifies the outcome of a BCRA enrichment attempt.
    /// </summary>
    /// <param name="entityUnresolved">
    /// Whether the entity lookup failed to produce an entity — either it threw, or it returned null
    /// because the EntityId does not resolve for this tenant. Both mean BCRA was never queried while
    /// the score was still computed without it, so both are
    /// <see cref="BcraEnrichmentOutcome.Degraded"/>.
    /// </param>
    /// <param name="cuit">
    /// The resolved entity's CUIT. Blank or whitespace on a RESOLVED entity means there is genuinely
    /// nothing to look up — <see cref="BcraEnrichmentOutcome.NotApplicable"/>, an absence of input
    /// rather than a failure.
    /// </param>
    /// <param name="bcraLookupThrew">Whether the BCRA call threw. Checked before <paramref name="bcraData"/>, which is then meaningless.</param>
    /// <param name="bcraData">The payload returned by the BCRA call, or <c>null</c>.</param>
    /// <param name="chequesCount">
    /// Whether the cheques penalty actually takes part in this score: the tenant's effective
    /// <c>bcra_cheques_enabled</c> is on and the BCRA component is not excluded. Only then can a failed
    /// cheques lookup change the score, so only then is it <see cref="BcraEnrichmentOutcome.Degraded"/>.
    /// A deuda failure is checked first and keeps precedence (its note wins).
    /// </param>
    public static BcraEnrichmentAssessment Classify(
        bool      entityUnresolved,
        string?   cuit,
        bool      bcraLookupThrew,
        BcraData? bcraData,
        bool      chequesCount)
    {
        // MUST precede the CUIT check. An unresolved entity necessarily has a null CUIT, so testing
        // the CUIT first would short-circuit to NotApplicable and claim BCRA was never applicable —
        // while ScoringService, seeing a null payload, still excluded the component and renormalised
        // its 0.35 weight away. The row would be penalised and deny it. This ordering IS the fix.
        if (entityUnresolved)
            return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.Degraded, NotesEntityUnresolved);

        // The entity resolved and simply has no CUIT on file. BCRA is excluded here too, but that
        // exclusion is permanent and unavoidable rather than transient — see BcraEnrichmentOutcome.
        if (string.IsNullOrWhiteSpace(cuit))
            return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.NotApplicable, null);

        if (bcraLookupThrew)
            return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.Degraded, NotesThrew);

        // Null only when deuda AND macro AND cambiario all failed.
        if (bcraData is null)
            return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.Degraded, NotesNoData);

        // The case the null-check alone misses: a payload that arrived, carries macro or cambiario,
        // and is persisted as if enriched — while the deuda block that drives the score is absent
        // or failed.
        if (bcraData.Deuda is null || bcraData.Deuda.Estado == BcraDeudaConsultaEstado.LookupFailed)
            return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.Degraded, NotesDeudaUnavailable);

        // The deuda is a real answer. A failed cheques lookup carries zero amounts, byte-identical to a
        // clean record, so the engine applies no penalty either way — which is only a loss when the
        // penalty counts. With it switched off for the tenant the score is exactly what it would be.
        if (chequesCount
            && (bcraData.Cheques is null || bcraData.Cheques.Estado == BcraChequesConsultaEstado.LookupFailed))
            return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.Degraded, NotesChequesUnavailable);

        // DebtFound or NoDebtReported. Both are real answers; "no debt" is a good one, not a missing one.
        return new BcraEnrichmentAssessment(BcraEnrichmentOutcome.Complete, null);
    }
}
