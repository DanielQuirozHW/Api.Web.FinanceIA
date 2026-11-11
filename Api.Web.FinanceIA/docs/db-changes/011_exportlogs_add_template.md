# 011 — Add Template column to ExportLogs

## What changed and why

The export feature now supports multiple PDF templates (e.g. `standard`, `detailed`).
A nullable `Template` column (`NVARCHAR(20) NULL`) is added to `dbo.ExportLogs` so
that each export record tracks which template was used. Existing rows remain `NULL`,
meaning they were generated before template support was available.

## DDL

```sql
-- ============================================================================
-- Script  : 011_exportlogs_add_template.sql
-- Purpose : Add nullable Template column to ExportLogs for template-based exports
-- Date    : 2026-06-07
-- Author  : Claude (assisted)
-- Notes   : Nullable, no default, no constraint. Old rows stay NULL.
-- ============================================================================

ALTER TABLE [dbo].[ExportLogs]
    ADD [Template] NVARCHAR(20) NULL;
GO
```

Script location: `docs/database/011_exportlogs_add_template.sql`

## Pre-conditions

- Must be executed **before** deploying commit `fc59f90` (or any later commit
  containing the export-with-template feature) to production on **every** tenant
  database.
- If the column is missing on any tenant, the `ExportLog` INSERT will fail for
  that tenant at runtime.

## Rollback

The column is nullable and has no default or constraint, so it can be safely
dropped without data loss:

```sql
ALTER TABLE [dbo].[ExportLogs]
    DROP COLUMN [Template];
GO
```

## Verification

Run this query after applying the script. It must return exactly one row:

```sql
SELECT COLUMN_NAME
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'ExportLogs'
  AND  COLUMN_NAME = 'Template';
```

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
