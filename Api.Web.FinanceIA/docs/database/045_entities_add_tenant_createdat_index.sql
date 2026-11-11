-- ============================================================================
-- Script  : 045_entities_add_tenant_createdat_index.sql
-- Purpose : Add the covering index [dbo].[Entities] needs to serve
--           GET /api/entities as an index seek instead of a full table scan.
-- Date    : 2026-08-25
-- Author  : Claude (assisted)
-- Notes   : [dbo].[Entities] has IX_Entities_TenantId (script 001, recreated by
--           005) and the unique UQ_Entities_CUIT. Neither can order a page:
--           there is no index involving [CreatedAt].
--
--           The list endpoint now runs, per page:
--
--               SELECT EntityId, CUIT, BusinessName, TradeName, Sector,
--                      MipymeCategory, IsMonitored, RiskScore,
--                      RiskScoreUpdatedAt, CreatedAt, CreatedBy
--               FROM   [dbo].[Entities]
--               WHERE  TenantId = @tenant
--               ORDER BY CreatedAt DESC, EntityId
--               OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
--
--           With only IX_Entities_TenantId that is a seek followed by a sort of
--           the whole tenant. With this index the range is already in CreatedAt
--           DESC order and only @take rows are looked up.
--
--           Key columns, in order:
--             TenantId  -> the equality predicate; every query here is
--                          tenant-scoped
--             CreatedAt -> DESC, matching the ORDER BY, so no sort operator is
--                          needed
--             EntityId  -> the tie-break. CreatedAt is not unique — a bulk
--                          import lands many entities in the same instant — and
--                          OFFSET/FETCH over a non-total order lets rows sharing
--                          a timestamp appear on two pages or on neither. It is
--                          also the clustering key, so it is already present in
--                          the leaf; naming it costs nothing and states intent.
--
--           No INCLUDE list. The nine remaining projected columns are narrow but
--           there are nine of them, and widening the index to cover them would
--           duplicate most of the useful width of the table for a lookup that is
--           bounded by page size (<= 100 rows), not by tenant size.
--
--           ⚠ This index makes IX_Entities_TenantId a redundant PREFIX: any plan
--           that used it can use this one instead. Dropping it would reclaim
--           space and remove a write-time maintenance cost, but that is a
--           separate DBA decision and is deliberately NOT done here. Leaving
--           both in place is correct, only slightly wasteful.
--
--           NOT a deploy-blocker, unlike the column-adding scripts 039-042.
--           No application code names this index and EF Core's model is
--           unaffected by its absence, so the endpoint returns correct results
--           either way — just with a sort instead of an ordered range. Run it
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

-- ── Entities.IX_Entities_TenantId_CreatedAt ─────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'IX_Entities_TenantId_CreatedAt'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Entities_TenantId_CreatedAt]
        ON [dbo].[Entities] ([TenantId] ASC, [CreatedAt] DESC, [EntityId] ASC)
        WITH (ONLINE = ON);
    PRINT 'Index IX_Entities_TenantId_CreatedAt created on [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Index IX_Entities_TenantId_CreatedAt already exists on [dbo].[Entities] — skipped.';
END
GO
