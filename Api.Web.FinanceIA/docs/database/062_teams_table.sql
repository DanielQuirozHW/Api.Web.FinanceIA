-- ============================================================================
-- Script  : 062_teams_table.sql
-- Purpose : Create [dbo].[Teams] — the analyst grouping that gives a
--           pre-approval somebody to go to. A team is a name plus a MANAGER,
--           and the manager is the whole point: when an analyst pre-approves an
--           analysis, the notification has to reach one specific person rather
--           than every RiskManager in the tenant.
-- Date    : 2026-09-10
-- Author  : Claude (assisted)
-- Notes   : Pairs with 063_users_add_teamid.sql, which adds the [TeamId] column
--           on [dbo].[Users] that points back here. RUN THIS ONE FIRST — 063
--           declares a foreign key against [dbo].[Teams].[TeamId] and cannot be
--           applied before this table exists.
--
--           TENANTID IS NVARCHAR(100), NOT UNIQUEIDENTIFIER. Script
--           005_fix_business_tenantid_to_nvarchar.sql migrated every business
--           table off UNIQUEIDENTIFIER so they hold Firebase tenant SLUGS
--           (e.g. 'SgrOneClickSandboxFB2'). A GUID column here would reject
--           every insert the application makes. Matches [dbo].[Users].[TenantId]
--           and [dbo].[Notifications].[TenantId] exactly.
--
--           [ManagerUserId] IS NULLABLE, and the null is meaningful: a team
--           whose manager has not been named yet. The application reads it as
--           "no manager to notify" and falls back to notifying every active
--           RiskManager of the tenant, which is the safe reading — a
--           pre-approval that reaches nobody is the failure mode worth avoiding.
--           A team is therefore never invalid for want of a manager, and the
--           Admin screen can create one before deciding who runs it.
--
--           FK_Teams_ManagerUser IS declared, following 027, 049, 054 and 059.
--           049 states the test: a foreign key earns its place when the column
--           is a live SUBJECT rather than a record of who performed a past
--           action. The manager is exactly that — a team pointing at a user row
--           that does not exist can never notify anyone — so the constraint is
--           worth having.
--
--           CreatedBy / UpdatedBy carry NO foreign key, and that is the other
--           half of the same rule: those record who performed an action and may
--           outlive the account, exactly as 030, 040, 041, 046 and 059 argue.
--           Both are NULLABLE because the DBA may seed a team directly, with no
--           application user behind it.
--
--           NO CASCADE on the foreign key, per 027, 049 and 059. Deleting a user
--           is not something the application does (accounts are deactivated via
--           [IsActive]), and a cascade would silently destroy a team because its
--           manager left.
--
--           NO CHECK CONSTRAINT on [Name] beyond NOT NULL. The API trims it and
--           rejects blanks, following the convention in 036, 046, 052 and 059
--           that value rules live in the code rather than turning every change
--           into a migration on every tenant database.
--
--           The index leads with [TenantId] — the only read this table serves is
--           "the teams of this tenant" — with [Name] as the second key column so
--           the Admin list comes back in display order from the index itself
--           rather than through a sort.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. Team is a mapped entity on FinanciaDbContext, so
--           EF Core maps [dbo].[Teams] unconditionally and publishing the code
--           first makes every /api/teams request fail outright with
--           'Invalid object name ''dbo.Teams'''. The notification emission path
--           degrades more gently — AnalysisDecisionNotifier is fault-isolated
--           and only logs a Warning — but the endpoints do not.
--
--           APP SERVICE RESTART REQUIRED after the deploy, so EF's model cache is
--           built against the table that now exists.
--
--           Creating the table changes NO existing behaviour on its own: until
--           the new code is live nothing reads it, and once live, an empty table
--           simply means every analyst falls back to the RiskManager fan-out.
--
--           Idempotent: the table, the foreign key and the index are each guarded
--           on sys.objects / sys.foreign_keys / sys.indexes, so the script can be
--           re-run safely and prints what it skipped.
-- ============================================================================

-- ── Teams table ─────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[Teams]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[Teams]
    (
        [TeamId]        UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Teams_TeamId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]      NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005 — NOT a GUID
        [Name]          NVARCHAR(200)    NOT NULL,   -- display name; trimmed and non-blank-checked in the API
        [ManagerUserId] UNIQUEIDENTIFIER NULL,       -- FK -> [dbo].[Users].UserId; NULL = no manager named yet
        [CreatedAt]     DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_Teams_CreatedAt] DEFAULT (GETUTCDATE()),
        [CreatedBy]     UNIQUEIDENTIFIER NULL,       -- [dbo].[Users].UserId; no FK, per script 030 — NULL when seeded by the DBA
        [UpdatedAt]     DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_Teams_UpdatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedBy]     UNIQUEIDENTIFIER NULL,       -- as CreatedBy

        CONSTRAINT [PK_Teams]
            PRIMARY KEY CLUSTERED ([TeamId] ASC)
    );
    PRINT 'Table [dbo].[Teams] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[Teams] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Teams_ManagerUser]')
)
BEGIN
    ALTER TABLE [dbo].[Teams]
        ADD CONSTRAINT [FK_Teams_ManagerUser]
            FOREIGN KEY ([ManagerUserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_Teams_ManagerUser] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Teams_ManagerUser] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────

-- Serves the only read path this table has: WHERE TenantId = @t ORDER BY Name.
-- [Name] is the second key column so the ordering comes from the index rather
-- than a sort.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Teams]')
      AND  name      = N'IX_Teams_TenantId_Name'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Teams_TenantId_Name]
        ON [dbo].[Teams] ([TenantId] ASC, [Name] ASC);
    PRINT 'Index [IX_Teams_TenantId_Name] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Teams_TenantId_Name] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The table exists with all eight columns, correctly typed → 8 rows
--
--    SELECT c.name AS ColumnName, t.name AS DataType,
--           c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[Teams]')
--    ORDER  BY c.column_id;
--    -- expected: TeamId uniqueidentifier NOT NULL / TenantId nvarchar 200 NOT NULL
--    --           (max_length is BYTES, not characters — 200 = nvarchar(100))
--    --           Name nvarchar 400 NOT NULL / ManagerUserId uniqueidentifier NULL
--    --           CreatedAt datetime2 NOT NULL / CreatedBy uniqueidentifier NULL
--    --           UpdatedAt datetime2 NOT NULL / UpdatedBy uniqueidentifier NULL
--
-- 2. Primary key is clustered on [TeamId] → 1 row
--
--    SELECT i.name, i.type_desc, c.name AS KeyColumn
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[Teams]')
--      AND  i.is_primary_key = 1;
--    -- expected: PK_Teams CLUSTERED TeamId
--
-- 3. Foreign key and index exist → 1 row each
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.foreign_keys
--    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Teams]');
--    -- expected: FK_Teams_ManagerUser  0  0
--
--    SELECT i.name, i.type_desc
--    FROM   sys.indexes i
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[Teams]') AND i.type > 0;
--    -- expected: PK_Teams (CLUSTERED) and IX_Teams_TenantId_Name (NONCLUSTERED)
--
-- 4. The table starts EMPTY, and that is correct — a tenant with no teams simply
--    falls back to notifying every active RiskManager.
--
--    SELECT COUNT(*) AS Teams FROM [dbo].[Teams];
--    -- expected immediately after this script: 0
--
-- 5. After creating a team through the API, every manager belongs to the same
--    tenant as their team. This should return NOTHING; a row here means a team
--    was pointed at a manager across a tenant boundary.
--
--    SELECT t.[TeamId], t.[Name], t.[TenantId] AS TeamTenant, u.[TenantId] AS ManagerTenant
--    FROM   [dbo].[Teams] t
--    JOIN   [dbo].[Users] u ON u.[UserId] = t.[ManagerUserId]
--    WHERE  t.[TenantId] <> u.[TenantId];
--    -- expected: 0 rows.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first, and drop 063 BEFORE this script: the
-- [FK_Users_Team] constraint added by 063 references this table and will block
-- the DROP while it exists. The deployed code also maps this table, so dropping
-- it under a running deployment breaks /api/teams with "Invalid object name".
--
--    ALTER TABLE [dbo].[Users] DROP CONSTRAINT [FK_Users_Team];
--    ALTER TABLE [dbo].[Users] DROP COLUMN [TeamId];
--    GO
--    DROP TABLE [dbo].[Teams];
--    GO
--
-- What is lost is the team structure and every membership. No analysis, user or
-- notification is affected — they live on their own tables and are untouched —
-- but after a rollback every pre-approval notification falls back to the
-- RiskManager fan-out until the teams are rebuilt. Export first if that matters:
--
--    SELECT [TeamId], [TenantId], [Name], [ManagerUserId] FROM [dbo].[Teams];
--    SELECT [UserId], [TenantId], [TeamId] FROM [dbo].[Users] WHERE [TeamId] IS NOT NULL;
-- ============================================================================
