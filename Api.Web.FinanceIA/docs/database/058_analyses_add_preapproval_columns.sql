-- ============================================================================
-- Script  : 058_analyses_add_preapproval_columns.sql
-- Purpose : Add the SECOND decision record and the amount trail to
--           [dbo].[Analyses], so a credit decision can carry two signatures —
--           an analyst's pre-approval and a RiskManager's confirmation —
--           instead of collapsing both into one reviewer field.
-- Date    : 2026-09-04
-- Author  : Claude (assisted)
-- Notes   : Six nullable columns, in two groups.
--
--             PreApprovedBy        — who pre-approved
--             PreApprovedAt        — when
--             PreApprovedAmount    — the amount they PROPOSED
--             PreApprovedCurrency  — its ISO 4217 code
--
--             DecisionAmount       — the amount actually COMMITTED
--             DecisionCurrency     — its ISO 4217 code
--
--           [ReviewedBy], [ReviewedAt] and [ReviewNotes] are REUSED as the
--           final-confirmation record and are NOT duplicated here. They already
--           meant "who settled this and when", which is exactly what a
--           confirmation is; a second pair of columns saying the same thing
--           would leave two candidates for "who approved this" and no rule for
--           choosing between them.
--
--           WHY BOTH AMOUNTS. They are not redundant. A RiskManager confirming
--           a pre-approval may LOWER the figure, and the pair
--           (PreApprovedAmount, DecisionAmount) is the only place that
--           adjustment is visible. Where they differ, someone overrode someone.
--           Where the pre-approval group is entirely NULL, no second signature
--           was ever needed.
--
--           ALL SIX NULLABLE, no default, no backfill. This follows 040 and 041
--           next door rather than 057: these columns record an EVENT that either
--           happened or did not, so NULL is the honest "no pre-approval was
--           made" — not a guess. Every analysis already decided under the
--           single-step model stays valid untouched, reads as having no
--           pre-approval record (which is true), and needs no data migration.
--           Contrast 057, whose flags record a CONDITION every row has an
--           answer for, which is why THOSE could be NOT NULL DEFAULT 0.
--
--           DECIMAL(18,2) for both amounts, matching [RequestedAmount] on this
--           table and [Amount] on [dbo].[CreditLines] — a confirmed decision
--           amount becomes a cupo without a precision change. NVARCHAR(3) for
--           both currencies, matching [dbo].[CreditLines].[Currency].
--
--           NOTE the deliberate avoidance of the names [ApprovedAmount] and
--           [ApprovedCurrency]. Those existed on this table once (added by 024,
--           dropped by 031) as a first-pass per-analysis credit line that never
--           reached production. Reusing them would make a re-run of 031 against
--           a stale database silently drop columns this feature depends on.
--           [DecisionAmount] / [DecisionCurrency] cannot collide with anything.
--
--           NO CHECK CONSTRAINT on either currency. The accepted codes live in
--           Clases/Currencies.cs and are validated in the API, following the
--           reasoning recorded in 036, 046 and 052: a value list in the schema
--           turns adding a currency into a migration on every tenant database.
--
--           FK_Analyses_PreApprovedBy IS declared, and this is a judgement call
--           worth stating. 049 records the rule that a column recording WHO
--           PERFORMED an action takes no foreign key (030, 040, 041, 046 all
--           omit one), because the actor may outlive the account. But on THIS
--           table the opposite precedent already governs: [RequestedBy],
--           [ReviewedBy] and [SentToSGRBy] each carry an FK from
--           001_initial_schema, and [PreApprovedBy] is the exact peer of
--           [ReviewedBy] — the two are read together as the two halves of one
--           decision. Matching the table beats matching the general rule here.
--           NO CASCADE, per 027 and 049: accounts are deactivated via
--           [IsActive], never deleted, and a cascade would silently destroy the
--           decision history if one ever were.
--
--           No index. None of the six is a predicate anywhere: they are read as
--           attributes of an analysis already located by its primary key, or
--           through the existing result -> analysis path.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on EVERY
--           tenant database. All six columns are mapped in AnalysisConfiguration,
--           and AnalysisRepository.GetByIdAsync selects the FULL entity with no
--           projection while UpdateAsync calls .Update() (a full-row write), so
--           EF Core emits them in every SELECT and every UPDATE against
--           [dbo].[Analyses]. Against a database without them, the decision
--           endpoint, the analysis detail AND the analyses list all fail with
--           "Invalid column name", and no application-level try/catch can
--           protect against it. Same deploy-order constraint as 040, 041 and
--           057. Restart the App Service after deploying so EF's model cache is
--           rebuilt.
--
--           ⚠ APPLY 060 IN THE SAME SITTING. This script only adds columns; the
--           'PreApproved' status the feature writes is refused by
--           CK_Analyses_Status until 060 widens it. 058 alone is harmless but
--           incomplete — every pre-approval would fail on the constraint.
--
--           ⚠ PORTAL FIRST, like 040 and unlike 057. This change introduces a
--           new [Status] value ('PreApproved', via 060) that the Portal's status
--           renderer has no branch for, and the API's decision payload starts
--           REQUIRING an amount and currency on an approval. A Portal deployed
--           after this API would render an unknown status and have its approve
--           call rejected with a 400.
--
--           Idempotent: every column and the foreign key are guarded, so the
--           script can be re-run safely. Re-running never rewrites a decision
--           already recorded.
-- ============================================================================

-- ── Analyses.PreApprovedBy ──────────────────────────────────────────────────
-- Nullable: an analysis approved directly, within the analyst's limit, never had
-- a pre-approver, and so does every analysis decided before this feature shipped.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'PreApprovedBy'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [PreApprovedBy] UNIQUEIDENTIFIER NULL;
    PRINT 'Column PreApprovedBy added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column PreApprovedBy already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Analyses.PreApprovedAt ──────────────────────────────────────────────────
-- datetime2(7), matching [ReviewedAt] and every other temporal column here.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'PreApprovedAt'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [PreApprovedAt] DATETIME2(7) NULL;
    PRINT 'Column PreApprovedAt added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column PreApprovedAt already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Analyses.PreApprovedAmount ──────────────────────────────────────────────
-- The amount PROPOSED. Kept even after a confirmation changes the figure — that
-- is what makes an override visible.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'PreApprovedAmount'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [PreApprovedAmount] DECIMAL(18, 2) NULL;
    PRINT 'Column PreApprovedAmount added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column PreApprovedAmount already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Analyses.PreApprovedCurrency ────────────────────────────────────────────
-- ISO 4217 code. Validated in the API against Clases/Currencies.cs, not by a
-- CHECK — see the header.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'PreApprovedCurrency'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [PreApprovedCurrency] NVARCHAR(3) NULL;
    PRINT 'Column PreApprovedCurrency added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column PreApprovedCurrency already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Analyses.DecisionAmount ─────────────────────────────────────────────────
-- The amount actually COMMITTED, alongside the reused [ReviewedBy]/[ReviewedAt].
-- Stays NULL on a rejection and while an analysis is only pre-approved: neither
-- commits anything, and a figure there would read as money that was granted.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'DecisionAmount'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [DecisionAmount] DECIMAL(18, 2) NULL;
    PRINT 'Column DecisionAmount added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column DecisionAmount already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Analyses.DecisionCurrency ───────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'DecisionCurrency'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [DecisionCurrency] NVARCHAR(3) NULL;
    PRINT 'Column DecisionCurrency added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column DecisionCurrency already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Foreign key ─────────────────────────────────────────────────────────────
-- Mirrors FK_Analyses_ReviewedBy from 001_initial_schema, whose peer this column
-- is. WITH CHECK: the column is entirely NULL at this point, so it cannot fail.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Analyses_PreApprovedBy]')
)
BEGIN
    ALTER TABLE [dbo].[Analyses] WITH CHECK
        ADD CONSTRAINT [FK_Analyses_PreApprovedBy]
            FOREIGN KEY ([PreApprovedBy]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_Analyses_PreApprovedBy] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Analyses_PreApprovedBy] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. All six columns exist, correctly typed, all nullable → 6 rows
--
--    SELECT c.name AS ColumnName, t.name AS DataType,
--           c.precision, c.scale, c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[Analyses]')
--      AND  c.name IN (N'PreApprovedBy', N'PreApprovedAt', N'PreApprovedAmount',
--                      N'PreApprovedCurrency', N'DecisionAmount', N'DecisionCurrency')
--    ORDER  BY c.name;
--    -- expected: DecisionAmount       decimal          18  2       1
--    --           DecisionCurrency     nvarchar                  6  1
--    --           PreApprovedAmount    decimal          18  2       1
--    --           PreApprovedAt        datetime2                    1
--    --           PreApprovedBy        uniqueidentifier             1
--    --           PreApprovedCurrency  nvarchar                  6  1
--    -- (max_length 6 = 3 NVARCHAR characters; is_nullable 1 on every row.)
--
-- 2. The foreign key exists and is trusted → 1 row
--
--    SELECT name, is_disabled, is_not_trusted
--    FROM   sys.foreign_keys
--    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
--      AND  name = N'FK_Analyses_PreApprovedBy';
--    -- expected: FK_Analyses_PreApprovedBy  0  0
--
-- 3. Nothing was backfilled — every pre-existing row is NULL on all six. Run
--    this IMMEDIATELY after the ALTERs, before the deploy.
--
--    SELECT COUNT(*) AS TotalAnalyses,
--           SUM(CASE WHEN [PreApprovedBy]  IS NOT NULL THEN 1 ELSE 0 END) AS WithPreApproval,
--           SUM(CASE WHEN [DecisionAmount] IS NOT NULL THEN 1 ELSE 0 END) AS WithDecisionAmount
--    FROM   [dbo].[Analyses];
--    -- expected immediately after this script: WithPreApproval = 0 and
--    --           WithDecisionAmount = 0, whatever TotalAnalyses is.
--
-- 4. After exercising the flow through the API (an analyst pre-approves above
--    their limit, a RiskManager confirms at a lower figure), both signatures are
--    present and the amounts differ → 1 row
--
--    SELECT TOP (1) [AnalysisId], [Status],
--                   [PreApprovedBy], [PreApprovedAmount], [PreApprovedCurrency],
--                   [ReviewedBy],    [DecisionAmount],    [DecisionCurrency]
--    FROM   [dbo].[Analyses]
--    WHERE  [PreApprovedBy] IS NOT NULL
--    ORDER  BY [PreApprovedAt] DESC;
--    -- expected: Status = 'Approved', PreApprovedBy <> ReviewedBy, and
--    --           DecisionAmount < PreApprovedAmount for the lowered case.
--
-- 5. Old rows still read correctly — an analysis approved under the single-step
--    model has a reviewer and no pre-approval:
--
--    SELECT COUNT(*) AS SingleStepApprovals
--    FROM   [dbo].[Analyses]
--    WHERE  [Status] = N'Approved' AND [PreApprovedBy] IS NULL;
--    -- expected: every approval that predates this release. These are valid,
--    --           not broken, and must NOT be backfilled.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: the deployed code maps all six columns, so
-- dropping them under a running deployment breaks every read and write of an
-- analysis. Drop the foreign key before its column.
--
--    ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [FK_Analyses_PreApprovedBy];
--    GO
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [DecisionCurrency];
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [DecisionAmount];
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedCurrency];
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedAmount];
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedAt];
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedBy];
--    GO
--
-- Roll 060 back too, and IN THIS ORDER — 060 first, or rather: no analysis may
-- be left in 'PreApproved' when the constraint narrows again. Export the
-- affected rows first; the pre-approval half of every two-signature decision
-- exists in no other column:
--
--    SELECT [AnalysisId], [Status], [PreApprovedBy], [PreApprovedAt],
--           [PreApprovedAmount], [PreApprovedCurrency],
--           [DecisionAmount], [DecisionCurrency]
--    FROM   [dbo].[Analyses]
--    WHERE  [PreApprovedBy] IS NOT NULL OR [Status] = N'PreApproved';
--
-- What survives a rollback is the FINAL decision on every confirmed analysis
-- ([Status], [ReviewedBy], [ReviewedAt], [ReviewNotes] are untouched here), so
-- no approval is lost — only the record of who proposed it and at what figure.
-- Any analysis still sitting in 'PreApproved' has no final decision at all and
-- must be re-decided after the rollback.
-- ============================================================================
