-- ============================================================================
-- Script  : 063_users_add_teamid.sql
-- Purpose : Add [dbo].[Users].[TeamId] — which team a user belongs to, and
--           therefore which manager a pre-approval they make should reach.
--           This is the half of the team feature that lives on the user; the
--           team itself is created by 062_teams_table.sql.
-- Date    : 2026-09-10
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN 062_teams_table.sql FIRST. [FK_Users_Team] references
--           [dbo].[Teams].[TeamId], so this script cannot be applied to a
--           database where that table does not yet exist.
--
--           THE COLUMN IS NULLABLE AND NULL IS THE DEFAULT — and it is
--           meaningful, not merely permissive. NULL means "not assigned to a
--           team", which is what every existing user will be the moment this
--           script runs, and it is the state the notification routing is
--           designed around: an analyst with no team has no manager to send a
--           pre-approval to, so the emitter falls back to notifying every active
--           RiskManager of the tenant. That fallback is the safe reading —
--           a pre-approval that reaches nobody is the failure mode worth
--           avoiding — which is why this column is NOT backfilled and NOT made
--           NOT NULL. A tenant that never creates a single team keeps working
--           exactly as it does today.
--
--           FK_Users_Team IS declared, following 027, 049, 054 and 059. 049
--           states the test: a foreign key earns its place when the column is a
--           live SUBJECT rather than a record of who performed a past action. A
--           user pointing at a team row that does not exist would resolve to no
--           manager and silently fall back forever, so the constraint is worth
--           having.
--
--           NO CASCADE, per 027, 049 and 059. Deleting a team must not delete
--           its members. The application unassigns every member (sets this
--           column back to NULL) BEFORE deleting a team, which is why the
--           delete never trips this constraint; without the NO CASCADE a
--           mis-typed DELETE would take the analysts with it.
--
--           NO INDEX on [TeamId]. Nothing queries users BY team: the Admin list
--           reads the whole active roster for the tenant in one go and groups it
--           in memory, and the notification path resolves the OTHER direction
--           (user -> team -> manager) through the clustered PK on [dbo].[Teams].
--           An index here would be written on every user update and read by
--           nothing.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. This is the more dangerous of the two scripts:
--           UserConfiguration maps [TeamId] unconditionally, so publishing the
--           code first makes EVERY SELECT against [dbo].[Users] fail with
--           'Invalid column name ''TeamId'''. That is not just the new
--           endpoints — it is every authenticated request, because resolving the
--           calling user reads that table. Run the DDL first.
--
--           APP SERVICE RESTART REQUIRED after the deploy, so EF's model cache is
--           built against the column that now exists.
--
--           Adding the column changes NO existing behaviour on its own: it is
--           nullable with no default, so every existing row simply gets NULL and
--           no existing query is affected until the new code reads it.
--
--           Idempotent: the column and the foreign key are each guarded on
--           sys.columns / sys.foreign_keys, so the script can be re-run safely
--           and prints what it skipped. Re-running never clears an assignment.
-- ============================================================================

-- ── Users.TeamId column ─────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Users]')
      AND  name      = N'TeamId'
)
BEGIN
    ALTER TABLE [dbo].[Users]
        ADD [TeamId] UNIQUEIDENTIFIER NULL;   -- FK -> [dbo].[Teams].TeamId; NULL = not assigned to a team
    PRINT 'Column [dbo].[Users].[TeamId] added.';
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Users].[TeamId] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────
-- Separate batch: the column must be committed by the GO above before a
-- constraint can reference it in the same script run.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Users_Team]')
)
BEGIN
    ALTER TABLE [dbo].[Users]
        ADD CONSTRAINT [FK_Users_Team]
            FOREIGN KEY ([TeamId]) REFERENCES [dbo].[Teams] ([TeamId]);
    PRINT 'Foreign key [FK_Users_Team] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Users_Team] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The column exists, is a uniqueidentifier and is NULLABLE → 1 row
--
--    SELECT c.name AS ColumnName, t.name AS DataType, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[Users]')
--      AND  c.name      = N'TeamId';
--    -- expected: TeamId  uniqueidentifier  1
--
-- 2. The foreign key exists, is enabled and is trusted → 1 row
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.foreign_keys
--    WHERE  name = N'FK_Users_Team';
--    -- expected: FK_Users_Team  0  0
--
-- 3. Every existing user starts unassigned, and that is correct — they fall back
--    to the RiskManager fan-out until an Admin puts them in a team.
--
--    SELECT COUNT(*) AS Assigned FROM [dbo].[Users] WHERE [TeamId] IS NOT NULL;
--    -- expected immediately after this script: 0
--
-- 4. After assigning users through the API, every user belongs to a team of
--    their OWN tenant. This should return NOTHING; a row here means a user was
--    assigned across a tenant boundary.
--
--    SELECT u.[UserId], u.[Email], u.[TenantId] AS UserTenant, t.[TenantId] AS TeamTenant
--    FROM   [dbo].[Users] u
--    JOIN   [dbo].[Teams] t ON t.[TeamId] = u.[TeamId]
--    WHERE  u.[TenantId] <> t.[TenantId];
--    -- expected: 0 rows.
--
-- 5. Membership, readable at a glance:
--
--    SELECT t.[Name] AS Team, u.[Email], u.[Role],
--           CASE WHEN t.[ManagerUserId] = u.[UserId] THEN 'manager' ELSE '' END AS IsManager
--    FROM   [dbo].[Users] u
--    JOIN   [dbo].[Teams] t ON t.[TeamId] = u.[TeamId]
--    ORDER  BY t.[Name], u.[Email];
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: UserConfiguration maps this column, so
-- dropping it under a running deployment breaks EVERY authenticated request
-- with "Invalid column name 'TeamId'", not just the team endpoints. The
-- constraint must go before the column.
--
--    ALTER TABLE [dbo].[Users] DROP CONSTRAINT [FK_Users_Team];
--    GO
--    ALTER TABLE [dbo].[Users] DROP COLUMN [TeamId];
--    GO
--
-- What is lost is every membership. No user account is otherwise affected and no
-- analysis, notification or team row is touched — but after a rollback every
-- pre-approval notification falls back to the RiskManager fan-out until the
-- assignments are rebuilt. Export first if that matters:
--
--    SELECT [UserId], [Email], [TenantId], [TeamId]
--    FROM   [dbo].[Users] WHERE [TeamId] IS NOT NULL;
--
-- If 062 is being rolled back too, run this script's rollback FIRST — the
-- foreign key blocks DROP TABLE [dbo].[Teams] while it exists.
-- ============================================================================
