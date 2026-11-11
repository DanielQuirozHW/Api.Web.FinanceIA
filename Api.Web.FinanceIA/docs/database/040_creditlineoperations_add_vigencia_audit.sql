-- ============================================================================
-- Script  : 040_creditlineoperations_add_vigencia_audit.sql
-- Purpose : Add [VigenteConfirmadaEl] and [VigenteConfirmadaPor] to
--           [dbo].[CreditLineOperations], and move the [Status] default from
--           'Vigente' to 'Aprobada', so an operation records WHO put it in
--           force and WHEN — and is no longer born already in force.
-- Date    : 2026-08-17
-- Author  : Claude (assisted)
-- Notes   : Operations were born 'Vigente', which collapsed two distinct
--           business states into one. The real lifecycle has an earlier step:
--           an approved, granted line is APROBADA (committed on paper) and only
--           becomes VIGENTE when an analyst confirms it is actually in force —
--           and in between it can fall through to 'Cancelada'.
--
--             Aprobada  — granted and committed, not yet confirmed in force.
--                         RESERVES cupo from creation (see below).
--             Vigente   — an analyst confirmed it is in force. The two columns
--                         added here record who did that and when.
--             Cancelada — closed, or it fell through ("se cayó").
--             Vencida   — DERIVED at read time from a VIGENTE operation past
--                         ValidUntil. Never stored, and an Aprobada operation
--                         never derives to it: 'Vencida' is a statement about a
--                         line that WAS in force.
--
--           This supersedes the "'Vigente' or 'Cancelada' ONLY" vocabulary
--           documented in the CREATE TABLE comment in script 030. That comment
--           is left as written — numbered scripts are an executed historical
--           record — so THIS header is the current vocabulary.
--
--           An APROBADA OPERATION RESERVES THE CUPO. Utilizado / Disponible /
--           OperationsCount now count every operation whose stored status is not
--           'Cancelada', so the reservation exists from creation and confirming
--           vigencia does NOT move Utilizado — it was already counted. This is
--           the same predicate the analysis-rejection guard already used, so the
--           two no longer disagree.
--
--           BOTH COLUMNS ARE NULL, WITH NO DEFAULT AND NO BACKFILL. Existing
--           operations keep their 'Vigente' status and get NULL audit values on
--           purpose: we do not know who made them vigente, or when, and NULL is
--           the honest answer. Treat these columns as meaningful only for
--           operations confirmed AFTER this script ran.
--
--           No CHECK constraint is added on [Status]. Script 030 deliberately
--           creates none (the vocabulary is validated in the API, as with
--           CreditLines.Status — see 029/036), so 'Aprobada' needs no constraint
--           change. NVARCHAR(20) already accommodates it.
--
--           The [Status] DEFAULT flip is inert for the application — EF Core
--           always writes Status explicitly, so the constraint never fires from
--           the API. It is not inert as documentation: leaving DEFAULT 'Vigente'
--           means a manual or DBA INSERT silently creates an operation that is
--           already in force and that no analyst ever confirmed, which is
--           precisely the failure this release exists to prevent.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE. Both columns
--           are mapped in CreditLineOperationConfiguration, so EF Core emits
--           them in every SELECT and INSERT against CreditLineOperations.
--           Deploying the code against a database without them breaks operation
--           reads AND writes with "Invalid column name", and no
--           application-level try/catch can protect against it. The columns
--           being nullable makes this less severe than 039 (nothing needs
--           backfilling) but NOT optional. Restart the App Service after
--           deploying so EF's model cache is rebuilt.
--
--           Idempotent: both columns and both halves of the default-constraint
--           swap are guarded, so the script can be re-run safely. Re-running
--           never resets a value already written.
-- ============================================================================

-- ── CreditLineOperations.VigenteConfirmadaEl ────────────────────────────────
-- Nullable: only an operation an analyst has confirmed has anything to record.
-- NULL means either "still Aprobada" or "confirmed before this script existed".
-- DATETIME2 per the convention in docs/database/README.md — never DATETIME.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'VigenteConfirmadaEl'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD [VigenteConfirmadaEl] DATETIME2 NULL;
    PRINT 'Column VigenteConfirmadaEl added to [dbo].[CreditLineOperations].';
END
ELSE
BEGIN
    PRINT 'Column VigenteConfirmadaEl already exists on [dbo].[CreditLineOperations] — skipped.';
END
GO

-- ── CreditLineOperations.VigenteConfirmadaPor ───────────────────────────────
-- UNIQUEIDENTIFIER to match [CreatedByUserId] on this same table and
-- [CreditLines].[CreatedByUserId] — user ids on the credit-line tables are
-- GUIDs referencing [dbo].[Users].UserId, not strings. No FK is declared, for
-- the same reason CreatedByUserId has none: a deleted user must not make an
-- operation's history unreadable.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'VigenteConfirmadaPor'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD [VigenteConfirmadaPor] UNIQUEIDENTIFIER NULL;
    PRINT 'Column VigenteConfirmadaPor added to [dbo].[CreditLineOperations].';
END
ELSE
BEGIN
    PRINT 'Column VigenteConfirmadaPor already exists on [dbo].[CreditLineOperations] — skipped.';
END
GO

-- ── Status default: 'Vigente' → 'Aprobada' (drop half) ──────────────────────
-- A default constraint cannot be altered in place, so it is dropped and
-- recreated. Guarded on the definition NOT already mentioning 'Aprobada', so a
-- re-run of this script drops nothing.

IF EXISTS (
    SELECT 1
    FROM   sys.default_constraints
    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name             = N'DF_CreditLineOperations_Status'
      AND  definition NOT LIKE N'%Aprobada%'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        DROP CONSTRAINT [DF_CreditLineOperations_Status];
    PRINT 'Default constraint [DF_CreditLineOperations_Status] dropped (was ''Vigente'').';
END
ELSE
BEGIN
    PRINT 'Default constraint [DF_CreditLineOperations_Status] already targets ''Aprobada'' or is absent — no drop needed.';
END
GO

-- ── Status default: 'Vigente' → 'Aprobada' (add half) ───────────────────────
-- Existing rows are untouched: a DEFAULT applies to future INSERTs that omit
-- the column, never retroactively. No 'Vigente' operation becomes 'Aprobada'.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.default_constraints
    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name             = N'DF_CreditLineOperations_Status'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD CONSTRAINT [DF_CreditLineOperations_Status]
        DEFAULT (N'Aprobada') FOR [Status];
    PRINT 'Default constraint [DF_CreditLineOperations_Status] added — DEFAULT N''Aprobada''.';
END
ELSE
BEGIN
    PRINT 'Default constraint [DF_CreditLineOperations_Status] already exists — skipped.';
END
GO
