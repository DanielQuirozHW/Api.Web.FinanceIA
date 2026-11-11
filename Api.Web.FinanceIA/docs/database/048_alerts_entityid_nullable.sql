-- ============================================================================
-- Script  : 048_alerts_entityid_nullable.sql
-- Purpose : Make [dbo].[Alerts].[EntityId] NULLABLE so the table can hold
--           SYSTEM-LEVEL alerts — tenant-scoped, but tied to no PyME and no
--           analysis. The first consumer is the billing quota alert (80% / 100%
--           of the monthly plan), which is about the tenant's contract, not
--           about any one company it analysed.
-- Date    : 2026-08-31
-- Author  : Claude (assisted)
-- Notes   : The application already models this column as nullable and has for
--           some time — Models/Financia/Alert.cs declares `Guid? EntityId`
--           ("Null for system-level alerts"), AlertConfiguration maps it WITHOUT
--           .IsRequired(), and AlertsController renders EntityName = 'Sistema'
--           whenever it is null. The DATABASE is the only layer still saying
--           NOT NULL. This script closes that gap; no C# mapping changes.
--
--           WHY THE INDEX IS DROPPED AND RECREATED
--           [EntityId] is a key column of IX_Alerts_TenantId_EntityId, and SQL
--           Server refuses ALTER COLUMN on a column an index depends on. So the
--           index is dropped, the column altered, and the index recreated to the
--           exact definition in 001_initial_schema.sql — (TenantId ASC,
--           EntityId ASC), no INCLUDE, not filtered.
--
--           ...BUT ONLY IF IT IS ACTUALLY THERE. The live index set on this
--           table is genuinely uncertain, and the script does not guess. Script
--           013 altered [TenantId] after dropping only 'IX_Alerts_TenantId' — a
--           name that does not appear in 001 at all. The two indexes that 001
--           DOES create (IX_Alerts_TenantId_EntityId, IX_Alerts_TenantId_IsResolved)
--           both key on TenantId, so 013's ALTER COLUMN would have failed with
--           "The object 'IX_Alerts_TenantId_EntityId' is dependent on column
--           'TenantId'" unless someone had already dropped them by hand. Hence
--           the @hadIndex flag: the index is recreated ONLY if this script was
--           the one that dropped it. Recreating it unconditionally would silently
--           add an index back to a database where it was removed deliberately,
--           and that is the DBA's call, not this script's.
--
--           The whole thing is ONE batch on purpose. @hadIndex is a local
--           variable and a GO would discard it, so the drop, the alter and the
--           recreate have to stay together.
--
--           FK_Alerts_EntityId needs no attention: a nullability change does not
--           require dropping a foreign key, and NULL bypasses the FK check — a
--           system alert points at no entity and is not supposed to.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. Unlike scripts 039-042 this adds NO column, so EF
--           Core's model does not reference anything the database might be
--           missing and nothing that works today starts failing — there is no
--           'Invalid column name' failure mode here. What breaks is only the new
--           feature: the emission path inserts rows with EntityId = NULL, which a
--           NOT NULL column rejects. That insert is fault-isolated (BillingAlertService
--           catches, logs a Warning and returns), so publishing the code first
--           degrades to "billing quota alerts silently never appear" rather than
--           to an outage. Run the DDL first anyway, so the feature works from its
--           first request.
--
--           NO APP SERVICE RESTART REQUIRED. EF's model cache describes this
--           column as nullable already, so nothing it holds becomes stale.
--
--           Idempotent: guarded on sys.columns.is_nullable, so re-running is a
--           no-op that prints and exits.
-- ============================================================================

-- ── Alerts.EntityId → NULL ──────────────────────────────────────────────────

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id   = OBJECT_ID(N'[dbo].[Alerts]')
      AND  name        = N'EntityId'
      AND  is_nullable = 0
)
BEGIN
    DECLARE @hadIndex BIT = 0;

    IF EXISTS (
        SELECT 1
        FROM   sys.indexes
        WHERE  object_id = OBJECT_ID(N'[dbo].[Alerts]')
          AND  name      = N'IX_Alerts_TenantId_EntityId'
    )
    BEGIN
        SET @hadIndex = 1;
        DROP INDEX [IX_Alerts_TenantId_EntityId] ON [dbo].[Alerts];
        PRINT 'Index IX_Alerts_TenantId_EntityId dropped (it will be recreated below).';
    END
    ELSE
    BEGIN
        PRINT 'Index IX_Alerts_TenantId_EntityId not present — nothing to drop, and nothing will be recreated.';
    END

    ALTER TABLE [dbo].[Alerts]
        ALTER COLUMN [EntityId] UNIQUEIDENTIFIER NULL;

    PRINT 'Column [dbo].[Alerts].[EntityId] is now NULLABLE.';

    IF @hadIndex = 1
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_Alerts_TenantId_EntityId]
            ON [dbo].[Alerts] ([TenantId] ASC, [EntityId] ASC);
        PRINT 'Index IX_Alerts_TenantId_EntityId recreated to its 001_initial_schema definition.';
    END
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Alerts].[EntityId] is already NULLABLE — skipped.';
END
GO

-- ── Verification (DBA reference only — safe to run, reads nothing sensitive) ──
-- Confirms the column is nullable and reports which indexes the table now carries:
--
--   SELECT c.name, c.is_nullable
--   FROM   sys.columns c
--   WHERE  c.object_id = OBJECT_ID(N'[dbo].[Alerts]') AND c.name = N'EntityId';
--
--   SELECT i.name, i.type_desc
--   FROM   sys.indexes i
--   WHERE  i.object_id = OBJECT_ID(N'[dbo].[Alerts]') AND i.type > 0;
--
-- Expected after this script: is_nullable = 1. The index list is whatever the
-- database already had — this script adds none and removes none.
