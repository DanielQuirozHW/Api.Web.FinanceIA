-- ============================================================================
-- Script  : 075_user_invitations_inviter_nullable.sql
-- Purpose : Make [dbo].[UserInvitations].[InvitedByUserId] NULLABLE so a brand-new
--           client database — zero rows in [dbo].[Users] — can hold the invitation
--           of its FIRST administrator. That invitation is issued by
--           POST /api/bff/setup/first-admin (guarded by the Setup:BootstrapKey App
--           Setting), not by an admin, so it has no inviter to point at.
-- Date    : 2026-10-25
-- Author  : Claude (assisted)
-- Notes   : NULL means "issued by the first-admin bootstrap". Every row written by
--           the normal invite (POST /api/bff/settings/users/invite) keeps a real
--           inviter. The column keeps its type (UNIQUEIDENTIFIER) and its FOREIGN
--           KEY to [dbo].[Users]([UserId]): a nullability change does not require
--           dropping a foreign key, and NULL bypasses the FK check (same reasoning
--           as 048_alerts_entityid_nullable.sql). Existing rows are untouched.
--
--           WHY THE SCRIPT REFUSES WHEN AN INDEX DEPENDS ON THE COLUMN
--           The CREATE TABLE for [dbo].[UserInvitations] is not in this folder, so
--           its live index set is not known from the repository. SQL Server refuses
--           ALTER COLUMN on a column an index depends on. The EF mapping declares
--           no index on [InvitedByUserId] (only UQ_UserInvitations_Token), so none
--           is expected. If one IS found, the script stops with an error naming
--           it and alters NOTHING — recreating an index this script cannot see the
--           definition of would be a guess, and that is the DBA's call (see 048).
--
--           ⚠ RUN THIS SCRIPT IN EVERY FINANCIA (TENANT BUSINESS) DATABASE BEFORE
--           PUBLISHING THE API THAT CONTAINS THE FIRST-ADMIN BOOTSTRAP. Unlike a
--           new column, this changes nothing EF selects, so publishing first does
--           NOT break existing reads or writes — but the bootstrap insert would
--           then fail with "Cannot insert the value NULL" (HTTP 500), and
--           `--verify-schema` CANNOT catch it: the verifier compares table and
--           column NAMES only, never nullability. Check with the query at the end.
--
--           NO APP SERVICE RESTART REQUIRED: the EF model of the matching build
--           already describes the column as nullable.
--
--           Idempotent: guarded on sys.columns.is_nullable, so re-running is a
--           no-op that prints and exits. No transaction (one statement).
-- ============================================================================

-- ── UserInvitations.InvitedByUserId → NULL ─────────────────────────────────

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id   = OBJECT_ID(N'[dbo].[UserInvitations]')
      AND  name        = N'InvitedByUserId'
      AND  is_nullable = 0
)
BEGIN
    -- TOP (1), not STRING_AGG: STRING_AGG needs database compatibility level 140+.
    DECLARE @dependentIndex SYSNAME;

    SELECT TOP (1) @dependentIndex = i.name
    FROM   sys.indexes       AS i
    INNER JOIN sys.index_columns AS ic
            ON ic.object_id = i.object_id
           AND ic.index_id  = i.index_id
    INNER JOIN sys.columns   AS c
            ON c.object_id = ic.object_id
           AND c.column_id = ic.column_id
    WHERE  i.object_id = OBJECT_ID(N'[dbo].[UserInvitations]')
      AND  c.name      = N'InvitedByUserId';

    IF @dependentIndex IS NOT NULL
    BEGIN
        RAISERROR(
            N'075 not applied: index %s depends on [dbo].[UserInvitations].[InvitedByUserId]. Drop it (and any other index on that column), re-run this script, then recreate them.',
            16, 1, @dependentIndex);
    END
    ELSE
    BEGIN
        ALTER TABLE [dbo].[UserInvitations]
            ALTER COLUMN [InvitedByUserId] UNIQUEIDENTIFIER NULL;

        PRINT 'Column [dbo].[UserInvitations].[InvitedByUserId] is now NULLABLE (FK kept).';
    END
END
ELSE
BEGIN
    PRINT 'Column [dbo].[UserInvitations].[InvitedByUserId] is already NULLABLE (or absent) — skipped.';
END
GO

-- ── Verification (DBA reference only — safe to run, reads nothing sensitive) ──
-- Expected after this script: is_nullable = 1, and the FK still listed.
--
--   SELECT c.name, c.is_nullable, TYPE_NAME(c.user_type_id) AS type_name
--   FROM   sys.columns c
--   WHERE  c.object_id = OBJECT_ID(N'[dbo].[UserInvitations]') AND c.name = N'InvitedByUserId';
--
--   SELECT fk.name
--   FROM   sys.foreign_keys fk
--   WHERE  fk.parent_object_id = OBJECT_ID(N'[dbo].[UserInvitations]');
