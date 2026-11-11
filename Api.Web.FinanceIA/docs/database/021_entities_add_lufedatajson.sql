-- 021_entities_add_lufedatajson.sql
-- Adds LufeDataJson column to [dbo].[Entities].
-- Database First — do NOT run dotnet ef migrations add.
-- Executed by DBA on each target environment.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND name = N'LufeDataJson')
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [LufeDataJson] NVARCHAR(MAX) NULL;
END
GO
