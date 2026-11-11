-- ============================================================================
-- Script  : 066_migrate_manager_to_team_managers.sql
-- Purpose : Copy every existing [dbo].[Teams].[ManagerUserId] into
--           [dbo].[TeamManagers], so a team that already had a manager still
--           has one after the application starts reading the new table.
-- Date    : 2026-09-16
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS IS A DATA SCRIPT (DML), NOT A SCHEMA SCRIPT. It is the first
--           of its kind in this folder: every other numbered script creates or
--           alters an object, and this one moves rows between two that already
--           exist. It is numbered into the same sequence anyway, because the
--           DBA has to run it at a specific point in the same visit and a file
--           outside the sequence is a file that gets missed.
--
--           ⚠ RUN 064_team_managers_table.sql FIRST. This script INSERTs into
--           [dbo].[TeamManagers] and fails outright if that table does not
--           exist. Order within the visit: 064, then 065, then THIS, then 067.
--
--           WHAT HAPPENS IF IT IS SKIPPED — read this before deciding it is
--           optional. The application does NOT break: AnalysisDecisionNotifier
--           reads the UNION of [dbo].[TeamManagers] and the deprecated
--           [Teams].[ManagerUserId], precisely so that a tenant whose data
--           migration was missed still routes a pre-approval to its named
--           manager instead of silently degrading to the RiskManager fan-out.
--           That union is a safety net, not a licence to skip this: it is also
--           the thing a later cleanup script wants to remove, and it cannot be
--           removed until every tenant has run this. Skipping it defers the
--           work, it does not avoid it.
--
--           It is also why this script is not, and must not be, folded into the
--           application as a startup migration. Nothing in this codebase writes
--           schema or bulk-migrates data at boot — Database First means the DBA
--           runs the scripts, and the code merely reads what is there.
--
--           IDEMPOTENT BY NOT EXISTS, NOT BY AN IF WRAPPER. The guard sits
--           inside the INSERT's WHERE clause, per row, rather than around the
--           whole statement. A statement-level "IF the table is empty" guard
--           would be wrong twice over: it would refuse to run on a tenant that
--           had already created one team through the new UI, and it would
--           silently do nothing on a re-run that ought to pick up a team added
--           between the two runs. Per-row, the script converges: run it once,
--           run it five times, run it again next month after new teams appear —
--           the result is the same and nothing is ever duplicated. (The
--           composite PK_TeamManagers would reject a duplicate anyway; the
--           NOT EXISTS is what stops the script erroring instead of no-opping.)
--
--           [CreatedBy] IS WRITTEN AS NULL, deliberately. The column records
--           which application user named a manager, and nobody did — the DBA
--           did, via this script. 062 made it nullable for exactly this case.
--           Inventing a user id here would put a fiction in an audit trail.
--
--           [TenantId] IS COPIED FROM THE TEAM, not looked up from the user.
--           They agree today and 064's verification query 6 checks that they
--           keep agreeing, but the team is the right source: this row records
--           "this team has this manager", so it belongs to the team's tenant.
--
--           TEAMS WITH A NULL [ManagerUserId] ARE SKIPPED, which is correct and
--           not a gap. NULL means "no manager named yet" — a real state, per
--           062 — and the application already reads it as "fall back to the
--           RiskManager ladder". There is nothing to migrate.
--
--           ⚠ [Teams].[ManagerUserId] IS NOT DROPPED BY THIS SCRIPT, AND THAT
--           IS INTENTIONAL. The column is RETAINED BUT DEPRECATED:
--             - the application still WRITES it, because the existing Admin UI
--               sets a single manager through PUT /api/teams/{id} and that
--               endpoint is not being replaced in this release;
--             - the application still READS it, unioned as described above;
--             - and it is the rollback path — with the column intact, dropping
--               [dbo].[TeamManagers] returns the system to its pre-064
--               behaviour rather than to a team with no manager at all.
--           A later cleanup script drops it, once Phase 2's management API has
--           replaced the single-manager setter and the union read is gone.
--           Do not "tidy" it away here.
--
--           NO APP SERVICE RESTART is needed for this script on its own — it
--           changes no schema, so EF's model cache is unaffected. Run it in the
--           same window as 064/065, which do need one.
-- ============================================================================

-- ── Backfill TeamManagers from the deprecated Teams.ManagerUserId ────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[TeamManagers]')
      AND  type      = N'U'
)
BEGIN
    -- Fail loudly rather than half-migrating. RAISERROR with severity 16 so the
    -- DBA sees it as an error in SSMS and the run stops here.
    RAISERROR (N'[dbo].[TeamManagers] does not exist. Run 064_team_managers_table.sql before this script.', 16, 1);
END
ELSE
BEGIN
    DECLARE @Inserted INT;

    INSERT INTO [dbo].[TeamManagers] ([TeamId], [UserId], [TenantId], [CreatedAt], [CreatedBy])
    SELECT t.[TeamId],
           t.[ManagerUserId],
           t.[TenantId],      -- the team's tenant, not the user's — see header
           GETUTCDATE(),
           NULL               -- no application user named this manager; the DBA did
    FROM   [dbo].[Teams] t
    WHERE  t.[ManagerUserId] IS NOT NULL
      AND  NOT EXISTS (
               SELECT 1
               FROM   [dbo].[TeamManagers] m
               WHERE  m.[TeamId] = t.[TeamId]
                 AND  m.[UserId] = t.[ManagerUserId]
           );

    SET @Inserted = @@ROWCOUNT;

    PRINT 'Migrated ' + CAST(@Inserted AS NVARCHAR(20))
        + ' manager(s) from [dbo].[Teams].[ManagerUserId] into [dbo].[TeamManagers].';
    PRINT 'A count of 0 is correct on a re-run, or on a tenant whose teams have no manager named.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. THE CHECK THAT MATTERS: every team with a named manager now has the
--    matching row. This must return NOTHING. A row here is a manager the
--    migration missed, and that team's pre-approvals depend on the deprecated
--    column's union read until it is fixed.
--
--    SELECT t.[TeamId], t.[Name], t.[TenantId], t.[ManagerUserId]
--    FROM   [dbo].[Teams] t
--    WHERE  t.[ManagerUserId] IS NOT NULL
--      AND  NOT EXISTS (SELECT 1 FROM [dbo].[TeamManagers] m
--                       WHERE m.[TeamId] = t.[TeamId] AND m.[UserId] = t.[ManagerUserId]);
--    -- expected: 0 rows.
--
-- 2. Counts line up. The migrated total should equal the number of teams with a
--    non-null manager (it may be LARGER if extra managers have since been added
--    through the application — that is fine and expected from Phase 2 on).
--
--    SELECT (SELECT COUNT(*) FROM [dbo].[Teams] WHERE [ManagerUserId] IS NOT NULL) AS TeamsWithManager,
--           (SELECT COUNT(*) FROM [dbo].[TeamManagers])                            AS TeamManagerRows;
--    -- expected immediately after this script: the two are EQUAL.
--
-- 3. Nothing was duplicated. The composite primary key makes this structurally
--    impossible, so a non-zero result means the PK is not what 064 declared.
--
--    SELECT [TeamId], [UserId], COUNT(*) AS Copies
--    FROM   [dbo].[TeamManagers]
--    GROUP  BY [TeamId], [UserId]
--    HAVING COUNT(*) > 1;
--    -- expected: 0 rows, always.
--
-- 4. Cross-tenant integrity survived the copy — the same query 064 carries,
--    worth re-running here because this script is what puts the first rows in.
--
--    SELECT m.[TeamId], m.[UserId], m.[TenantId] AS RowTenant,
--           t.[TenantId] AS TeamTenant, u.[TenantId] AS UserTenant
--    FROM   [dbo].[TeamManagers] m
--    JOIN   [dbo].[Teams] t ON t.[TeamId] = m.[TeamId]
--    JOIN   [dbo].[Users] u ON u.[UserId] = m.[UserId]
--    WHERE  m.[TenantId] <> t.[TenantId] OR m.[TenantId] <> u.[TenantId];
--    -- expected: 0 rows.
--
-- 5. Readable before/after, for the run sheet:
--
--    SELECT t.[Name] AS Team, u.[Email] AS Manager, m.[CreatedAt],
--           CASE WHEN m.[CreatedBy] IS NULL THEN 'migrated by 066' ELSE 'named in the app' END AS Origin
--    FROM   [dbo].[TeamManagers] m
--    JOIN   [dbo].[Teams] t ON t.[TeamId] = m.[TeamId]
--    JOIN   [dbo].[Users] u ON u.[UserId] = m.[UserId]
--    ORDER  BY t.[Name], u.[Email];
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- There is nothing to roll back that is not already covered by 064's rollback:
-- this script only inserts rows into [dbo].[TeamManagers], and dropping that
-- table removes them.
--
-- To undo ONLY this script's effect while keeping the table — deleting the
-- migrated rows but preserving any manager added through the application —
-- delete exactly the rows it wrote, which are the ones with a NULL [CreatedBy]
-- that still match the deprecated column:
--
--    DELETE m
--    FROM   [dbo].[TeamManagers] m
--    JOIN   [dbo].[Teams] t ON t.[TeamId] = m.[TeamId]
--    WHERE  m.[CreatedBy] IS NULL
--      AND  m.[UserId] = t.[ManagerUserId];
--    GO
--
-- Note the [CreatedBy] IS NULL predicate: without it this would also delete a
-- manager an Admin named through the application who happens to be the same
-- person. Export first if the distinction matters:
--
--    SELECT [TeamId], [UserId], [TenantId], [CreatedAt], [CreatedBy] FROM [dbo].[TeamManagers];
--
-- Nothing is lost that cannot be regenerated: [Teams].[ManagerUserId] is
-- deliberately still populated (see the header), so re-running this script
-- rebuilds exactly what the delete removed.
-- ============================================================================
