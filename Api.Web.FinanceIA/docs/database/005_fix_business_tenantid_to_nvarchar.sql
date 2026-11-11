-- Migration : 005_fix_business_tenantid_to_nvarchar
-- Created   : 2026-05-05
-- Purpose   : Migrate TenantId from UNIQUEIDENTIFIER to NVARCHAR(100) in all
--             business tables so they accept Firebase tenant slugs
--             (e.g. SgrOneClickSandboxFB2) instead of GUIDs.
--             Users.TenantId was already migrated in script 003.
--
-- IMPORTANT : Run this script on financia-dev before any business write operation.
--             The database has no business data to preserve at this point.
--             Executed by DBA — do NOT run dotnet ef migrations add.
-- ---------------------------------------------------------------------------

-- SQL Server requires dropping indexes on a column before altering its type.

-- ── Entities ────────────────────────────────────────────────────────────────

DROP INDEX IF EXISTS [IX_Entities_TenantId] ON [dbo].[Entities];
GO

ALTER TABLE [dbo].[Entities]
    ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;
GO

CREATE NONCLUSTERED INDEX [IX_Entities_TenantId]
    ON [dbo].[Entities] ([TenantId]);
GO

-- ── Documents ────────────────────────────────────────────────────────────────

DROP INDEX IF EXISTS [IX_Documents_TenantId]         ON [dbo].[Documents];
DROP INDEX IF EXISTS [IX_Documents_TenantId_EntityId] ON [dbo].[Documents];
DROP INDEX IF EXISTS [IX_Documents_TenantId_IsDeleted] ON [dbo].[Documents];
GO

ALTER TABLE [dbo].[Documents]
    ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;
GO

CREATE NONCLUSTERED INDEX [IX_Documents_TenantId]
    ON [dbo].[Documents] ([TenantId]);

CREATE NONCLUSTERED INDEX [IX_Documents_TenantId_EntityId]
    ON [dbo].[Documents] ([TenantId], [EntityId]);

CREATE NONCLUSTERED INDEX [IX_Documents_TenantId_IsDeleted]
    ON [dbo].[Documents] ([TenantId], [IsDeleted]);
GO

-- ── AnalysisResults ──────────────────────────────────────────────────────────

ALTER TABLE [dbo].[AnalysisResults]
    ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;
GO

-- ── Analyses ─────────────────────────────────────────────────────────────────

ALTER TABLE [dbo].[Analyses]
    ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;
GO
