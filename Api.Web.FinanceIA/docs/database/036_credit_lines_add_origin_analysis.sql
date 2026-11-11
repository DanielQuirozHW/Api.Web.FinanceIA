-- ============================================================================
-- Script  : 036_credit_lines_add_origin_analysis.sql
-- Purpose : Record which approved analysis justified each cupo at creation time.
--           Adds nullable [OriginAnalysisId] to [dbo].[CreditLines] plus a FK to
--           [dbo].[Analyses]([AnalysisId]).
-- Date    : 2026-08-07
-- Author  : Claude (assisted)
-- Notes   : OriginAnalysisId is the Approved analysis that justified the cupo's
--           CREATION — distinct from LastRaiseAnalysisId (035), which tracks the
--           analysis behind the most recent ampliación (raise). It is written by
--           the application on creation only.
--
--           NULLABLE PERMANENTLY — DO NOT change this column to NOT NULL, and do
--           NOT add a backfill. Pre-existing cupos were created before this field
--           existed and have NO recoverable origin analysis, so they stay NULL by
--           design. Enforcement that new cupos carry an OriginAnalysisId is done at
--           write time in the API (CreditLinesController.CreateCreditLineAsync),
--           not in the schema. This script contains NO UPDATE statement.
--
--           Unlike LastRaiseAnalysisId (035, which has no FK), this column DOES get
--           a FK — mirroring [dbo].[CreditLineOperations].AnalysisId (030), because
--           an origin analysis, when present, must reference a real analysis row.
--
--           Status vocabulary: 'Suspended' joins the [dbo].[CreditLines].[Status]
--           vocabulary in this release (Active | Superseded | Suspended). Per script
--           030, credit-line status values are validated in the API BY DESIGN rather
--           than by a CHECK constraint (there is intentionally no CK on this column),
--           so no schema change is needed to accept 'Suspended'. 'Suspended' will
--           mean the cupo accepts no new operations or raises while its existing
--           operations remain valid — NO endpoint produces it yet; a later branch
--           will. This header is the single place that split (CK on [Analyses] but
--           none here) is explained.
--
--           Idempotent: the column and the FK are each guarded so the script can be
--           re-run safely.
-- ============================================================================

-- ── OriginAnalysisId ────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLines]')
      AND  name      = N'OriginAnalysisId'
)
BEGIN
    ALTER TABLE [dbo].[CreditLines]
        ADD [OriginAnalysisId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column OriginAnalysisId added to [dbo].[CreditLines].';
END
ELSE
BEGIN
    PRINT 'Column OriginAnalysisId already exists on [dbo].[CreditLines] — skipped.';
END
GO

-- ── FK_CreditLines_Analyses_Origin ──────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_CreditLines_Analyses_Origin]')
)
BEGIN
    ALTER TABLE [dbo].[CreditLines]
        ADD CONSTRAINT [FK_CreditLines_Analyses_Origin]
            FOREIGN KEY ([OriginAnalysisId]) REFERENCES [dbo].[Analyses] ([AnalysisId]);
    PRINT 'Foreign key [FK_CreditLines_Analyses_Origin] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_CreditLines_Analyses_Origin] already exists — skipped.';
END
GO
