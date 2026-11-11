-- ============================================================================
-- Script  : 041_creditlineoperations_add_cancellation_audit.sql
-- Purpose : Add [CanceladaEl] and [CanceladaPor] to
--           [dbo].[CreditLineOperations], so a cancelled operation records WHO
--           cancelled it and WHEN — the mirror of what 040 did for vigencia.
-- Date    : 2026-08-24
-- Author  : Claude (assisted)
-- Notes   : Script 040 gave the 'Aprobada' → 'Vigente' transition two audit
--           columns. Cancellation got none, so the end of an operation's life
--           was recorded less well than its beginning: the row said 'Cancelada'
--           and nothing else, and the only trace of who did it and when lived
--           in an AuditLogs entry that named neither the old nor the new status.
--
--           That last omission mattered, because 'Cancelada' is reached from
--           two different business events:
--
--             Aprobada  → Cancelada — the deal FELL THROUGH ("se cayó"). It was
--                         granted on paper and never became real.
--             Vigente   → Cancelada — the line was CLOSED. It was in force and
--                         its life ended.
--
--           Both leave an identical row. Only the audit trail can separate them,
--           and until now it did not try. The application change that accompanies
--           this script writes both ends of the transition into the AuditLogs
--           entry; these two columns carry the who and the when on the row
--           itself, exactly as VigenteConfirmadaEl / VigenteConfirmadaPor do.
--
--           THIS SUPERSEDES a claim in the companion document of 040
--           (docs/db-changes/040_creditlineoperations_add_vigencia_audit.md),
--           which stated that "cancellation gets no equivalent row-level
--           columns" because AuditLogs already held the same facts. That reading
--           held only as long as nobody needed to tell the two cancellation
--           paths apart, or to sort and filter by cancellation date without
--           joining to the audit table. Both turned out to be needed. That
--           document keeps its original paragraph, with a pointer to this one.
--
--           BOTH COLUMNS ARE NULL, WITH NO DEFAULT AND NO BACKFILL. Operations
--           cancelled before this script ran keep their 'Cancelada' status and
--           get NULL audit values on purpose: we do not know who cancelled them,
--           or when, and NULL is the honest answer. Treat these columns as
--           meaningful only for operations cancelled AFTER this script ran.
--
--           No CHECK constraint and no change to the [Status] DEFAULT. 040
--           already moved that default to 'Aprobada' and explained why the
--           vocabulary carries no CHECK (it is validated in the API — see
--           029/036); 'Cancelada' is not a new value and needs neither.
--
--           No index either. Both columns are read as attributes of an operation
--           already located by its primary key or by the existing
--           TenantId/CreditLineId index — nothing searches BY cancellation date.
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
--           Unlike 040 there is NO "Portal first" requirement. 040 introduced a
--           new status value the Portal's renderer had no branch for; this
--           release introduces none. The Portal gains one optional read field
--           (canceladaEl, null unless cancelled) and can ignore it entirely.
--
--           Idempotent: both columns are guarded, so the script can be re-run
--           safely. Re-running never resets a value already written.
-- ============================================================================

-- ── CreditLineOperations.CanceladaEl ────────────────────────────────────────
-- Nullable: only a cancelled operation has anything to record. NULL means either
-- "still live" or "cancelled before this script existed".
-- DATETIME2 per the convention in docs/database/README.md — never DATETIME.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'CanceladaEl'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD [CanceladaEl] DATETIME2 NULL;
    PRINT 'Column CanceladaEl added to [dbo].[CreditLineOperations].';
END
ELSE
BEGIN
    PRINT 'Column CanceladaEl already exists on [dbo].[CreditLineOperations] — skipped.';
END
GO

-- ── CreditLineOperations.CanceladaPor ───────────────────────────────────────
-- UNIQUEIDENTIFIER to match [CreatedByUserId] and [VigenteConfirmadaPor] on this
-- same table — user ids on the credit-line tables are GUIDs referencing
-- [dbo].[Users].UserId, not strings. No FK is declared, for the same reason
-- those two have none: a deleted user must not make an operation's history
-- unreadable.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'CanceladaPor'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD [CanceladaPor] UNIQUEIDENTIFIER NULL;
    PRINT 'Column CanceladaPor added to [dbo].[CreditLineOperations].';
END
ELSE
BEGIN
    PRINT 'Column CanceladaPor already exists on [dbo].[CreditLineOperations] — skipped.';
END
GO
