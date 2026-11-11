-- ============================================================================
-- Script  : 024_analyses_add_creditline_columns.sql
-- Purpose : Add the granted credit-line columns to [dbo].[Analyses], populated
--           only when an analysis is Approved via PATCH /api/analyses/{id}/decision.
-- Date    : 2026-07-13
-- Author  : Claude (assisted)
-- Notes   : All four columns are nullable and remain NULL for existing rows and
--           for Rejected decisions. ApprovedAmount uses decimal(18,2) to match
--           [RequestedAmount]. ApprovedCurrency is an ISO 4217 code (e.g. 'ARS',
--           'USD'). CreditLineValidUntil is the vigencia (validity expiry).
--           REVIEW: CreditLineValidUntil is the FIRST use of the DATE type in
--           this database — every other temporal column is datetime2(7). DATE is
--           intentional here (date-only expiry, no time component). DBA to confirm.
--           No change to CK_Analyses_Status is required: 'Approved' and 'Rejected'
--           are already whitelisted by CK_Analyses_Status.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ApprovedAmount'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [ApprovedAmount] DECIMAL(18, 2) NULL;
    PRINT 'Column ApprovedAmount added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ApprovedAmount already exists on [dbo].[Analyses] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ApprovedCurrency'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [ApprovedCurrency] NVARCHAR(3) NULL;
    PRINT 'Column ApprovedCurrency added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ApprovedCurrency already exists on [dbo].[Analyses] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'CreditLineValidUntil'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [CreditLineValidUntil] DATE NULL;
    PRINT 'Column CreditLineValidUntil added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column CreditLineValidUntil already exists on [dbo].[Analyses] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'CreditLineNotes'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [CreditLineNotes] NVARCHAR(1000) NULL;
    PRINT 'Column CreditLineNotes added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column CreditLineNotes already exists on [dbo].[Analyses] — skipped.';
END
GO
