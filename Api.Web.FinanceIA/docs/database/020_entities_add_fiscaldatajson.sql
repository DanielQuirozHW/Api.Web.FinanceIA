-- 020_entities_add_fiscaldatajson.sql
-- Adds FiscalDataJson column to [dbo].[Entities].
-- Database First — do NOT run dotnet ef migrations add.
-- Executed by DBA on each target environment.

ALTER TABLE [dbo].[Entities]
    ADD [FiscalDataJson] NVARCHAR(MAX) NULL;
GO
