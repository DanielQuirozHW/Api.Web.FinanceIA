-- ============================================================================
-- Script  : 039_analysisresults_add_enrichment_status.sql
-- Purpose : Add [EnrichmentStatus] and [EnrichmentNotes] to
--           [dbo].[AnalysisResults], so a failed enrichment is distinguishable
--           from an entity that legitimately had nothing to look up.
-- Date    : 2026-08-17
-- Author  : Claude (assisted)
-- Notes   : A transient BCRA outage costs an analysis the ENTIRE 0.35 BCRA
--           component weight: ScoringService excludes the component and
--           renormalises the remaining weights onto ratios and macro, which
--           makes the composite strictly worse (on the order of 100 NOSIS
--           points, varying with the ratios score). Until now the resulting row
--           was indistinguishable from a fully-enriched one — the only trace was
--           a `bcraExcluded` boolean buried in the free-form ScoreBreakdownJson
--           blob, and that boolean cannot tell "BCRA was down" apart from "this
--           entity has no CUIT".
--
--           EnrichmentStatus makes that distinction first-class and queryable:
--             Complete      — BCRA answered (debt found, or no debt reported).
--             NotApplicable — the entity resolved and has no CUIT on file, so
--                             there was never anything to query.
--             Degraded      — we tried and failed, and a retry could succeed:
--                             the entity could not be resolved, or BCRA threw,
--                             returned nothing, or returned LookupFailed. This
--                             is the row an analyst must not read as a clean one.
--
--           NotApplicable and Degraded carry the SAME numeric penalty — both are
--           scored without the BCRA component and both trigger the identical
--           renormalisation. What separates them is PERMANENCE, not magnitude: a
--           NotApplicable analysis can never score differently, because with no
--           CUIT no BCRA data can ever exist for it, whereas a Degraded one would
--           on a retry that succeeded. Only the second is provisional.
--
--           EnrichmentNotes carries a short es-AR sentence for the Degraded case
--           only, written for an analyst rather than for a log reader.
--
--           DEFAULT 'Complete' BACKFILLS EVERY EXISTING ROW. That is a
--           deliberate, and imperfect, choice: the column is NOT NULL (so EF can
--           map it as a required string), and no honest per-row value can be
--           reconstructed for history — see the optional backfill at the bottom
--           of this script for why. Treat EnrichmentStatus as trustworthy only
--           for analyses created AFTER this script ran.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE. Both columns
--           are mapped in AnalysisResultConfiguration, and EnrichmentStatus is
--           NOT NULL, so EF Core emits them in every SELECT and INSERT against
--           AnalysisResults. Deploying the code against a database without these
--           columns takes analysis creation AND retrieval down with "Invalid
--           column name". There is no application-level try/catch that can
--           protect against this. Same failure mode as CatalogueAlerts in 038.
--
--           Idempotent: both columns and the check constraint are guarded, so
--           the script can be re-run safely.
-- ============================================================================

-- ── AnalysisResults.EnrichmentStatus ────────────────────────────────────────
-- NOT NULL with a default so existing rows backfill in the same statement.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'EnrichmentStatus'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [EnrichmentStatus] NVARCHAR(20) NOT NULL
            CONSTRAINT [DF_AnalysisResults_EnrichmentStatus] DEFAULT (N'Complete');
    PRINT 'Column EnrichmentStatus added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column EnrichmentStatus already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

-- ── AnalysisResults.EnrichmentNotes ─────────────────────────────────────────
-- Nullable: only a Degraded outcome has anything to say. 500 chars is ample for
-- the fixed set of es-AR diagnostics the API writes (longest is ~90 chars) and
-- leaves room for a future one without a schema change.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'EnrichmentNotes'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [EnrichmentNotes] NVARCHAR(500) NULL;
    PRINT 'Column EnrichmentNotes added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column EnrichmentNotes already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

-- ── Vocabulary guard ────────────────────────────────────────────────────────
-- The three values are a closed set written from BcraEnrichmentOutcome.ToString().
-- Constrained in the schema (unlike CreditLines.Status, see 029/036, which is
-- validated in the API) because this column is read to decide whether a score can
-- be trusted: a typo must fail the INSERT, not quietly produce an unknown status
-- that no consumer branches on.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  object_id = OBJECT_ID(N'[dbo].[CK_AnalysisResults_EnrichmentStatus]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisResults_EnrichmentStatus]
        CHECK ([EnrichmentStatus] IN (N'Complete', N'NotApplicable', N'Degraded'));
    PRINT 'Check constraint [CK_AnalysisResults_EnrichmentStatus] added.';
END
ELSE
BEGIN
    PRINT 'Check constraint [CK_AnalysisResults_EnrichmentStatus] already exists — skipped.';
END
GO

-- ============================================================================
-- OPTIONAL BACKFILL — NOT RUN BY THIS SCRIPT. DBA DECISION.
-- ============================================================================
--
-- The statement below marks historical rows Degraded where the persisted score
-- breakdown recorded that BCRA was excluded from the composite.
--
-- READ THIS BEFORE RUNNING IT. IT OVER-MARKS, AND IT CANNOT BE MADE PRECISE.
--
-- `bcraExcluded` is true in BOTH of the situations this column exists to
-- separate:
--
--   • BCRA was queried and failed          → genuinely Degraded.
--   • the entity had no CUIT to query      → NotApplicable, and its score was
--                                            never penalised for a failure.
--
-- Pre-039 rows carry nothing that distinguishes the two — the discriminator is
-- the CUIT presence at analysis time, which was never persisted on the result
-- and may since have changed on the entity. So running this marks a population
-- of legitimately-NotApplicable analyses as Degraded, which is the same class of
-- false claim the column was added to eliminate, pointed the other way.
--
-- Rows created after 039 are written correctly by the API and must NOT be
-- touched by this statement — hence the GeneratedAt cutoff, which the DBA must
-- set to the timestamp at which this script was applied.
--
-- Choose deliberately: leaving every historical row at 'Complete' understates
-- how many were degraded; running this overstates it. Neither is the truth, and
-- the truth is not recoverable. The default is to do nothing.
--
-- UPDATE [dbo].[AnalysisResults]
-- SET    [EnrichmentStatus] = N'Degraded',
--        [EnrichmentNotes]  = N'Marcado retroactivamente: el score se calculó sin el componente BCRA. No es posible determinar si la consulta falló o si la entidad no tenía CUIT.'
-- WHERE  [ScoreBreakdownJson] IS NOT NULL
--   AND  ISJSON([ScoreBreakdownJson]) = 1
--   AND  JSON_VALUE([ScoreBreakdownJson], '$.bcraExcluded') = 'true'
--   AND  [GeneratedAt] < '<<SET THIS TO THE UTC TIMESTAMP AT WHICH 039 WAS APPLIED>>';
-- GO
