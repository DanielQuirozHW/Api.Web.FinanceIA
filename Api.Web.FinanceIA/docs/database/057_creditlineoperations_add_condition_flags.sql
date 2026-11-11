-- ============================================================================
-- Script  : 057_creditlineoperations_add_condition_flags.sql
-- Purpose : Add [ASolaFirma] and [RequiereContragarantiaReal] to
--           [dbo].[CreditLineOperations], so the two underwriting conditions an
--           operation may carry are recorded as structured fields instead of
--           being lost or buried in the free-text [Notes].
-- Date    : 2026-09-04
-- Author  : Claude (assisted)
-- Notes   : Two independent conditions, per operation — not per cupo and not per
--           analysis, because two operations under the SAME cupo, drawn from the
--           same analysis, can differ on both:
--
--             ASolaFirma                 — the line was granted "a sola firma".
--             RequiereContragarantiaReal — the line requires a "contragarantía
--                                          real".
--
--           An operation may carry both, either or neither. The two are not
--           opposites and neither implies anything about the other.
--
--           THESE ARE PURE METADATA. They do not participate in any rule:
--           - they do not change whether an operation occupies cupo
--             (OccupiesCupo is still "stored status is not 'Cancelada'", and a
--             flagged operation reserves saldo exactly like an unflagged one);
--           - they do not affect Utilizado / Disponible / OperationsCount, the
--             create-time sum rule, or the raise underwater check;
--           - they do not touch the 'Aprobada' → 'Vigente' → 'Cancelada'
--             lifecycle, and no status is derived from them;
--           - nothing validates them. Any of the four combinations is legal.
--           They are recorded at creation and read back; that is the whole
--           feature. Do not wire them into the cupo maths later without saying
--           so in a new script — the "metadata only" contract is what makes it
--           safe for the DBA to backfill every existing row to 0 below.
--
--           NOT NULL WITH A NAMED DEFAULT OF 0, not nullable. This is the
--           inverse of the choice 040 and 041 made for their audit columns next
--           door, and deliberately so. Those columns record an EVENT that either
--           happened or did not, so NULL is the honest "we do not know when this
--           operation became vigente". These two record a CONDITION of the
--           granted line, and every operation has an answer for them: an
--           operation that was not granted a sola firma simply was not, which is
--           false, not unknown. So 0 is a true statement about every existing
--           row rather than a plausible-looking guess, and the DEFAULT backfills
--           them inside the ALTER itself. (SQL Server also requires a DEFAULT to
--           add a NOT NULL column to a populated table at all.)
--
--           Naming follows this table's own precedent: [CreditLineOperations]
--           already mixes English structural names ([Status], [Amount],
--           [ValidUntil]) with Spanish business names ([VigenteConfirmadaEl],
--           [VigenteConfirmadaPor], [CanceladaEl], [CanceladaPor] from 040/041).
--           "A sola firma" and "contragarantía real" are Argentine SGR terms of
--           art with no faithful English equivalent, so they stay Spanish.
--           Unaccented, like every other identifier in this schema and codebase.
--
--           No CHECK constraint: BIT is already a two-value domain, so there is
--           nothing left to constrain.
--
--           No index. Neither column is a predicate anywhere — both are read as
--           attributes of an operation already located by its primary key or by
--           the existing IX_CreditLineOperations_TenantId_CreditLineId. A BIT
--           column is also near-useless as a leading index key.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on EVERY
--           tenant database. Both columns are mapped in
--           CreditLineOperationConfiguration, and CreditLineOperationRepository
--           reads and inserts the FULL entity with no projection, so EF Core
--           emits them in every SELECT and every INSERT against
--           [dbo].[CreditLineOperations]. Against a database without them,
--           operation creation, confirmar-vigencia, cancellation AND the whole
--           credit-line structure read fail with "Invalid column name", and no
--           application-level try/catch can protect against it. This is the same
--           deploy-order constraint as 040 and 041, on the same table, for the
--           same reason. Restart the App Service after deploying so EF's model
--           cache is rebuilt.
--
--           NO PORTAL-FIRST REQUIREMENT, unlike 040. That script introduced a
--           new [Status] value the Portal's renderer had no branch for; this one
--           introduces no enum value and changes no existing field's shape. The
--           API's operation payload gains two additive booleans (plus the
--           authorship fields from the ownership change shipping alongside),
--           which a Portal that does not know them ignores.
--
--           Idempotent: both columns are guarded, so the script can be re-run
--           safely. Re-running never rewrites a flag already recorded.
-- ============================================================================

-- ── CreditLineOperations.ASolaFirma ─────────────────────────────────────────
-- NOT NULL with a named default constraint so existing rows backfill to 0 in the
-- ALTER itself. 0 = this operation was not granted a sola firma, which is a true
-- statement about every operation created before this script ran.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'ASolaFirma'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD [ASolaFirma] BIT NOT NULL
            CONSTRAINT [DF_CreditLineOperations_ASolaFirma] DEFAULT (0);
    PRINT 'Column ASolaFirma added to [dbo].[CreditLineOperations].';
END
ELSE
BEGIN
    PRINT 'Column ASolaFirma already exists on [dbo].[CreditLineOperations] — skipped.';
END
GO

-- ── CreditLineOperations.RequiereContragarantiaReal ─────────────────────────
-- Same shape and same reasoning as the flag above, and independent of it: the
-- two are separate conditions, so there is no constraint tying them together.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'RequiereContragarantiaReal'
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD [RequiereContragarantiaReal] BIT NOT NULL
            CONSTRAINT [DF_CreditLineOperations_RequiereContragarantiaReal] DEFAULT (0);
    PRINT 'Column RequiereContragarantiaReal added to [dbo].[CreditLineOperations].';
END
ELSE
BEGIN
    PRINT 'Column RequiereContragarantiaReal already exists on [dbo].[CreditLineOperations] — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. Both columns exist, BIT, NOT NULL, each with its default constraint → 2 rows
--
--    SELECT c.name AS ColumnName, t.name AS DataType, c.is_nullable, dc.definition
--    FROM   sys.columns c
--    JOIN   sys.types   t  ON t.user_type_id = c.user_type_id
--    LEFT   JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
--      AND  c.name IN (N'ASolaFirma', N'RequiereContragarantiaReal')
--    ORDER  BY c.name;
--    -- expected: ASolaFirma                 bit  0  ((0))
--    --           RequiereContragarantiaReal bit  0  ((0))
--
-- 2. Every pre-existing row backfilled to 0, and no row is NULL. Run this
--    IMMEDIATELY after the ALTER, before the deploy — once the code is live,
--    newly created operations may legitimately carry 1.
--
--    SELECT [ASolaFirma], [RequiereContragarantiaReal], COUNT(*) AS Operations
--    FROM   [dbo].[CreditLineOperations]
--    GROUP  BY [ASolaFirma], [RequiereContragarantiaReal];
--    -- expected immediately after the script: exactly one row, 0 / 0.
--
-- 3. After creating one operation through the API with both conditions set,
--    the flags round-trip → 1 row
--
--    SELECT TOP (1) [OperationId], [Status], [ASolaFirma], [RequiereContragarantiaReal]
--    FROM   [dbo].[CreditLineOperations]
--    ORDER  BY [CreatedAt] DESC;
--    -- expected: Status = 'Aprobada', both flags = 1
--
-- 4. The flags changed nothing about cupo consumption. For any cupo, Utilizado
--    must be the sum of its non-'Cancelada' operations REGARDLESS of the flags:
--
--    SELECT o.[CreditLineId],
--           SUM(o.[Amount])                                              AS Utilizado,
--           SUM(CASE WHEN o.[ASolaFirma] = 1 THEN o.[Amount] ELSE 0 END) AS DeLasCualesASolaFirma
--    FROM   [dbo].[CreditLineOperations] o
--    WHERE  o.[Status] <> N'Cancelada'
--    GROUP  BY o.[CreditLineId];
--    -- expected: Utilizado matches what GET /api/credit-lines reports for that
--    --           cupo; the second column is informational and must not change it.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: the deployed code maps both columns, so
-- dropping them under a running deployment breaks operation reads and writes.
-- Each DROP COLUMN also removes its default constraint, so there is no separate
-- constraint drop.
--
--    ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [RequiereContragarantiaReal];
--    GO
--    ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [ASolaFirma];
--    GO
--
-- What is lost is the two conditions on any operation created while the release
-- was live; they exist in no other column and in no audit row. Export them first
-- if that matters:
--
--    SELECT [OperationId], [ASolaFirma], [RequiereContragarantiaReal]
--    FROM   [dbo].[CreditLineOperations]
--    WHERE  [ASolaFirma] = 1 OR [RequiereContragarantiaReal] = 1;
--
-- Nothing else is affected: no other table references these columns, no index or
-- CHECK constraint covers them, and because they feed no rule, dropping them
-- changes no cupo figure and no operation status.
-- ============================================================================
