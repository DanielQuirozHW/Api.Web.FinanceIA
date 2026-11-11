-- ============================================================================
-- Script  : 049_notifications_table.sql
-- Purpose : Create [dbo].[Notifications] — the PER-USER delivery layer that
--           tells one specific user "something happened, go look".
--           This is a NEW concept, deliberately separate from [dbo].[Alerts]:
--           an Alert is per-TENANT (every user of the tenant sees the same row,
--           and a read by one marks it read for everybody), while a Notification
--           is addressed to exactly one [dbo].[Users].[UserId].
--           Alerts are NOT modified by this script and keep working unchanged.
-- Date    : 2026-08-31
-- Author  : Claude (assisted)
-- Notes   : GENERIC ON PURPOSE. In v1 the only emitter is the billing quota
--           alert (BillingAlertService fans one notification out per user of the
--           tenant for each quota alert it emits), but nothing in this table is
--           billing-specific. [Type] is an open code, and [SourceType] +
--           [SourceId] are an untyped pointer at whatever produced the row
--           ('Alert' + the AlertId today). Later emitters — analysis complete,
--           invitations — are new [Type] values, NOT a schema change.
--
--           TENANTID IS NVARCHAR(100), NOT UNIQUEIDENTIFIER. This is the single
--           most important column type here. Script
--           005_fix_business_tenantid_to_nvarchar.sql migrated every business
--           table off UNIQUEIDENTIFIER so they hold Firebase tenant SLUGS
--           (e.g. 'SgrOneClickSandboxFB2'). A GUID column here would reject
--           every insert the application makes. Matches [dbo].[Alerts].[TenantId]
--           exactly, which NotificationConfiguration also mirrors.
--
--           [UserId] IS the difference vs Alerts, and it is NOT NULL: a
--           notification with no addressee is meaningless. There is no
--           "broadcast" row — a tenant-wide event becomes N rows, one per user,
--           written by the emitter. That is what makes per-user read state work.
--
--           FK_Notifications_Users follows 027_notes_table.sql, which likewise
--           constrains [AuthorUserId] -> [dbo].[Users].[UserId]. Note that
--           scripts 030 and 046 deliberately declare NO foreign key on their
--           CreatedByUserId columns; the difference is that those columns record
--           who performed an action (and may outlive the account), whereas this
--           one is a live addressee — a notification pointing at a user row that
--           does not exist can never be read by anyone, so the constraint is
--           worth having. Every row is fanned out from a real [dbo].[Users] row,
--           so the FK costs the emitter nothing.
--
--           NO CASCADE on the FK, matching 027. Deleting a user is not something
--           the application does (accounts are deactivated via [IsActive]), and
--           a cascade would silently destroy history.
--
--           Both indexes are (TenantId, UserId, ...) leading — every query this
--           table serves is scoped to one user of one tenant, because there is no
--           EF global query filter and the repository writes both predicates by
--           hand on every read. The CreatedAt DESC key column matches the list
--           endpoint's ORDER BY exactly, so the list is an index scan in order
--           rather than a sort.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. Unlike 048 (which only relaxed a nullability) this
--           CREATES A TABLE that EF Core maps unconditionally, so publishing the
--           code first makes the three new endpoints fail outright with
--           'Invalid object name ''dbo.Notifications''' on every request. The
--           billing emission path degrades more gently — it is fault-isolated in
--           BillingAlertService and only logs a Warning — but the endpoints do
--           not. Run the DDL first.
--
--           APP SERVICE RESTART REQUIRED after the deploy, so EF's model cache is
--           built against the table that now exists.
--
--           Idempotent: the table, the foreign key and both indexes are each
--           guarded on sys.objects / sys.foreign_keys / sys.indexes, so the
--           script can be re-run safely and prints what it skipped.
-- ============================================================================

-- ── Notifications table ─────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notifications]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[Notifications]
    (
        [NotificationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Notifications_NotificationId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]       NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005 — NOT a GUID
        [UserId]         UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Users].UserId; the addressee (vs Alerts, which have none)
        [Type]           NVARCHAR(50)     NOT NULL,   -- open code, e.g. 'BILLING_ANALYSIS_QUOTA_80'
        [Title]          NVARCHAR(300)    NOT NULL,   -- same width as [dbo].[Alerts].[Title]
        [Body]           NVARCHAR(MAX)    NULL,       -- long copy; nullable so a title-only notification is legal
        [SourceType]     NVARCHAR(50)     NULL,       -- generic pointer, e.g. 'Alert'
        [SourceId]       UNIQUEIDENTIFIER NULL,       -- ...and the id within that source, e.g. the AlertId
        [IsRead]         BIT              NOT NULL
            CONSTRAINT [DF_Notifications_IsRead] DEFAULT (0),
        [ReadAt]         DATETIME2(7)     NULL,       -- set together with IsRead = 1; stays NULL while unread
        [CreatedAt]      DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_Notifications_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_Notifications]
            PRIMARY KEY CLUSTERED ([NotificationId] ASC)
    );
    PRINT 'Table [dbo].[Notifications] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[Notifications] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Notifications_Users]')
)
BEGIN
    ALTER TABLE [dbo].[Notifications]
        ADD CONSTRAINT [FK_Notifications_Users]
            FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_Notifications_Users] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Notifications_Users] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────

-- Serves the list read path: WHERE TenantId = @t AND UserId = @u ORDER BY CreatedAt DESC.
-- CreatedAt is a DESC key column so the ORDER BY is satisfied by the index order itself.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notifications]')
      AND  name      = N'IX_Notifications_TenantId_UserId_CreatedAt'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Notifications_TenantId_UserId_CreatedAt]
        ON [dbo].[Notifications] ([TenantId] ASC, [UserId] ASC, [CreatedAt] DESC);
    PRINT 'Index [IX_Notifications_TenantId_UserId_CreatedAt] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Notifications_TenantId_UserId_CreatedAt] already exists — skipped.';
END
GO

-- Serves the unread-count read path: WHERE TenantId = @t AND UserId = @u AND IsRead = 0.
-- Deliberately NOT filtered on IsRead = 0: the same index also answers the read side, and a
-- filtered index would be rebuilt-into constantly as rows flip to read.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notifications]')
      AND  name      = N'IX_Notifications_TenantId_UserId_IsRead'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Notifications_TenantId_UserId_IsRead]
        ON [dbo].[Notifications] ([TenantId] ASC, [UserId] ASC, [IsRead] ASC);
    PRINT 'Index [IX_Notifications_TenantId_UserId_IsRead] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Notifications_TenantId_UserId_IsRead] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — safe to run, reads nothing sensitive) ──
-- Confirms the table shape, the foreign key and both indexes:
--
--   SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable
--   FROM   sys.columns c
--   JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--   WHERE  c.object_id = OBJECT_ID(N'[dbo].[Notifications]')
--   ORDER  BY c.column_id;
--
--   SELECT name FROM sys.foreign_keys
--   WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Notifications]');
--
--   SELECT i.name, i.type_desc
--   FROM   sys.indexes i
--   WHERE  i.object_id = OBJECT_ID(N'[dbo].[Notifications]') AND i.type > 0;
--
-- Expected: 11 columns with [TenantId] as nvarchar(100) (max_length 200 — bytes,
-- not characters); one foreign key FK_Notifications_Users; three indexes
-- (PK_Notifications clustered, plus the two nonclustered ones above).
