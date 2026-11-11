-- ============================================================================
-- Script  : 032_invitations_lifecycle_and_entity_createdby.sql
-- Purpose : Add the invitation-revocation columns to [dbo].[UserInvitations]
--           and a [CreatedBy] author column to [dbo].[Entities], then backfill
--           [Entities].[CreatedBy] from the audit trail where possible.
-- Date    : 2026-07-24
-- Author  : Claude (assisted)
-- Notes   : [dbo].[UserInvitations] gains RevokedAt (DATETIME2 NULL) and
--           RevokedByUserId (UNIQUEIDENTIFIER NULL). These are DISTINCT from
--           UsedAt: UsedAt means the invitation was accepted (the user
--           registered); RevokedAt means an admin cancelled a still-pending
--           invitation. Invitation status is DERIVED, never stored — precedence
--           accepted -> revoked -> expired -> pending. Both columns are nullable
--           and additive; existing rows keep NULL and read as before.
--
--           [dbo].[Entities] gains CreatedBy (UNIQUEIDENTIFIER NULL) — the id of
--           the user who created the PyME. NULL means "author unknown" (the UI
--           renders "—"); no fabricated fallback.
--
--           BACKFILL: [Entities].[CreatedBy] is backfilled from the earliest
--           'Entity.Created' audit row per entity in [dbo].[AuditLogs]
--           (AuditLogs.EntityId stores the GUID as a string; joined via CAST).
--           This is best-effort by nature: the audit write is fire-and-forget,
--           and entities created before the audit code existed have no row — both
--           correctly leave CreatedBy NULL. The UPDATE only touches rows where
--           CreatedBy IS NULL, so re-running is safe.
--
--           NO new index. The invitation-listing query filters by TenantId only
--           (derived status cannot be indexed) and invitation volume is low, so
--           the optional (TenantId, Email) index is intentionally NOT created.
--
--           Every statement is idempotent (sys.columns guards). No data loss,
--           no DROP.
-- ============================================================================

-- ── [dbo].[UserInvitations].RevokedAt ───────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[UserInvitations]')
      AND  name      = N'RevokedAt'
)
BEGIN
    ALTER TABLE [dbo].[UserInvitations]
        ADD [RevokedAt] DATETIME2 NULL;
    PRINT 'Column RevokedAt added to [dbo].[UserInvitations].';
END
ELSE
BEGIN
    PRINT 'Column RevokedAt already exists on [dbo].[UserInvitations] — skipped.';
END
GO

-- ── [dbo].[UserInvitations].RevokedByUserId ─────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[UserInvitations]')
      AND  name      = N'RevokedByUserId'
)
BEGIN
    ALTER TABLE [dbo].[UserInvitations]
        ADD [RevokedByUserId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column RevokedByUserId added to [dbo].[UserInvitations].';
END
ELSE
BEGIN
    PRINT 'Column RevokedByUserId already exists on [dbo].[UserInvitations] — skipped.';
END
GO

-- ── [dbo].[Entities].CreatedBy ──────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'CreatedBy'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [CreatedBy] UNIQUEIDENTIFIER NULL;
    PRINT 'Column CreatedBy added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column CreatedBy already exists on [dbo].[Entities] — skipped.';
END
GO

-- ── Backfill [dbo].[Entities].CreatedBy from the audit trail ────────────────
-- Earliest 'Entity.Created' audit row per entity wins. Idempotent: only rows
-- whose CreatedBy is still NULL are updated. Runs in its own batch so the column
-- added above is guaranteed to exist at parse time.

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'CreatedBy'
)
BEGIN
    ;WITH [Creation] AS
    (
        SELECT
            [EntityId],
            [UserId],
            ROW_NUMBER() OVER (
                PARTITION BY [EntityId]
                ORDER BY     [Timestamp] ASC
            ) AS [rn]
        FROM   [dbo].[AuditLogs]
        WHERE  [Action] = N'Entity.Created'
          AND  [UserId] IS NOT NULL
    )
    UPDATE e
        SET e.[CreatedBy] = c.[UserId]
    FROM   [dbo].[Entities] e
    JOIN   [Creation]       c
        ON  CAST(e.[EntityId] AS NVARCHAR(100)) = c.[EntityId]
        AND c.[rn] = 1
    WHERE  e.[CreatedBy] IS NULL;

    PRINT 'Backfilled [dbo].[Entities].[CreatedBy] for '
        + CAST(@@ROWCOUNT AS NVARCHAR(20))
        + ' row(s) from audit history.';
END
ELSE
BEGIN
    PRINT 'Column CreatedBy missing on [dbo].[Entities] — backfill skipped.';
END
GO
