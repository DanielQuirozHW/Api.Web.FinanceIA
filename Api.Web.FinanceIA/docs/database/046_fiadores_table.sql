-- ============================================================================
-- Script  : 046_fiadores_table.sql
-- Purpose : Create [dbo].[Fiadores] — the fiadores/garantes module
--           (epic step 1: data foundation only).
--           A fiador is a guarantor of a PyME: a natural person (or entity)
--           identified by their OWN CUIT, selected by the analyst from the
--           PyME's LUFE socios/autoridades list (LufeData.Autoridades[]).
-- Date    : 2026-08-28
-- Author  : Claude (assisted)
-- Notes   : Tenant-scoped like every other table (TenantId NVARCHAR(100) NOT NULL,
--           filtered in the repository).
--           The fiador hangs off [dbo].[Entities], NOT off an analysis: the
--           guarantor relationship belongs to the PyME and persists across
--           analyses. N fiadores per PyME.
--
--           ONE Active fiador per (TenantId, EntityId, CUIT): the same person
--           cannot be added twice to the same PyME while active. Enforced by the
--           FILTERED UNIQUE INDEX [UX_Fiadores_ActivePerEntityCuit], following the
--           [UX_CreditLines_ActivePerEntityCurrency] precedent from script 030.
--           Status is 'Active' or 'Removed' ONLY — a fiador the analyst added is
--           soft-removed, never hard-deleted, so the selection history survives.
--           There is no derived state here: unlike CreditLineOperations, nothing
--           expires, so no 'Vencida'-style read-time computation applies.
--
--           CUIT is NVARCHAR(13) to match [dbo].[Entities].CUIT exactly — the CUIT
--           is stored as a string, never as a numeric type. (The LUFE source field
--           Autoridades[].cuit arrives as a JSON number; converting it to this
--           column's string form is the ingesting code's job, in a later PR.)
--
--           NosisDataJson / NosisFetchedAt hold the fiador's OWN NOSIS snapshot,
--           queried by the FIADOR's CUIT rather than the PyME's, in the same
--           NosisData shape stored in [dbo].[Entities].NosisDataJson. BOTH SHIP
--           NULL AND STAY NULL IN THIS DELIVERY: the query that populates them is a
--           LATER PR, blocked on the vendor widening the VR variable group to
--           include the 19 "green" variables. Treat the snapshot as write-once for
--           now; NosisFetchedAt is the room left for a refresh policy later.
--           The name deliberately differs from the NosisDataUpdatedAt column added
--           to [dbo].[Entities] by script 042: that one tracks a repeatable refresh,
--           this one records when the single snapshot was taken. Not a typo.
--
--           Origen records how the CUIT got here ('Lufe' = picked from the socios
--           list). 'Manual' is reserved for a later analyst-typed CUIT. Like
--           CreditLineOperations.Type, the allowed values of Origen and Status are
--           validated in the API on purpose — NO CHECK constraint here.
--
--           CreatedByUserId references [dbo].[Users].UserId by convention but carries
--           NO FK constraint, matching script 030 (which likewise declares none on
--           its CreatedByUserId columns).
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. Every column below is mapped in FiadorConfiguration, so EF Core
--           emits them in any SELECT or INSERT against Fiadores. This delivery adds no
--           query that touches the table, but the ordering rule stands for the PR that
--           does: DDL first, then publish, then restart the App Service so EF's model
--           cache is rebuilt.
--
--           Idempotent: the table, the FK constraint and both indexes are each guarded
--           so the script can be re-run safely.
-- ============================================================================

-- ── Fiadores table ──────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[Fiadores]
    (
        [FiadorId]        UNIQUEIDENTIFIER   NOT NULL
            CONSTRAINT [DF_Fiadores_FiadorId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]        NVARCHAR(100)      NOT NULL,
        [EntityId]        UNIQUEIDENTIFIER   NOT NULL,   -- FK -> [dbo].[Entities].EntityId (the PyME)
        [CUIT]            NVARCHAR(13)       NOT NULL,   -- the fiador's OWN CUIT, not the PyME's
        [Denominacion]    NVARCHAR(300)      NULL,       -- name as it came from Autoridades[].Denominacion
        [Cargo]           NVARCHAR(200)      NULL,       -- from Autoridades[].Cargo (socio, presidente, etc.)
        [PorcAccionista]  DECIMAL(5, 2)      NULL,       -- from Autoridades[].PorcAccionista, when informed
        [Origen]          NVARCHAR(20)       NOT NULL
            CONSTRAINT [DF_Fiadores_Origen] DEFAULT ('Lufe'),        -- Lufe | Manual (validated in API, no CHECK)
        [NosisDataJson]   NVARCHAR(MAX)      NULL,       -- fiador's NOSIS snapshot (NosisData shape); NULL until a later PR queries it
        [NosisFetchedAt]  DATETIME2          NULL,       -- when that snapshot was taken; NULL until first query
        [Status]          NVARCHAR(20)       NOT NULL
            CONSTRAINT [DF_Fiadores_Status] DEFAULT ('Active'),      -- Active | Removed ONLY (soft-remove)
        [CreatedByUserId] UNIQUEIDENTIFIER   NOT NULL,   -- [dbo].[Users].UserId; no FK, per script 030
        [CreatedAt]       DATETIME2          NOT NULL
            CONSTRAINT [DF_Fiadores_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_Fiadores]
            PRIMARY KEY CLUSTERED ([FiadorId] ASC)
    );
    PRINT 'Table [dbo].[Fiadores] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[Fiadores] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Fiadores_Entities]')
)
BEGIN
    ALTER TABLE [dbo].[Fiadores]
        ADD CONSTRAINT [FK_Fiadores_Entities]
            FOREIGN KEY ([EntityId]) REFERENCES [dbo].[Entities] ([EntityId]);
    PRINT 'Foreign key [FK_Fiadores_Entities] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Fiadores_Entities] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────

-- Enforces ONE Active fiador per (TenantId, EntityId, CUIT): the same person cannot
-- be added twice to the same PyME while active. A Removed row does not block re-adding.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  name      = N'UX_Fiadores_ActivePerEntityCuit'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_Fiadores_ActivePerEntityCuit]
        ON [dbo].[Fiadores] ([TenantId] ASC, [EntityId] ASC, [CUIT] ASC)
        WHERE [Status] = 'Active';
    PRINT 'Index [UX_Fiadores_ActivePerEntityCuit] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_Fiadores_ActivePerEntityCuit] already exists — skipped.';
END
GO

-- Serves the list-per-PyME read path: WHERE TenantId = @t AND EntityId = @e.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  name      = N'IX_Fiadores_TenantId_EntityId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Fiadores_TenantId_EntityId]
        ON [dbo].[Fiadores] ([TenantId] ASC, [EntityId] ASC);
    PRINT 'Index [IX_Fiadores_TenantId_EntityId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Fiadores_TenantId_EntityId] already exists — skipped.';
END
GO
