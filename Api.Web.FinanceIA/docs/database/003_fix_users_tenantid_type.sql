-- ============================================================
-- Script  : 003_fix_users_tenantid_type.sql
-- Date    : 2026-05-03
-- Author  : DBA / FinanceIA team
-- ============================================================
-- Decision: Align Users.TenantId and Users.CreatedBy with the
-- tenant-slug string type used in Credenciales and
-- TenantFirebaseConfig (NVARCHAR(100)).
--
-- The original schema used UNIQUEIDENTIFIER for TenantId, which
-- was inconsistent with the rest of the platform where TenantId
-- is a string slug (e.g. "acme"), not a GUID.
--
-- Users is empty at the time of this change so no data migration
-- is required.
--
-- CreatedBy is also changed from UNIQUEIDENTIFIER to NVARCHAR(100)
-- so it can store the tenant slug of the creating admin, consistent
-- with the TenantId type.
-- ============================================================

USE [financia-dev];
GO

ALTER TABLE [dbo].[Users]
    ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;
GO

ALTER TABLE [dbo].[Users]
    ALTER COLUMN [CreatedBy] NVARCHAR(100) NULL;
GO
