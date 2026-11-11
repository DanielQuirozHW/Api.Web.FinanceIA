-- ============================================================================
-- Script  : 018_analysisresults_add_scoringnarrative.sql
-- Purpose : Add nullable ScoringNarrative column to AnalysisResults
-- Date    : 2026-06-08
-- Author  : Claude (assisted)
-- Notes   : Separates scoring justification from ExecutiveSummary.
--           Existing rows keep ExecutiveSummary as-is; ScoringNarrative = NULL.
--           New analyses will populate both columns independently.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'ScoringNarrative'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [ScoringNarrative] NVARCHAR(MAX) NULL;
END
GO
