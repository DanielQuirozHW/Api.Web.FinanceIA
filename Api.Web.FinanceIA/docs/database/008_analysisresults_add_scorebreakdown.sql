-- Migration : 008_analysisresults_add_scorebreakdown
-- Created   : 2026-05-08
-- Purpose   : Add ScoreBreakdownJson column to [dbo].[AnalysisResults] to persist
--             the per-component and per-ratio score breakdown produced by the
--             deterministic scoring engine.
-- Executed by DBA — do NOT run dotnet ef migrations add

ALTER TABLE [dbo].[AnalysisResults]
    ADD [ScoreBreakdownJson] NVARCHAR(MAX) NULL;
GO
