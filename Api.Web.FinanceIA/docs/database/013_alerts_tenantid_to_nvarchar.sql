-- 013 — Alerts: change TenantId from uniqueidentifier to nvarchar(100)
-- Aligns with the fix applied to other business tables in 005_fix_business_tenantid_to_nvarchar.sql.
-- The tenant system uses string slugs (e.g. 'SgrOneClickSandboxFB2'), not GUIDs.
-- Run on: FinancIA business database
-- =====================================================================

BEGIN TRANSACTION;

-- 1. Drop existing index on TenantId
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Alerts_TenantId' AND object_id = OBJECT_ID('dbo.Alerts'))
    DROP INDEX IX_Alerts_TenantId ON dbo.Alerts;

-- 2. Alter column from uniqueidentifier to nvarchar(100)
ALTER TABLE dbo.Alerts
    ALTER COLUMN TenantId NVARCHAR(100) NOT NULL;

-- 3. Recreate index
CREATE NONCLUSTERED INDEX IX_Alerts_TenantId
    ON dbo.Alerts (TenantId);

COMMIT TRANSACTION;
