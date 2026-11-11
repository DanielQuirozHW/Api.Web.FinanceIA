-- ============================================================================
-- Script  : 076_users_role_admit_superadmin.sql
-- Purpose : Widen [dbo].[Users] CHECK constraint [CK_Users_Role] so a user row
--           can carry Role = 'SuperAdmin' — the read-only support role that also
--           owns the two operator writes (PUT /api/billing/config and
--           PUT /api/bff/settings/tenant-profile).
-- Date    : 2026-10-28
-- Author  : Claude (assisted)
-- Notes   : WHY IT IS NEEDED
--           The JWT role claim is copied from [Users].[Role] at token exchange
--           (BffController.ExchangeFirebaseTokenAsync → TokenService.IssueAsync).
--           001_initial_schema created CK_Users_Role admitting only Analyst,
--           RiskManager, Admin and Auditor, and no later script widened it, so
--           Roles.SuperAdmin existed in code with no way to hold it: inserting or
--           updating a user to 'SuperAdmin' fails with a CHECK violation.
--
--           WHAT IT DOES NOT CHANGE
--           [dbo].[UserInvitations] CHECK [CK_UserInvitations_Role] stays at the
--           four client roles ON PURPOSE: a SuperAdmin is granted by the DBA,
--           never invited from the portal (SettingsController.ValidRoles excludes
--           it too). No column, type or index changes; existing rows untouched.
--
--           ⚠ RUN THIS SCRIPT IN EVERY FINANCIA (TENANT BUSINESS) DATABASE the
--           DDL procedure lists — [dbo].[Users] lives there, not in Default.
--           The API does NOT depend on it: EF maps no check constraint for
--           [Users], so publishing before or after is equally safe. It only has
--           to run BEFORE the DBA creates or promotes a SuperAdmin user row.
--           `--verify-schema` CANNOT see it (it compares table and column NAMES,
--           never constraints) — check with the query at the end.
--
--           NO APP SERVICE RESTART REQUIRED.
--
--           Idempotent: guarded on sys.check_constraints.definition, so re-running
--           (or running it on a database whose constraint the DBA already widened
--           by hand) is a no-op that prints and exits. DROP + ADD run in one
--           transaction under XACT_ABORT, so a failure never leaves [Users]
--           without its constraint.
-- ============================================================================

SET XACT_ABORT ON;
GO

-- ── CK_Users_Role → + 'SuperAdmin' ─────────────────────────────────────────

IF OBJECT_ID(N'[dbo].[Users]', N'U') IS NULL
BEGIN
    RAISERROR(N'076 not applied: table [dbo].[Users] does not exist in this database. Is this a Financia database?', 16, 1);
END
ELSE IF EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Users]')
      AND  name             = N'CK_Users_Role'
      AND  definition LIKE N'%SuperAdmin%'
)
BEGIN
    PRINT 'Constraint [CK_Users_Role] already admits SuperAdmin — skipped.';
END
ELSE
BEGIN
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1
        FROM   sys.check_constraints
        WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Users]')
          AND  name             = N'CK_Users_Role'
    )
    BEGIN
        ALTER TABLE [dbo].[Users] DROP CONSTRAINT [CK_Users_Role];
    END

    ALTER TABLE [dbo].[Users] WITH CHECK
        ADD CONSTRAINT [CK_Users_Role]
        CHECK ([Role] IN (N'Analyst', N'RiskManager', N'Admin', N'Auditor', N'SuperAdmin'));

    COMMIT TRANSACTION;

    PRINT 'Constraint [CK_Users_Role] now admits Analyst, RiskManager, Admin, Auditor, SuperAdmin.';
END
GO

-- ── Verification (DBA reference only — safe to run, reads nothing sensitive) ──
-- Expected after this script: one row, definition listing all five roles,
-- is_not_trusted = 0 and is_disabled = 0.
--
--   SELECT name, definition, is_not_trusted, is_disabled
--   FROM   sys.check_constraints
--   WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Users]') AND name = N'CK_Users_Role';
--
-- And the invitation constraint, which must still list exactly FOUR roles:
--
--   SELECT name, definition
--   FROM   sys.check_constraints
--   WHERE  parent_object_id = OBJECT_ID(N'[dbo].[UserInvitations]') AND name = N'CK_UserInvitations_Role';
--
-- ── Rollback (DBA reference only) ───────────────────────────────────────────
-- Fails BY DESIGN while any row still holds Role = 'SuperAdmin' (WITH CHECK
-- validates existing rows) — demote or deactivate those users first.
--
--   BEGIN TRANSACTION;
--   ALTER TABLE [dbo].[Users] DROP CONSTRAINT [CK_Users_Role];
--   ALTER TABLE [dbo].[Users] WITH CHECK ADD CONSTRAINT [CK_Users_Role]
--       CHECK ([Role] IN (N'Analyst', N'RiskManager', N'Admin', N'Auditor'));
--   COMMIT TRANSACTION;
