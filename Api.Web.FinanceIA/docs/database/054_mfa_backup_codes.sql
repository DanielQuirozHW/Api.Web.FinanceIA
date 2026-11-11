-- ============================================================================
-- Script  : 054_mfa_backup_codes.sql
-- Purpose : Create [dbo].[MfaBackupCodes] — single-use MFA recovery codes for
--           users who enrolled a TOTP factor in Firebase Identity Platform.
--           Firebase owns the TOTP factor but exposes NO backup-code API, so
--           the BFF issues, hashes and validates the codes itself.
-- Date    : 2026-09-03
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS SUPERSEDES docs/database/default-db/001_mfa_backup_codes.sql,
--           which has been DELETED and must never be run. That script created
--           this table in the shared "Default" database (AppDbContext). It was
--           never executed anywhere, so there is no data to migrate — only the
--           design changed: a backup code is FinancIA BUSINESS data belonging to
--           one [dbo].[Users] row, so it belongs in the per-tenant business
--           database, and THIS script is the per-tenant one. Run it on EVERY
--           tenant business database ("FinanciaDefault"), like every other
--           numbered script in this folder.
--
--           Being in the same database as [dbo].[Users] is what makes
--           [FK_MfaBackupCodes_Users] possible — the earlier design keyed the
--           rows by the global Firebase uid precisely because it could not have
--           a foreign key. There is no Firebase uid column here: [dbo].[Users]
--           already carries a unique one (UQ_Users_FirebaseUid), so a uid
--           resolves to a UserId in one indexed lookup, and a second copy could
--           only drift out of agreement with the first.
--
--           [TenantId] is denormalised from the owning [dbo].[Users] row and is
--           written by the application from THAT row, never from the request's
--           X-Tenant-ID header, so a code can never be filed under a tenant its
--           owner does not belong to. NVARCHAR(100) slug per script 005 — NOT a
--           GUID. Both indexes lead with (TenantId, UserId) because every read
--           this table serves is scoped to one user of one tenant.
--
--           [BackupCodeId] is INT IDENTITY rather than the UNIQUEIDENTIFIER used
--           by most tables here: the validation path orders candidates by it, and
--           nothing exposes it outside the API.
--
--           [CodeHash] holds a salted PBKDF2-HMAC-SHA256 hash. The plaintext code
--           is NEVER stored anywhere and is returned exactly once, at generation.
--
--           [UsedAt] is what makes a code single-use. It is also mapped as an EF
--           concurrency token, so the UPDATE that spends a code carries
--           "AND [UsedAt] IS NULL" and two concurrent submissions of the same
--           code cannot both succeed. Spent rows are NEVER deleted — they are the
--           consumption audit trail, which is why the second index is filtered.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database, then restart the App Service so EF's model cache is rebuilt.
--           EF Core maps the table unconditionally through
--           FinanciaDbContext.MfaBackupCodes, so publishing first makes all four
--           /api/mfa/backup-codes endpoints fail with
--           "Invalid object name 'dbo.MfaBackupCodes'". Nothing that works today
--           regresses: no existing table is touched and no existing column changes,
--           so the blast radius is the new feature only.
--
--           Idempotent: the table, the foreign key, both indexes and both column
--           descriptions are each guarded, so the script can be re-run safely.
-- ============================================================================

-- ── MfaBackupCodes table ────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[MfaBackupCodes]
    (
        [BackupCodeId] INT              NOT NULL IDENTITY(1,1),
        [TenantId]     NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005 — NOT a GUID
        [UserId]       UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Users].[UserId]; the code's owner
        [CodeHash]     NVARCHAR(255)    NOT NULL,   -- salted PBKDF2-HMAC-SHA256, never the plaintext
        [CreatedAt]    DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_MfaBackupCodes_CreatedAt]
            DEFAULT (GETUTCDATE()),
        [UsedAt]       DATETIME2(7)     NULL,       -- NULL = still spendable; set once, never cleared
        [UsedIp]       NVARCHAR(45)     NULL,       -- 45 chars holds an IPv6 literal

        CONSTRAINT [PK_MfaBackupCodes]
            PRIMARY KEY CLUSTERED ([BackupCodeId] ASC)
    );
    PRINT 'Table [dbo].[MfaBackupCodes] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[MfaBackupCodes] already exists — skipped.';
END
GO

-- ── Foreign key to Users ────────────────────────────────────────────────────
-- The whole point of moving this table into the business database: a code now
-- cannot outlive, or point at, a user who does not exist.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_MfaBackupCodes_Users]')
)
BEGIN
    ALTER TABLE [dbo].[MfaBackupCodes]
        ADD CONSTRAINT [FK_MfaBackupCodes_Users]
            FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_MfaBackupCodes_Users] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_MfaBackupCodes_Users] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────
-- Lookup by owner. Non-unique: a user holds a whole set of codes at once
-- (10 by default).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]')
      AND  name      = N'IX_MfaBackupCodes_TenantId_UserId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_MfaBackupCodes_TenantId_UserId]
        ON [dbo].[MfaBackupCodes] ([TenantId] ASC, [UserId] ASC);
    PRINT 'Index [IX_MfaBackupCodes_TenantId_UserId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_MfaBackupCodes_TenantId_UserId] already exists — skipped.';
END
GO

-- The hot path: validation and the remaining-count read both look ONLY at codes
-- that have not been spent. Spent rows are kept forever as the consumption audit
-- trail, so the filter is what keeps this index small as the table grows.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]')
      AND  name      = N'IX_MfaBackupCodes_TenantId_UserId_Unused'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_MfaBackupCodes_TenantId_UserId_Unused]
        ON [dbo].[MfaBackupCodes] ([TenantId] ASC, [UserId] ASC)
        WHERE [UsedAt] IS NULL;
    PRINT 'Index [IX_MfaBackupCodes_TenantId_UserId_Unused] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_MfaBackupCodes_TenantId_UserId_Unused] already exists — skipped.';
END
GO

-- ── Column descriptions ─────────────────────────────────────────────────────
-- Guarded individually so a partial run re-runs cleanly.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.extended_properties
    WHERE  major_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]')
      AND  name     = N'MS_Description'
      AND  minor_id = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[MfaBackupCodes]'), 'CodeHash', 'ColumnId')
)
BEGIN
    EXEC sys.sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'Salted PBKDF2-HMAC-SHA256 hash of the backup code,
                   formatted "pbkdf2-sha256${iterations}${salt}${hash}".
                   NEVER the plaintext code — the plaintext is returned
                   once at generation and is not recoverable.',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'MfaBackupCodes',
        @level2type = N'COLUMN', @level2name = N'CodeHash';
    PRINT 'Description added for MfaBackupCodes.CodeHash.';
END
ELSE
BEGIN
    PRINT 'Description for MfaBackupCodes.CodeHash already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.extended_properties
    WHERE  major_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]')
      AND  name     = N'MS_Description'
      AND  minor_id = COLUMNPROPERTY(OBJECT_ID(N'[dbo].[MfaBackupCodes]'), 'UsedAt', 'ColumnId')
)
BEGIN
    EXEC sys.sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'When the code was spent (UTC), or NULL while it is
                   still available. This is what makes a code single-use:
                   every lookup filters on UsedAt IS NULL, and the
                   application treats it as an EF concurrency token so
                   two concurrent submissions cannot spend it twice.',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'MfaBackupCodes',
        @level2type = N'COLUMN', @level2name = N'UsedAt';
    PRINT 'Description added for MfaBackupCodes.UsedAt.';
END
ELSE
BEGIN
    PRINT 'Description for MfaBackupCodes.UsedAt already exists — skipped.';
END
GO

-- ── Verification (run manually after executing this script) ─────────────────
--   SELECT name, system_type_id, max_length, is_nullable
--   FROM   sys.columns
--   WHERE  object_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]');
--   -- Expected: 7 columns, [TenantId] nvarchar(100) (max_length 200 — bytes).
--
--   SELECT name FROM sys.foreign_keys
--   WHERE  parent_object_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]');
--   -- Expected: FK_MfaBackupCodes_Users
--
--   SELECT i.name, i.type_desc, i.has_filter
--   FROM   sys.indexes i
--   WHERE  i.object_id = OBJECT_ID(N'[dbo].[MfaBackupCodes]') AND i.type > 0;
--   -- Expected: PK_MfaBackupCodes (clustered) + the two nonclustered indexes,
--   --           the _Unused one with has_filter = 1.
