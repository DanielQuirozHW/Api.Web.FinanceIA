-- ============================================================================
-- Script  : 029_analyses_add_creditline_type.sql
-- Purpose : Add the SGR One Click credit-line TYPE column to [dbo].[Analyses],
--           populated only when an analysis is Approved WITH a granted credit
--           line via PATCH /api/analyses/{id}/decision.
-- Date    : 2026-07-19
-- Author  : Claude (assisted)
-- Notes   : Nullable and remains NULL for existing rows, for approvals without a
--           credit line, and for Rejected decisions. Stores the numeric SGR code
--           (stable); the human label is display-only on the frontend.
--           Valid range is 0..17 (SGR One Click catalog). NO CHECK constraint is
--           applied on purpose: the range is enforced in the API so the catalog
--           can evolve without a schema change. Complements the credit-line
--           columns added in 024_analyses_add_creditline_columns.sql.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'CreditLineType'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [CreditLineType] INT NULL;
    PRINT 'Column CreditLineType added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column CreditLineType already exists on [dbo].[Analyses] — skipped.';
END
GO
