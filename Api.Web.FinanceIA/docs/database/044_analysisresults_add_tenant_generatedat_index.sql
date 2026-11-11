-- ============================================================================
-- Script  : 044_analysisresults_add_tenant_generatedat_index.sql
-- Purpose : Add the covering index [dbo].[AnalysisResults] needs to serve
--           GET /api/analyses as an index seek instead of a full table scan.
-- Date    : 2026-08-25
-- Author  : Claude (assisted)
-- Notes   : [dbo].[AnalysisResults] currently has NO nonclustered index at all —
--           only PK_AnalysisResults (clustered on ResultId) and the unique
--           UQ_AnalysisResults_AnalysisId. There is not even one on TenantId.
--
--           The list endpoint now runs, per page:
--
--               SELECT ResultId, AnalysisId, GeneratedAt, AiSuggestedScore,
--                      AiSuggestedLevel, ScoreBreakdownJson
--               FROM   [dbo].[AnalysisResults]
--               WHERE  TenantId = @tenant
--               ORDER BY GeneratedAt DESC, ResultId
--               OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
--
--           Without this index that is a full scan of the tenant's rows plus a
--           sort. With it, it is a seek on TenantId, a range already in
--           GeneratedAt DESC order, and a key lookup for at most @take rows.
--
--           Key columns, in order:
--             TenantId    -> the equality predicate; every query here is
--                            tenant-scoped
--             GeneratedAt -> DESC, matching the ORDER BY, so the range is read
--                            in order and no sort operator is needed
--             ResultId    -> the tie-break. GeneratedAt is not unique, and
--                            OFFSET/FETCH over a non-total order lets rows
--                            sharing a timestamp appear on two pages or on
--                            neither. It is also the clustering key, so it is
--                            already present in the leaf — naming it in the key
--                            costs nothing and makes the intent explicit.
--
--           Deliberately NO INCLUDE list. ScoreBreakdownJson is NVARCHAR(MAX)
--           and must be read for the four flattened scoring signals, so a key
--           lookup happens regardless; INCLUDE-ing it would duplicate the blob
--           into the index and roughly double its storage for no gain. The
--           lookup is bounded by page size (<= 100 rows), not by tenant size.
--
--           NOT a deploy-blocker, unlike the column-adding scripts 039-042.
--           No application code names this index and EF Core's model is
--           unaffected by its absence, so the endpoint returns correct results
--           either way — just with a scan and a sort instead of a seek. Run it
--           BEFORE publishing the matching code anyway, per the ordering
--           convention in this folder, so the release is fast from its first
--           request. No App Service restart is required.
--
--           Run on every tenant business database. Online build is used so the
--           table stays writable during creation; drop the WITH clause if the
--           target edition does not support ONLINE (SQL Server Standard below
--           2019). Azure SQL Database supports it.
--
--           Idempotent: guarded on sys.indexes, so re-running is a no-op. It
--           never drops or rebuilds an index that already exists.
-- ============================================================================

-- ── AnalysisResults.IX_AnalysisResults_TenantId_GeneratedAt ─────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'IX_AnalysisResults_TenantId_GeneratedAt'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_AnalysisResults_TenantId_GeneratedAt]
        ON [dbo].[AnalysisResults] ([TenantId] ASC, [GeneratedAt] DESC, [ResultId] ASC)
        WITH (ONLINE = ON);
    PRINT 'Index IX_AnalysisResults_TenantId_GeneratedAt created on [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Index IX_AnalysisResults_TenantId_GeneratedAt already exists on [dbo].[AnalysisResults] — skipped.';
END
GO
