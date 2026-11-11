-- ============================================================================
-- Script  : 060_analyses_status_add_preapproved.sql
-- Purpose : Widen CK_Analyses_Status to admit N'PreApproved', the state an
--           analysis enters when an analyst commits to an amount ABOVE their own
--           approval limit and it is waiting on a RiskManager's confirmation.
-- Date    : 2026-09-04
-- Author  : Claude (assisted)
-- Notes   : THIS SCRIPT IS REQUIRED, not optional. [dbo].[Analyses].[Status] IS
--           constrained — CK_Analyses_Status, created in 001_initial_schema —
--           and its current list is exactly six values:
--
--             Queued, Processing, PendingReview, Reviewed, Approved, Rejected
--
--           'PreApproved' is not among them, so without this script every
--           pre-approval fails on the constraint. No earlier script has ever
--           altered this constraint; 024's header notes it did not have to,
--           because 'Approved' and 'Rejected' were already whitelisted.
--
--           This is the exception to the project's usual preference. 029, 036,
--           046 and 052 all deliberately validate an evolving vocabulary in the
--           API rather than in the schema. [Status] is the older, stricter case:
--           the constraint already exists, dropping it altogether would be a
--           much larger change than this feature warrants, and unlike a vendor
--           catalogue this vocabulary is a state machine whose members are known
--           in advance. So the list is widened, not abolished.
--
--           NO NEW STATE FOR "RETURNED TO THE ANALYST". A RiskManager who
--           declines to confirm but does not want to reject can send the analysis
--           back for rework, and that destino is the EXISTING 'PendingReview' —
--           already legal, already the status a freshly created analysis carries,
--           and already exactly what it means. Inventing a second name for a
--           state that exists would have cost a second value here and a second
--           branch in every consumer.
--
--           WITH CHECK is deliberate, following 043. Every existing row already
--           satisfies the WIDER list — this only adds a value, it removes none —
--           so validation cannot fail on legitimate data. If it does fail, some
--           unexpected value is already present and failing loudly is correct.
--
--           The drop is guarded on sys.check_constraints and prints in both
--           branches; the re-add is unconditional and immediately follows, so a
--           re-run leaves exactly one constraint with the full list. The value
--           list is written in the newer IN (N'...') form used by 039 and 043
--           rather than 001's SSMS-generated chained-OR form; they are equivalent.
--
--           ⚠ RUN THIS IN THE SAME SITTING AS 058 AND 059, BEFORE THE DEPLOY.
--           058 adds the columns the pre-approval record is written to, 059 adds
--           the limits table that decides when a pre-approval happens, and this
--           script admits the state. Any one of the three alone leaves the
--           feature broken:
--             - 058 + 059 without 060 → every pre-approval fails on the CHECK.
--             - 060 without 058       → the state is legal but has nowhere to
--                                       record who pre-approved or at what amount.
--           Order among the three does not otherwise matter; they touch different
--           objects and none depends on another's data.
--
--           ⚠ PORTAL FIRST. This is the same situation as 040, which introduced
--           'Aprobada' on [CreditLineOperations]: a Portal that does not know
--           'PreApproved' has no branch for it in its status renderer. Deploy the
--           Portal before this API.
--
--           Idempotent: safe to re-run. Re-running rewrites the constraint with
--           the same list and changes no row.
-- ============================================================================

-- ── CK_Analyses_Status : admit 'PreApproved' ────────────────────────────────
-- Drop and recreate, as scripts 009, 012 and 043 did for CK_ApiCallLogs_Service.
-- There is no ALTER for a CHECK constraint's expression in T-SQL.

IF EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Analyses_Status'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        DROP CONSTRAINT [CK_Analyses_Status];
    PRINT 'Constraint CK_Analyses_Status dropped.';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Analyses_Status not present — nothing to drop.';
END
GO

ALTER TABLE [dbo].[Analyses] WITH CHECK
    ADD CONSTRAINT [CK_Analyses_Status]
    CHECK ([Status] IN (N'Queued', N'Processing', N'PendingReview',
                        N'Reviewed', N'Approved', N'Rejected', N'PreApproved'));
GO

PRINT 'Constraint CK_Analyses_Status recreated with PreApproved allowed.';
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The constraint exists, is enabled and is TRUSTED → 1 row
--
--    SELECT name, is_disabled, is_not_trusted, definition
--    FROM   sys.check_constraints
--    WHERE  name = N'CK_Analyses_Status'
--      AND  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]');
--    -- expected: CK_Analyses_Status  0  0
--    -- and the definition listing all SEVEN values. is_not_trusted = 1 would
--    -- mean the WITH CHECK validation was skipped — investigate before deploying.
--
-- 2. Exactly ONE check constraint governs [Status] (a failed re-run could
--    conceivably leave a duplicate under a different name) → 1 row
--
--    SELECT cc.name
--    FROM   sys.check_constraints cc
--    JOIN   sys.columns c ON c.object_id = cc.parent_object_id
--                        AND c.column_id = cc.parent_column_id
--    WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
--      AND  c.name = N'Status';
--    -- expected: CK_Analyses_Status, and nothing else.
--
-- 3. No row is left holding a value outside the new list. Should return NOTHING.
--
--    SELECT [Status], COUNT(*) AS Analyses
--    FROM   [dbo].[Analyses]
--    WHERE  [Status] NOT IN (N'Queued', N'Processing', N'PendingReview',
--                            N'Reviewed', N'Approved', N'Rejected', N'PreApproved')
--    GROUP  BY [Status];
--    -- expected: 0 rows.
--
-- 4. The new value is genuinely accepted. Run inside an explicit transaction and
--    ROLL BACK — this must not leave a fabricated pre-approval behind:
--
--    BEGIN TRANSACTION;
--        UPDATE TOP (1) [dbo].[Analyses] SET [Status] = N'PreApproved';
--        -- expected: 1 row affected, no constraint violation
--    ROLLBACK TRANSACTION;
--
-- 5. After the deploy, the distribution of states shows the flow working:
--
--    SELECT [Status], COUNT(*) AS Analyses
--    FROM   [dbo].[Analyses]
--    GROUP  BY [Status]
--    ORDER  BY Analyses DESC;
--    -- 'PreApproved' rows are analyses AWAITING a second signature. A count that
--    -- only grows means nobody is confirming them — a process problem, not a
--    -- schema one, but this is where it shows up first.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Narrowing this constraint FAILS while any row still holds 'PreApproved', which
-- is the correct behaviour: those analyses are mid-decision and silently
-- rewriting them would fabricate a decision nobody made. Resolve them first —
-- through the API, by confirming or rejecting each one, which is the honest
-- route — and only then narrow the list.
--
-- To see what is outstanding:
--
--    SELECT [AnalysisId], [PreApprovedBy], [PreApprovedAt],
--           [PreApprovedAmount], [PreApprovedCurrency]
--    FROM   [dbo].[Analyses]
--    WHERE  [Status] = N'PreApproved';
--
-- Once that returns no rows:
--
--    ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [CK_Analyses_Status];
--    GO
--    ALTER TABLE [dbo].[Analyses] WITH CHECK
--        ADD CONSTRAINT [CK_Analyses_Status]
--        CHECK ([Status] IN (N'Queued', N'Processing', N'PendingReview',
--                            N'Reviewed', N'Approved', N'Rejected'));
--    GO
--
-- If those analyses genuinely cannot be decided through the API, returning them
-- to the analyst is the least destructive alternative — it discards no final
-- decision, because a pre-approved analysis has none yet:
--
--    UPDATE [dbo].[Analyses] SET [Status] = N'PendingReview'
--    WHERE  [Status] = N'PreApproved';
--
-- Roll the application back BEFORE narrowing either way, or the running code
-- will keep writing 'PreApproved' and start failing on the constraint.
-- ============================================================================
