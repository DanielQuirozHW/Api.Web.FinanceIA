-- Migration : 006_analysisresults_add_bcra_columns
-- Created   : 2026-05-08
-- Purpose   : Add BcraDataJson and MacroDataJson columns to [dbo].[AnalysisResults]
--             to store BCRA Central de Deudores and macroeconomic data fetched
--             at analysis time.
-- Executed by DBA — do NOT run dotnet ef migrations add

ALTER TABLE [dbo].[AnalysisResults]
    ADD [BcraDataJson]  NVARCHAR(MAX) NULL,
        [MacroDataJson] NVARCHAR(MAX) NULL;
GO
