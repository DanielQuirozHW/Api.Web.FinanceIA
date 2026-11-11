-- ============================================================================
-- Script  : 059_user_approval_limits.sql
-- Purpose : Create [dbo].[UserApprovalLimits] — how much one user may commit
--           directly, per currency, before an analysis needs a second signature.
--           This is the table that makes the two-step decision flow a delegation
--           ladder rather than a blanket rule.
-- Date    : 2026-09-04
-- Author  : Claude (assisted)
-- Notes   : One row per (UserId, Currency). The row answers two questions:
--
--             CanApprove — may this user approve directly in this currency?
--             MaxAmount  — up to how much? NULL = no ceiling.
--
--           THE ABSENCE OF A ROW IS MEANINGFUL, and it is the default. A user
--           with no row for a currency CANNOT approve in it directly; their
--           decision becomes a pre-approval awaiting a RiskManager. This is
--           deliberate and is why the table is NOT seeded: a tenant that never
--           configures it routes every analyst decision through a second
--           signature, which is the safe reading of "not configured". Reading a
--           missing row as "unlimited" would be the same table with the opposite
--           and much worse default.
--
--           A row with CanApprove = 0 is therefore not redundant with no row at
--           all: both stop a direct approval, but the row is a deliberate,
--           auditable statement, and it retains the ceiling an administrator had
--           configured if they later re-enable it.
--
--           ONLY ANALYSTS ARE MEASURED AGAINST THIS TABLE. RiskManager and Admin
--           have no ceiling and the code never reads it for them, so they have no
--           rows here — an empty result for a RiskManager is not a restriction.
--
--           PK IS (UserId, Currency), and TenantId is deliberately NOT part of
--           it. UserId is already globally unique ([dbo].[Users] is keyed by it
--           alone), so adding TenantId to the key would allow the same user to
--           hold two different ceilings in one currency with nothing to choose
--           between them. TenantId is still carried and still filtered on in
--           every query, because FinanciaDbContext has no global query filter —
--           the same reason every other business table carries it.
--
--           UserId leads the key rather than TenantId, unlike the tenant-first
--           indexes elsewhere: the hot read is a point lookup for one user in one
--           currency on the decision path, and the leading column is the one the
--           predicate always has. IX_UserApprovalLimits_TenantId_UserId covers
--           the admin screen's "everything for this user in this tenant" read.
--
--           NO CHECK CONSTRAINT on [Currency]. The accepted codes live in
--           Clases/Currencies.cs and are validated in the API, following 036,
--           046 and 052: a value list in the schema turns adding a currency into
--           a migration on every tenant database. Likewise no CHECK forbidding a
--           negative MaxAmount — the API rejects it, and a DECIMAL cannot express
--           the rule about NULL that actually matters.
--
--           FK_UserApprovalLimits_Users IS declared, following 027, 049 and 054.
--           049 states the test: a foreign key earns its place when the column is
--           a live SUBJECT rather than a record of who performed a past action. A
--           limit belonging to a user row that does not exist can never be read by
--           anything, so it is exactly that case. NO CASCADE, per 027 and 049 —
--           accounts are deactivated via [IsActive], never deleted.
--
--           CreatedBy / UpdatedBy carry NO foreign key, and that is the other half
--           of the same rule: those record who performed an action and may outlive
--           the account, exactly as 030, 040, 041 and 046 argue. Both are NULLABLE
--           because the DBA may seed a row directly, with no application user
--           behind it.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on EVERY
--           tenant database. UserApprovalLimit is a mapped entity on
--           FinanciaDbContext, and the decision endpoint queries it on every
--           analyst approval. Against a database without the table, every
--           approval fails with "Invalid object name". Restart the App Service
--           after deploying so EF's model cache is rebuilt.
--
--           Creating the table changes NO existing behaviour on its own: until
--           the new code is live nothing reads it, and once live, an empty table
--           simply means every analyst decision is a pre-approval. Populate it
--           through the Admin screen (PUT /api/bff/settings/users/{id}/approval-limits)
--           or seed it here — see the commented example at the end.
--
--           Idempotent: the table, the foreign key and the index are each
--           guarded, so the script can be re-run safely and prints what it
--           skipped. Re-running never overwrites a configured limit.
-- ============================================================================

-- ── UserApprovalLimits table ────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[UserApprovalLimits]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[UserApprovalLimits]
    (
        [TenantId]   NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005 — NOT a GUID
        [UserId]     UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Users].UserId; the subject of the limit
        [Currency]   NVARCHAR(3)      NOT NULL,   -- ISO 4217 code (e.g. 'ARS'); validated in the API, no CHECK
        [CanApprove] BIT              NOT NULL    -- 0 = may only pre-approve in this currency
            CONSTRAINT [DF_UserApprovalLimits_CanApprove] DEFAULT (0),
        [MaxAmount]  DECIMAL(18, 2)   NULL,       -- inclusive ceiling; NULL with CanApprove = 1 means NO ceiling
        [CreatedAt]  DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_UserApprovalLimits_CreatedAt] DEFAULT (GETUTCDATE()),
        [CreatedBy]  UNIQUEIDENTIFIER NULL,       -- [dbo].[Users].UserId; no FK, per script 030 — NULL when seeded by the DBA
        [UpdatedAt]  DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_UserApprovalLimits_UpdatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedBy]  UNIQUEIDENTIFIER NULL,       -- as CreatedBy

        CONSTRAINT [PK_UserApprovalLimits]
            PRIMARY KEY CLUSTERED ([UserId] ASC, [Currency] ASC)
    );
    PRINT 'Table [dbo].[UserApprovalLimits] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[UserApprovalLimits] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_UserApprovalLimits_Users]')
)
BEGIN
    ALTER TABLE [dbo].[UserApprovalLimits]
        ADD CONSTRAINT [FK_UserApprovalLimits_Users]
            FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_UserApprovalLimits_Users] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_UserApprovalLimits_Users] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────

-- Serves the admin read path: WHERE TenantId = @t AND UserId = @u ORDER BY Currency.
-- The decision path's point lookup (UserId + Currency) is already served by the
-- clustered primary key and needs nothing further.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[UserApprovalLimits]')
      AND  name      = N'IX_UserApprovalLimits_TenantId_UserId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_UserApprovalLimits_TenantId_UserId]
        ON [dbo].[UserApprovalLimits] ([TenantId] ASC, [UserId] ASC);
    PRINT 'Index [IX_UserApprovalLimits_TenantId_UserId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_UserApprovalLimits_TenantId_UserId] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The table exists with all nine columns, correctly typed → 9 rows
--
--    SELECT c.name AS ColumnName, t.name AS DataType,
--           c.precision, c.scale, c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[UserApprovalLimits]')
--    ORDER  BY c.column_id;
--    -- expected: TenantId nvarchar 200 / UserId uniqueidentifier / Currency nvarchar 6
--    --           CanApprove bit NOT NULL / MaxAmount decimal 18,2 NULL
--    --           CreatedAt datetime2 NOT NULL / CreatedBy uniqueidentifier NULL
--    --           UpdatedAt datetime2 NOT NULL / UpdatedBy uniqueidentifier NULL
--
-- 2. Primary key is the composite, clustered, in this order → 2 rows
--
--    SELECT i.name, i.type_desc, c.name AS KeyColumn, ic.key_ordinal
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[UserApprovalLimits]')
--      AND  i.is_primary_key = 1
--    ORDER  BY ic.key_ordinal;
--    -- expected: PK_UserApprovalLimits CLUSTERED, UserId (1) then Currency (2)
--
-- 3. Foreign key and index exist → 1 row each
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.foreign_keys
--    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[UserApprovalLimits]');
--    -- expected: FK_UserApprovalLimits_Users  0  0
--
--    SELECT name FROM sys.indexes
--    WHERE  object_id = OBJECT_ID(N'[dbo].[UserApprovalLimits]') AND name IS NOT NULL;
--    -- expected: PK_UserApprovalLimits, IX_UserApprovalLimits_TenantId_UserId
--
-- 4. The table starts EMPTY, and that is correct — every analyst pre-approves
--    until an Admin configures them.
--
--    SELECT COUNT(*) AS ConfiguredLimits FROM [dbo].[UserApprovalLimits];
--    -- expected immediately after this script: 0
--
-- 5. After configuring one analyst through the API, the row round-trips and the
--    composite key holds (a second row for the same user+currency must fail):
--
--    SELECT l.[UserId], u.[Email], l.[Currency], l.[CanApprove], l.[MaxAmount], l.[UpdatedBy]
--    FROM   [dbo].[UserApprovalLimits] l
--    JOIN   [dbo].[Users] u ON u.[UserId] = l.[UserId]
--    ORDER  BY u.[Email], l.[Currency];
--
-- 6. Every row belongs to a user of the same tenant. This should return NOTHING;
--    a row here means a limit was written across a tenant boundary.
--
--    SELECT l.[UserId], l.[TenantId] AS LimitTenant, u.[TenantId] AS UserTenant
--    FROM   [dbo].[UserApprovalLimits] l
--    JOIN   [dbo].[Users] u ON u.[UserId] = l.[UserId]
--    WHERE  l.[TenantId] <> u.[TenantId];
--    -- expected: 0 rows.
-- ============================================================================

-- ── Optional seed (DBA reference only — NOT executed) ───────────────────────
-- Not run by this script, on purpose: who may commit how much is a business
-- decision per tenant, and a guessed ceiling is worse than none. Prefer the
-- Admin screen. If seeding by hand, this is the shape — note that CanApprove
-- must be set explicitly, since the column defaults to 0.
--
--    INSERT INTO [dbo].[UserApprovalLimits]
--        ([TenantId], [UserId], [Currency], [CanApprove], [MaxAmount])
--    SELECT u.[TenantId], u.[UserId], N'ARS', 1, 5000000.00
--    FROM   [dbo].[Users] u
--    WHERE  u.[Email] = N'analista@ejemplo.com'
--      AND  NOT EXISTS (SELECT 1 FROM [dbo].[UserApprovalLimits] l
--                       WHERE l.[UserId] = u.[UserId] AND l.[Currency] = N'ARS');
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: the deployed code queries this table on every
-- analyst approval, so dropping it under a running deployment breaks the decision
-- endpoint with "Invalid object name". Dropping the table removes its foreign
-- key, index and default constraints with it.
--
--    DROP TABLE [dbo].[UserApprovalLimits];
--    GO
--
-- What is lost is every configured ceiling. No analysis is affected — the
-- decisions themselves live on [dbo].[Analyses] and are untouched — but after a
-- rollback and re-apply, every analyst is back to the default of "may only
-- pre-approve" until an Admin reconfigures them. Export first if that matters:
--
--    SELECT [TenantId], [UserId], [Currency], [CanApprove], [MaxAmount]
--    FROM   [dbo].[UserApprovalLimits];
-- ============================================================================
