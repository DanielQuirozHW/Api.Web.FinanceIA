-- ============================================================================
-- Script  : 035_credit_lines_add_raise_columns.sql
-- Purpose : Add nullable LastRaisedAt + LastRaiseAnalysisId columns to
--           [dbo].[CreditLines] to support raising (ampliación) a cupo in place.
-- Date    : 2026-08-03
-- Author  : Claude (assisted)
-- Notes   : A cupo (tope) is raised in place — the same row keeps its id and its
--           operations; only Amount changes. These two columns record the raise:
--           LastRaisedAt        -> UTC timestamp of the most recent raise
--           LastRaiseAnalysisId -> the Approved analysis that justifies the current amount
--           The freshness rule for a raise is: the analysis must be Approved and
--           created after COALESCE(LastRaisedAt, CreatedAt), so the same analysis can
--           never justify two raises.
--           Both nullable, no default, no backfill: existing cupos and cupos never
--           raised stay NULL (COALESCE then falls back to CreatedAt). Written by the
--           application only. No FK on LastRaiseAnalysisId (mirrors
--           [dbo].[CreditLineOperations].AnalysisId, which has none). No DROP.
--           Idempotent: each column is guarded so the script can be re-run safely.
-- ============================================================================

-- ── LastRaisedAt ────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLines]')
      AND  name      = N'LastRaisedAt'
)
BEGIN
    ALTER TABLE [dbo].[CreditLines]
        ADD [LastRaisedAt] DATETIME2 NULL;
    PRINT 'Column LastRaisedAt added to [dbo].[CreditLines].';
END
ELSE
BEGIN
    PRINT 'Column LastRaisedAt already exists on [dbo].[CreditLines] — skipped.';
END
GO

-- ── LastRaiseAnalysisId ─────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLines]')
      AND  name      = N'LastRaiseAnalysisId'
)
BEGIN
    ALTER TABLE [dbo].[CreditLines]
        ADD [LastRaiseAnalysisId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column LastRaiseAnalysisId added to [dbo].[CreditLines].';
END
ELSE
BEGIN
    PRINT 'Column LastRaiseAnalysisId already exists on [dbo].[CreditLines] — skipped.';
END
GO
