-- =============================================================================
-- 011 — Change UserInvitations.TenantId from UNIQUEIDENTIFIER to NVARCHAR(100)
-- =============================================================================
-- Reason : UserInvitations.TenantId was UNIQUEIDENTIFIER but all other tables
--          (Users, Documents, AnalysisResults, etc.) use NVARCHAR(100) for
--          TenantId (e.g. 'SgrOneClickSandboxFB2'). This caused Guid.Parse()
--          failures at runtime.
-- Impact : Column type change; existing rows with GUID values will be converted
--          to their string representation. If the table has no rows yet, this is
--          a no-op data-wise.
-- =============================================================================

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. Drop the default constraint on TenantId if one exists
DECLARE @ConstraintName NVARCHAR(200);
SELECT @ConstraintName = dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = OBJECT_ID('dbo.UserInvitations')
  AND c.name = 'TenantId';

IF @ConstraintName IS NOT NULL
    EXEC('ALTER TABLE [dbo].[UserInvitations] DROP CONSTRAINT [' + @ConstraintName + ']');

-- 2. Alter column type
ALTER TABLE [dbo].[UserInvitations]
    ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;

COMMIT TRANSACTION;
GO

-- Verify
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo'
  AND TABLE_NAME   = 'UserInvitations'
  AND COLUMN_NAME  = 'TenantId';
-- Expected: TenantId | nvarchar | 100
