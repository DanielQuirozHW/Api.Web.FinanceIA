-- ============================================================================
-- Script  : 031_analyses_drop_creditline_columns.sql
-- Purpose : Drop the five legacy per-analysis credit-line columns from
--           [dbo].[Analyses]: ApprovedAmount, ApprovedCurrency,
--           CreditLineValidUntil, CreditLineNotes, CreditLineType.
-- Date    : 2026-07-20
-- Author  : Claude (assisted)
-- Notes   : These columns (added in 024 + 029) were a first-pass design that
--           NEVER reached production with real data. The granted credit line now
--           lives exclusively in the CreditLines / CreditLineOperations tables
--           (030_credit_lines_tables.sql). No data migration is needed.
--           Each column is dropped idempotently: any dependent DEFAULT constraint
--           is resolved and dropped first (a column cannot be dropped while a
--           constraint references it), then the column itself. The add scripts
--           created these as plain NULL columns with no named DEFAULT, so in
--           practice there is likely nothing to drop — the lookup is defensive in
--           case a default was added out-of-band.
-- ============================================================================

-- ── ApprovedAmount ──────────────────────────────────────────────────────────
DECLARE @ApprovedAmountDefault NVARCHAR(200);
SELECT @ApprovedAmountDefault = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE  c.object_id = OBJECT_ID(N'[dbo].[Analyses]')
  AND  c.name      = N'ApprovedAmount';

IF @ApprovedAmountDefault IS NOT NULL
    EXEC('ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [' + @ApprovedAmountDefault + ']');

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ApprovedAmount'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        DROP COLUMN [ApprovedAmount];
    PRINT 'Column ApprovedAmount dropped from [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ApprovedAmount does not exist on [dbo].[Analyses] — skipped.';
END
GO

-- ── ApprovedCurrency ────────────────────────────────────────────────────────
DECLARE @ApprovedCurrencyDefault NVARCHAR(200);
SELECT @ApprovedCurrencyDefault = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE  c.object_id = OBJECT_ID(N'[dbo].[Analyses]')
  AND  c.name      = N'ApprovedCurrency';

IF @ApprovedCurrencyDefault IS NOT NULL
    EXEC('ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [' + @ApprovedCurrencyDefault + ']');

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ApprovedCurrency'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        DROP COLUMN [ApprovedCurrency];
    PRINT 'Column ApprovedCurrency dropped from [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ApprovedCurrency does not exist on [dbo].[Analyses] — skipped.';
END
GO

-- ── CreditLineValidUntil ────────────────────────────────────────────────────
DECLARE @CreditLineValidUntilDefault NVARCHAR(200);
SELECT @CreditLineValidUntilDefault = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE  c.object_id = OBJECT_ID(N'[dbo].[Analyses]')
  AND  c.name      = N'CreditLineValidUntil';

IF @CreditLineValidUntilDefault IS NOT NULL
    EXEC('ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [' + @CreditLineValidUntilDefault + ']');

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'CreditLineValidUntil'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        DROP COLUMN [CreditLineValidUntil];
    PRINT 'Column CreditLineValidUntil dropped from [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column CreditLineValidUntil does not exist on [dbo].[Analyses] — skipped.';
END
GO

-- ── CreditLineNotes ─────────────────────────────────────────────────────────
DECLARE @CreditLineNotesDefault NVARCHAR(200);
SELECT @CreditLineNotesDefault = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE  c.object_id = OBJECT_ID(N'[dbo].[Analyses]')
  AND  c.name      = N'CreditLineNotes';

IF @CreditLineNotesDefault IS NOT NULL
    EXEC('ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [' + @CreditLineNotesDefault + ']');

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'CreditLineNotes'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        DROP COLUMN [CreditLineNotes];
    PRINT 'Column CreditLineNotes dropped from [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column CreditLineNotes does not exist on [dbo].[Analyses] — skipped.';
END
GO

-- ── CreditLineType ──────────────────────────────────────────────────────────
DECLARE @CreditLineTypeDefault NVARCHAR(200);
SELECT @CreditLineTypeDefault = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE  c.object_id = OBJECT_ID(N'[dbo].[Analyses]')
  AND  c.name      = N'CreditLineType';

IF @CreditLineTypeDefault IS NOT NULL
    EXEC('ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [' + @CreditLineTypeDefault + ']');

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'CreditLineType'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        DROP COLUMN [CreditLineType];
    PRINT 'Column CreditLineType dropped from [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column CreditLineType does not exist on [dbo].[Analyses] — skipped.';
END
GO
