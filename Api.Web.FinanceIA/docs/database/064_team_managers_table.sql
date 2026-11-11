-- ============================================================================
-- Script  : 064_team_managers_table.sql
-- Purpose : Create [dbo].[TeamManagers] — the many-to-many that lets a team
--           have MORE THAN ONE manager. 062 gave an escalation an addressee;
--           it gave it exactly one, which is a single point of failure: the
--           named manager goes on holiday and every pre-approval from that team
--           sits unactioned, because the RiskManager fallback only fires when
--           the manager is ABSENT or DEACTIVATED, never when they are merely
--           unavailable. This table is the first half of the fix.
-- Date    : 2026-09-16
-- Author  : Claude (assisted)
-- Notes   : PHASE 1 of the team-quorum feature. This script and 065 change WHO
--           is notified; they do NOT change how a decision is confirmed. A
--           single confirmation still confirms an analysis — quorum enforcement
--           is Phase 2, and 067 provisions the table it will need.
--
--           ⚠ RUN 062_teams_table.sql AND 063_users_add_teamid.sql FIRST.
--           [FK_TeamManagers_Team] references [dbo].[Teams].[TeamId] and
--           [FK_TeamManagers_User] references [dbo].[Users].[UserId], so this
--           script cannot be applied to a database where either table is
--           missing.
--
--           ⚠ RUN 066_migrate_manager_to_team_managers.sql AFTER this one, on
--           the same visit. 066 copies every existing [Teams].[ManagerUserId]
--           into this table; without it, a tenant that already has teams starts
--           with an EMPTY TeamManagers. That is survivable — the application
--           unions the deprecated column in, see below — but it leaves the two
--           halves disagreeing, which is exactly the state 066 exists to close.
--
--           TENANTID IS NVARCHAR(100), NOT UNIQUEIDENTIFIER. Script
--           005_fix_business_tenantid_to_nvarchar.sql migrated every business
--           table off UNIQUEIDENTIFIER so they hold Firebase tenant SLUGS
--           (e.g. 'SgrOneClickSandboxFB2'). A GUID column here would reject
--           every insert the application makes. Matches [dbo].[Teams].[TenantId]
--           and [dbo].[Users].[TenantId] exactly.
--
--           THE PRIMARY KEY IS COMPOSITE — (TeamId, UserId) — AND THERE IS NO
--           SURROGATE [TeamManagerId]. Four reasons, in order of weight:
--
--             1. The pair IS the identity. A surrogate would be a column
--                nothing ever addresses: no route takes one, no DTO carries
--                one, and no other table points at a single row of this one.
--                Contrast [dbo].[Teams] and [dbo].[Notifications], which carry
--                surrogate GUIDs precisely BECAUSE they are addressed by id.
--             2. It makes the uniqueness rule structural rather than bolted on.
--                A surrogate key would still need UNIQUE (TeamId, UserId) to
--                stop the same person being named twice on one team — i.e. the
--                same index, plus a redundant key column, plus a second object
--                to keep in step.
--             3. It IS the index this table needs. The only read is
--                "every manager of this team": WHERE TeamId = @t AND
--                TenantId = @tenant, and a CLUSTERED (TeamId, UserId) serves
--                that as a covering seek with no key lookup.
--             4. Therefore NO SECONDARY INDEX — the same argument 063 makes for
--                adding none on [dbo].[Users].[TeamId]. An index written on
--                every membership change and read by nothing is a cost, not a
--                win.
--
--           BOTH FOREIGN KEYS ARE DECLARED, following 027, 049, 054, 059 and
--           062. 049 states the test: a foreign key earns its place when the
--           column is a live SUBJECT rather than a record of who performed a
--           past action. Both are — a row pointing at a team or a user that
--           does not exist can never notify anyone.
--
--           [CreatedBy] carries NO foreign key, and that is the other half of
--           the same rule, as 030, 040, 041, 046, 059 and 062 argue: it records
--           who performed an action and may outlive the account. It is NULLABLE
--           because the DBA may seed a row directly — and because 066 does
--           exactly that, writing NULL for every migrated row.
--
--           NO CASCADE on either foreign key, per 027, 049, 059 and 062.
--           Accounts are deactivated via [IsActive], never deleted, and a
--           cascade would silently strip a team of its managers because one of
--           them left. The application clears this table for a team BEFORE
--           deleting the team row (the same contract [FK_Users_Team] already
--           imposes on 063), so the delete never trips the constraint.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. TeamManager is a mapped entity on
--           FinanciaDbContext, so EF Core maps [dbo].[TeamManagers]
--           unconditionally and publishing the code first makes every
--           /api/teams request fail with
--           'Invalid object name ''dbo.TeamManagers'''. The notification
--           emission path degrades more gently — AnalysisDecisionNotifier is
--           fault-isolated and only logs a Warning — but the endpoints do not.
--
--           APP SERVICE RESTART REQUIRED after the deploy, so EF's model cache is
--           built against the table that now exists.
--
--           Creating the table changes NO existing behaviour on its own: until
--           the new code is live nothing reads it, and once live, an empty table
--           for a team simply means the application falls back to that team's
--           deprecated [ManagerUserId] and, failing that, to the RiskManager
--           ladder.
--
--           Idempotent: the table and both foreign keys are each guarded on
--           sys.objects / sys.foreign_keys, so the script can be re-run safely
--           and prints what it skipped.
-- ============================================================================

-- ── TeamManagers table ──────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[TeamManagers]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[TeamManagers]
    (
        [TeamId]    UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Teams].TeamId
        [UserId]    UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Users].UserId
        [TenantId]  NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005 — NOT a GUID
        [CreatedAt] DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_TeamManagers_CreatedAt] DEFAULT (GETUTCDATE()),
        [CreatedBy] UNIQUEIDENTIFIER NULL,       -- [dbo].[Users].UserId; no FK, per script 030 — NULL when seeded by the DBA or by 066

        -- Composite and clustered: the pair IS the identity, it makes the
        -- "one person named once per team" rule structural, and it doubles as
        -- the covering index for the only read this table serves. See the
        -- header for the full argument.
        CONSTRAINT [PK_TeamManagers]
            PRIMARY KEY CLUSTERED ([TeamId] ASC, [UserId] ASC)
    );
    PRINT 'Table [dbo].[TeamManagers] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[TeamManagers] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────
-- Separate batches: the table must be committed by the GO above before a
-- constraint can reference it in the same script run.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_TeamManagers_Team]')
)
BEGIN
    ALTER TABLE [dbo].[TeamManagers]
        ADD CONSTRAINT [FK_TeamManagers_Team]
            FOREIGN KEY ([TeamId]) REFERENCES [dbo].[Teams] ([TeamId]);
    PRINT 'Foreign key [FK_TeamManagers_Team] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_TeamManagers_Team] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_TeamManagers_User]')
)
BEGIN
    ALTER TABLE [dbo].[TeamManagers]
        ADD CONSTRAINT [FK_TeamManagers_User]
            FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_TeamManagers_User] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_TeamManagers_User] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────
-- NONE, deliberately. PK_TeamManagers is CLUSTERED on (TeamId, UserId), which
-- already serves the only read path — "every manager of this team" — as a
-- covering seek. Nothing queries this table the other way round (teams BY
-- manager), and the same reasoning 063 gives for [dbo].[Users].[TeamId]
-- applies: an index written on every membership change and read by nothing is
-- a cost, not a win. Phase 2's "is this user a manager of that team?" check is
-- a point lookup on the full composite key, which is the clustered seek again.

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The table exists with all five columns, correctly typed → 5 rows
--
--    SELECT c.name AS ColumnName, t.name AS DataType,
--           c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[TeamManagers]')
--    ORDER  BY c.column_id;
--    -- expected: TeamId uniqueidentifier NOT NULL / UserId uniqueidentifier NOT NULL
--    --           TenantId nvarchar 200 NOT NULL
--    --           (max_length is BYTES, not characters — 200 = nvarchar(100))
--    --           CreatedAt datetime2 NOT NULL / CreatedBy uniqueidentifier NULL
--
-- 2. Primary key is CLUSTERED and COMPOSITE, TeamId first → 2 rows
--
--    SELECT i.name, i.type_desc, ic.key_ordinal, c.name AS KeyColumn
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[TeamManagers]')
--      AND  i.is_primary_key = 1
--    ORDER  BY ic.key_ordinal;
--    -- expected: PK_TeamManagers CLUSTERED 1 TeamId
--    --           PK_TeamManagers CLUSTERED 2 UserId
--    -- TeamId MUST be key_ordinal 1 — the read path seeks on it.
--
-- 3. Both foreign keys exist, enabled and trusted → 2 rows
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.foreign_keys
--    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[TeamManagers]');
--    -- expected: FK_TeamManagers_Team  0  0
--    --           FK_TeamManagers_User  0  0
--
-- 4. There is exactly ONE index — the clustered PK. A second index here means
--    somebody added one this table does not need.
--
--    SELECT i.name, i.type_desc FROM sys.indexes i
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[TeamManagers]') AND i.type > 0;
--    -- expected: PK_TeamManagers (CLUSTERED), and nothing else.
--
-- 5. The table starts EMPTY, and that is correct — 066 is what fills it.
--
--    SELECT COUNT(*) AS TeamManagers FROM [dbo].[TeamManagers];
--    -- expected immediately after THIS script: 0
--    -- expected after 066: one row per team with a non-null [ManagerUserId]
--
-- 6. Cross-tenant integrity. Every row's tenant must match BOTH its team's and
--    its user's. This should return NOTHING; a row here means a manager was
--    named across a tenant boundary.
--
--    SELECT m.[TeamId], m.[UserId], m.[TenantId] AS RowTenant,
--           t.[TenantId] AS TeamTenant, u.[TenantId] AS UserTenant
--    FROM   [dbo].[TeamManagers] m
--    JOIN   [dbo].[Teams] t ON t.[TeamId] = m.[TeamId]
--    JOIN   [dbo].[Users] u ON u.[UserId] = m.[UserId]
--    WHERE  m.[TenantId] <> t.[TenantId]
--       OR  m.[TenantId] <> u.[TenantId];
--    -- expected: 0 rows.
--
-- 7. Managers per team, readable at a glance — and the check that matters after
--    066: every team with a [ManagerUserId] should have at least one row here.
--
--    SELECT t.[Name] AS Team, COUNT(m.[UserId]) AS Managers, t.[ManagerUserId]
--    FROM   [dbo].[Teams] t
--    LEFT   JOIN [dbo].[TeamManagers] m ON m.[TeamId] = t.[TeamId]
--    GROUP  BY t.[Name], t.[ManagerUserId]
--    ORDER  BY t.[Name];
--    -- after 066: Managers >= 1 wherever ManagerUserId IS NOT NULL.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: TeamManager is a mapped entity, so dropping
-- this table under a running deployment breaks /api/teams with
-- "Invalid object name 'dbo.TeamManagers'".
--
-- Nothing references this table, so it drops on its own — the two foreign keys
-- point OUT of it, not into it, and go with it.
--
--    DROP TABLE [dbo].[TeamManagers];
--    GO
--
-- What is lost is the multi-manager structure ONLY. No team, user, analysis or
-- notification row is touched, and [dbo].[Teams].[ManagerUserId] is deliberately
-- still populated — this PR keeps writing it for exactly this reason — so after
-- a rollback every team falls back to its single named manager, which is the
-- pre-062-era behaviour and a working state, not a broken one. Export first if
-- the extra managers matter:
--
--    SELECT [TeamId], [UserId], [TenantId], [CreatedAt] FROM [dbo].[TeamManagers];
--
-- If 062 is being rolled back too, run THIS script's rollback FIRST —
-- [FK_TeamManagers_Team] blocks DROP TABLE [dbo].[Teams] while it exists.
-- ============================================================================
