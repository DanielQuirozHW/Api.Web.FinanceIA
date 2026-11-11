-- ============================================================================
-- Script  : 065_teams_add_approval_quorum.sql
-- Purpose : Add [dbo].[Teams].[ApprovalQuorum] — how many manager signatures a
--           pre-approval from this team needs before it is confirmed. 064 let a
--           team have several managers; this column is where a team says how
--           many of them have to agree.
-- Date    : 2026-09-16
-- Author  : Claude (assisted)
-- Notes   : PHASE 1 of the team-quorum feature. ⚠ THE COLUMN IS STORED AND
--           DISPLAYED BUT ENFORCED BY NOTHING IN THIS RELEASE. DecideAsync is
--           deliberately untouched: a single confirmation still confirms an
--           analysis, exactly as it does today. Phase 2 is what reads this
--           value and counts votes against it (see 067). It ships now so the
--           DBA runs one set of scripts for the feature rather than two, and
--           so the Portal can surface the field before it becomes live.
--
--           Because nothing enforces it yet, the API deliberately exposes it
--           READ-ONLY: it is returned by GET /api/teams and IGNORED by POST and
--           PUT. A control that visibly does nothing is worse than an absent
--           one, so no Admin can set a quorum of 3 and be quietly overruled by
--           a single signature.
--
--           DEFAULT (1) IS THE WHOLE SAFETY ARGUMENT. NOT NULL with a default
--           of 1 backfills every existing row to 1 the moment this runs, and
--           "one signature" is precisely today's behaviour. There is therefore
--           no such thing as a team that behaves differently because of this
--           script — not on the day it runs, and not on the day Phase 2 starts
--           reading the column, since a team nobody has reconfigured still says
--           1. The migration is a no-op by construction rather than by luck.
--
--           INT, not TINYINT or SMALLINT. The storage saving is meaningless at
--           this row count, and INT is what every other count-shaped column in
--           this schema uses ([Documents].[ExtractionAttempts], script 050).
--           Matching the neighbours costs nothing and removes a surprise.
--
--           NO CHECK CONSTRAINT bounding the value, per the convention in 036,
--           046, 052, 059 and 062: value rules live in the API, where changing
--           one is a deploy rather than a migration on every tenant database.
--           The sharper reason here is that Phase 2 has not settled what the
--           bound should be — whether a quorum may exceed the team's manager
--           count, and what happens when a manager is removed below it, are
--           open questions, and freezing an answer into a constraint now means
--           an ALTER on every tenant DB to change our minds. 060 is the worked
--           example of that cost: it exists solely to widen CK_Analyses_Status
--           by one value.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. THIS IS THE DANGEROUS ONE OF THE FOUR.
--           TeamConfiguration maps [ApprovalQuorum] unconditionally, so
--           publishing the code first makes EVERY SELECT against [dbo].[Teams]
--           fail with 'Invalid column name ''ApprovalQuorum'''. That is not
--           just the new field — it is the whole of /api/teams (list, create,
--           update, delete), and it is also the team lookup on the notification
--           path, which is fault-isolated and so fails silently into the
--           RiskManager fallback rather than loudly. Run the DDL first.
--
--           APP SERVICE RESTART REQUIRED after the deploy, so EF's model cache is
--           built against the column that now exists.
--
--           Idempotent: guarded on sys.columns, so the script can be re-run
--           safely and prints what it skipped. Re-running never resets a quorum
--           an Admin has changed — the ALTER simply does not execute a second
--           time.
-- ============================================================================

-- ── Teams.ApprovalQuorum column ─────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Teams]')
      AND  name      = N'ApprovalQuorum'
)
BEGIN
    -- NOT NULL with a default, so every existing row is backfilled to 1 in the
    -- same statement. 1 = one signature = exactly the behaviour before this
    -- column existed, which is what makes adding it a no-op by construction.
    ALTER TABLE [dbo].[Teams]
        ADD [ApprovalQuorum] INT NOT NULL
            CONSTRAINT [DF_Teams_ApprovalQuorum] DEFAULT (1);
    PRINT 'Column [dbo].[Teams].[ApprovalQuorum] added, defaulted to 1.';
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Teams].[ApprovalQuorum] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The column exists, is an int and is NOT NULLABLE → 1 row
--
--    SELECT c.name AS ColumnName, t.name AS DataType, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[Teams]')
--      AND  c.name      = N'ApprovalQuorum';
--    -- expected: ApprovalQuorum  int  0
--
-- 2. The default constraint exists and reads (1) → 1 row
--
--    SELECT dc.name, dc.definition
--    FROM   sys.default_constraints dc
--    JOIN   sys.columns c ON c.object_id = dc.parent_object_id
--                        AND c.column_id = dc.parent_column_id
--    WHERE  dc.parent_object_id = OBJECT_ID(N'[dbo].[Teams]')
--      AND  c.name = N'ApprovalQuorum';
--    -- expected: DF_Teams_ApprovalQuorum  ((1))
--
-- 3. EVERY existing team was backfilled to 1 — this is the check that proves
--    the script changed no behaviour. Anything other than 0 here immediately
--    after the script means a row escaped the default.
--
--    SELECT COUNT(*) AS NotOne FROM [dbo].[Teams] WHERE [ApprovalQuorum] <> 1;
--    -- expected immediately after this script: 0
--
-- 4. The distribution, for reading back later once Phase 2 is live:
--
--    SELECT [ApprovalQuorum], COUNT(*) AS Teams
--    FROM   [dbo].[Teams]
--    GROUP  BY [ApprovalQuorum]
--    ORDER  BY [ApprovalQuorum];
--    -- expected in this release: a single row, "1  <all teams>".
--
-- 5. Once Phase 2 is live, a quorum larger than the team's manager count is the
--    configuration that can never be satisfied. Nothing forbids it in the
--    schema (see the header on why), so this is the query that finds it:
--
--    SELECT t.[Name], t.[ApprovalQuorum], COUNT(m.[UserId]) AS Managers
--    FROM   [dbo].[Teams] t
--    LEFT   JOIN [dbo].[TeamManagers] m ON m.[TeamId] = t.[TeamId]
--    GROUP  BY t.[Name], t.[ApprovalQuorum]
--    HAVING t.[ApprovalQuorum] > COUNT(m.[UserId]);
--    -- expected in this release: 0 rows (every quorum is 1, and 066 gives every
--    -- team that had a manager at least one row). Harmless while nothing reads
--    -- the column; worth watching from Phase 2 on.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: TeamConfiguration maps this column, so
-- dropping it under a running deployment breaks EVERY request against
-- [dbo].[Teams] with "Invalid column name 'ApprovalQuorum'", not just the new
-- field. The default constraint must go before the column.
--
--    ALTER TABLE [dbo].[Teams] DROP CONSTRAINT [DF_Teams_ApprovalQuorum];
--    GO
--    ALTER TABLE [dbo].[Teams] DROP COLUMN [ApprovalQuorum];
--    GO
--
-- What is lost is nothing that any deployed behaviour depends on — in THIS
-- release the column is read by no decision path, so removing it is invisible
-- outside the /api/teams payload. Export first only if Phase 2 has shipped and
-- real quorums have been configured:
--
--    SELECT [TeamId], [TenantId], [Name], [ApprovalQuorum] FROM [dbo].[Teams];
-- ============================================================================
