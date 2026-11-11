-- ============================================================================
-- Script  : 071_legajo_items_and_incomes.sql
-- Purpose : The reconciled view of a persona humana's LEGAJO (M10, Phase 3b):
--             - create [dbo].[LegajoItems]: the reconciled inventory, one asset
--               or debt per row, with the market value (Manifestación de
--               bienes) and the fiscal value (Bienes Personales) side by side,
--               the reconciliation status and the analyst's edits;
--             - create [dbo].[LegajoIncomes]: incomes and sales by period and
--               source (DDJJ IVA, IIBB, Ganancias, certificación, manifestación,
--               monotributo);
--             - add ReviewOverride and ReviewReason to [dbo].[LegajoExtractions]
--               (the approval of a run that still fails a check is an override;
--               until now only the audit row knew it).
-- Date    : 2026-10-19
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, once on Financia-db, then restart the App
--           Service. The API binds ONE static FinanciaDefault connection
--           (Financia-db) and separates tenants by the TenantId column; if the
--           DDL procedure lists further business databases, run it on each of
--           them too, as with 069 and 070. Nothing below is scoped to one tenant.
--           EF maps both new tables and the two new [LegajoExtractions] columns,
--           so publishing first breaks every SELECT and INSERT against
--           [dbo].[LegajoExtractions] with "Invalid column name": the legajo
--           document list, the extraction review and the background extraction
--           worker. --verify-schema catches exactly that before the publish.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. The two new tables are
--           not read by the old code, and both new [LegajoExtractions] columns
--           are NULLABLE with no default, so the old INSERT and UPDATE
--           statements are unaffected.
--
--           SET OPTIONS, THE FIRST BATCH. Neither new table has a FILTERED index,
--           but [LegajoExtractions] already has one (UX_LegajoExtractions_Current,
--           070), so the ALTERs below need ANSI_NULLS and QUOTED_IDENTIFIER ON
--           (error 1934), as every DML on it already does. 001 set both database
--           defaults OFF. Run in SSMS / Azure Data Studio or with sqlcmd -I; the
--           SETs at the top cover a session that did not.
--
--           WHAT IS STORED, AND WHAT IS NOT. Both tables are REBUILT by the API
--           (LegajoReconciliationService) from the legajo's documents in Ready
--           with a current extraction run, read on their effective values
--           (corrections applied). The design keeps calculations out of the
--           database: no total, ratio, coherence figure or indicator is stored —
--           the API computes them at read time, and the person analysis will
--           freeze them in its own snapshot. Amounts are in PESOS (the printed
--           figures scaled by the document's amountsUnit), never multiplied by
--           the ownership percentage: both documents already state the person's
--           share.
--
--           [LegajoItems] IS REBUILT, NOT REWRITTEN. A rebuild keeps a row whose
--           ItemKey it computes again (same ItemId), rewrites its document-derived
--           columns EXCEPT the ones listed in EditedFields, never touches
--           AnalystNote, and keeps an AnalystConfirmed status only while the
--           same conflict (ConfirmedFingerprint) still stands. A row whose key it
--           no longer computes is DELETED: the analyst's edits on it remain in
--           [dbo].[AuditLogs]. UX_LegajoItems_Key is load-bearing: the API
--           detects a lost race between two rebuilds by its violation.
--
--           [LegajoIncomes] IS REPLACED WHOLE on every rebuild; it carries no
--           analyst state. UX_LegajoIncomes_Key admits one row per source,
--           concept and date range, the shape the rebuild produces.
--
--           FKs. Every FK below is NO ACTION. Documents are SOFT-deleted
--           (IsDeleted), so a document FK never blocks the application; a HARD
--           delete of a document or a run by hand must first delete the rows
--           that reference it (or run a rebuild after soft-deleting it, which
--           removes them). The EF model maps none of them, as 070 did.
--
--           ⚠ PII. Both tables hold a natural person's patrimony and income,
--           read from their paperwork: partidas, dominios, CBUs, the CUIT of the
--           companies they hold shares in, and free-text analyst notes. Treat
--           every row — and every export in the rollback — as personal data.
--           The API never logs a value or a note, only ids and counts.
--
--           CHECK CONSTRAINTS follow 069/070: WITH CHECK, each list mirrored by a
--           constants class the tests pin to this script (LegajoItemSides,
--           LegajoItemKinds, LegajoMatchKeyKinds, LegajoReconciliationStatuses,
--           LegajoIncomeSources, LegajoIncomeConcepts).
--
--           TRANSACTIONS: none. Every guarded block is one atomic statement.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 069 and 070 have run here. Expected: 3 rows.
--
--    SELECT name FROM sys.objects
--    WHERE  type = N'U'
--      AND  name IN (N'PersonLegajos', N'LegajoExtractions', N'Documents');
--
-- 2. Nothing of 071 exists yet (a first run). Expected: 0 rows; on a re-run the
--    objects found are skipped.
--
--    SELECT name FROM sys.objects
--    WHERE  type = N'U' AND name IN (N'LegajoItems', N'LegajoIncomes');
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
--      AND  name IN (N'ReviewOverride', N'ReviewReason');
-- ============================================================================

-- ── 0. Session options (see header) ─────────────────────────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. [dbo].[LegajoItems] ──────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[LegajoItems]
    (
        [ItemId]                UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_LegajoItems_ItemId] DEFAULT (NEWSEQUENTIALID()),   -- the API supplies its own
        [TenantId]              NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005; NOT a GUID
        [PersonLegajoId]        UNIQUEIDENTIFIER NOT NULL,
        [ItemKey]               NVARCHAR(300)    NOT NULL,   -- the rebuild identity: Partida:123, BP:Inmueble:CASA …
        [Side]                  NVARCHAR(10)     NOT NULL,   -- Activo | Pasivo
        [Kind]                  NVARCHAR(20)     NOT NULL,
        [MatchKeyKind]          NVARCHAR(10)     NOT NULL,
        [MatchKey]              NVARCHAR(60)     NULL,       -- normalized; PII (partida, dominio, CBU)
        [Description]           NVARCHAR(200)    NULL,
        [OwnershipPct]          DECIMAL(9, 4)    NULL,
        [MarketValue]           DECIMAL(19, 2)   NULL,       -- pesos; Manifestación de bienes
        [MarketValueDocumentId] UNIQUEIDENTIFIER NULL,
        [FiscalValue]           DECIMAL(19, 2)   NULL,       -- pesos; Bienes Personales
        [FiscalValueDocumentId] UNIQUEIDENTIFIER NULL,
        [Currency]              NVARCHAR(10)     NULL,       -- ISO code of the asset, when printed
        [IsActivityAsset]       BIT              NOT NULL
            CONSTRAINT [DF_LegajoItems_IsActivityAsset] DEFAULT (0),
        [IsHousing]             BIT              NOT NULL
            CONSTRAINT [DF_LegajoItems_IsHousing] DEFAULT (0),
        [LinkedCuit]            NVARCHAR(13)     NULL,       -- XX-XXXXXXXX-X of the company held
        [ReconciliationStatus]  NVARCHAR(30)     NOT NULL,
        [ConflictReasons]       NVARCHAR(200)    NULL,       -- CSV: OwnershipDiffers, BelowFiscalFloor, CurrencyDiffers
        [EditedFields]          NVARCHAR(100)    NULL,       -- CSV of the fields the analyst set; a rebuild keeps them
        [AnalystNote]           NVARCHAR(1000)   NULL,       -- PII; never touched by a rebuild
        [ConfirmedFingerprint]  NVARCHAR(64)     NULL,       -- SHA-256 hex of the conflict the analyst confirmed
        [CreatedAt]             DATETIME2        NOT NULL
            CONSTRAINT [DF_LegajoItems_CreatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedAt]             DATETIME2        NOT NULL
            CONSTRAINT [DF_LegajoItems_UpdatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedBy]             UNIQUEIDENTIFIER NULL,       -- [dbo].[Users].UserId; no FK, per 030/069; NULL = a rebuild

        CONSTRAINT [PK_LegajoItems]
            PRIMARY KEY CLUSTERED ([ItemId] ASC),

        CONSTRAINT [FK_LegajoItems_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]),

        CONSTRAINT [FK_LegajoItems_MarketValueDocument]
            FOREIGN KEY ([MarketValueDocumentId]) REFERENCES [dbo].[Documents] ([DocumentId]),

        CONSTRAINT [FK_LegajoItems_FiscalValueDocument]
            FOREIGN KEY ([FiscalValueDocumentId]) REFERENCES [dbo].[Documents] ([DocumentId]),

        -- The code's lists are LegajoItemSides / LegajoItemKinds / LegajoMatchKeyKinds /
        -- LegajoReconciliationStatuses; a test pins each to this script.
        CONSTRAINT [CK_LegajoItems_Side]
            CHECK ([Side] IN (N'Activo', N'Pasivo')),

        CONSTRAINT [CK_LegajoItems_Kind]
            CHECK ([Kind] IN (N'Inmueble', N'Rodado', N'Participacion', N'Deposito', N'Inversion',
                              N'Credito', N'Deuda', N'Otro')),

        CONSTRAINT [CK_LegajoItems_MatchKeyKind]
            CHECK ([MatchKeyKind] IN (N'Partida', N'Dominio', N'Cuit', N'Cbu', N'Otro', N'Ninguno')),

        CONSTRAINT [CK_LegajoItems_ReconciliationStatus]
            CHECK ([ReconciliationStatus] IN (N'Matched', N'OnlyInBienesPersonales', N'OnlyInManifestacion',
                                              N'Conflict', N'AnalystConfirmed')),

        CONSTRAINT [CK_LegajoItems_OwnershipPct]
            CHECK ([OwnershipPct] IS NULL OR ([OwnershipPct] >= 0 AND [OwnershipPct] <= 100))
    );
    PRINT 'Table [dbo].[LegajoItems] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[LegajoItems] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
      AND  name      = N'IX_LegajoItems_TenantId_PersonLegajoId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_LegajoItems_TenantId_PersonLegajoId]
        ON [dbo].[LegajoItems] ([TenantId] ASC, [PersonLegajoId] ASC);
    PRINT 'Index [IX_LegajoItems_TenantId_PersonLegajoId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_LegajoItems_TenantId_PersonLegajoId] already exists — skipped.';
END
GO

-- ONE row per rebuild identity in a legajo. The name is load-bearing (see header).
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
      AND  name      = N'UX_LegajoItems_Key'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_LegajoItems_Key]
        ON [dbo].[LegajoItems] ([TenantId] ASC, [PersonLegajoId] ASC, [ItemKey] ASC);
    PRINT 'Index [UX_LegajoItems_Key] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_LegajoItems_Key] already exists — skipped.';
END
GO

-- ── 2. [dbo].[LegajoIncomes] ────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoIncomes]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[LegajoIncomes]
    (
        [IncomeId]       UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_LegajoIncomes_IncomeId] DEFAULT (NEWSEQUENTIALID()),   -- the API supplies its own
        [TenantId]       NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005; NOT a GUID
        [PersonLegajoId] UNIQUEIDENTIFIER NOT NULL,
        [Period]         NVARCHAR(7)      NULL,       -- YYYY-MM (a month) | YYYY (a calendar year) | NULL (another range)
        [PeriodFrom]     DATE             NOT NULL,   -- first day the amount covers
        [PeriodTo]       DATE             NOT NULL,   -- last day the amount covers
        [Source]         NVARCHAR(20)     NOT NULL,
        [Concept]        NVARCHAR(30)     NOT NULL,
        [Amount]         DECIMAL(19, 2)   NOT NULL,   -- pesos
        [Currency]       NVARCHAR(10)     NOT NULL
            CONSTRAINT [DF_LegajoIncomes_Currency] DEFAULT (N'ARS'),
        [DocumentId]     UNIQUEIDENTIFIER NOT NULL,
        [ExtractionId]   UNIQUEIDENTIFIER NOT NULL,
        [CreatedAt]      DATETIME2        NOT NULL
            CONSTRAINT [DF_LegajoIncomes_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_LegajoIncomes]
            PRIMARY KEY CLUSTERED ([IncomeId] ASC),

        CONSTRAINT [FK_LegajoIncomes_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]),

        CONSTRAINT [FK_LegajoIncomes_Documents]
            FOREIGN KEY ([DocumentId]) REFERENCES [dbo].[Documents] ([DocumentId]),

        CONSTRAINT [FK_LegajoIncomes_LegajoExtractions]
            FOREIGN KEY ([ExtractionId]) REFERENCES [dbo].[LegajoExtractions] ([ExtractionId]),

        -- The code's lists are LegajoIncomeSources / LegajoIncomeConcepts.
        CONSTRAINT [CK_LegajoIncomes_Source]
            CHECK ([Source] IN (N'Iva', N'Facturacion', N'Ganancias', N'Certificacion', N'Manifestacion',
                                N'Iibb', N'Monotributo')),

        CONSTRAINT [CK_LegajoIncomes_Concept]
            CHECK ([Concept] IN (N'Ventas', N'Renta3ra', N'Renta4ta', N'MontoConsumido', N'IngresoCertificado',
                                 N'Otro')),

        CONSTRAINT [CK_LegajoIncomes_Period]
            CHECK ([Period] IS NULL
                   OR [Period] LIKE N'[12][0-9][0-9][0-9]'
                   OR [Period] LIKE N'[12][0-9][0-9][0-9]-[01][0-9]'),

        CONSTRAINT [CK_LegajoIncomes_PeriodRange]
            CHECK ([PeriodFrom] <= [PeriodTo])
    );
    PRINT 'Table [dbo].[LegajoIncomes] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[LegajoIncomes] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoIncomes]')
      AND  name      = N'IX_LegajoIncomes_TenantId_PersonLegajoId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_LegajoIncomes_TenantId_PersonLegajoId]
        ON [dbo].[LegajoIncomes] ([TenantId] ASC, [PersonLegajoId] ASC);
    PRINT 'Index [IX_LegajoIncomes_TenantId_PersonLegajoId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_LegajoIncomes_TenantId_PersonLegajoId] already exists — skipped.';
END
GO

-- ONE row per source, concept and range in a legajo: the rebuild sums inside the
-- winning document and keeps one document per source and range.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoIncomes]')
      AND  name      = N'UX_LegajoIncomes_Key'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_LegajoIncomes_Key]
        ON [dbo].[LegajoIncomes] ([TenantId] ASC, [PersonLegajoId] ASC, [Source] ASC, [Concept] ASC,
                                  [PeriodFrom] ASC, [PeriodTo] ASC);
    PRINT 'Index [UX_LegajoIncomes_Key] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_LegajoIncomes_Key] already exists — skipped.';
END
GO

-- ── 3. [dbo].[LegajoExtractions]: the approval override ─────────────────────
-- Set with ReviewedBy/ReviewedAt on approval; cleared with them when a
-- correction reopens the document. NULL on every run never approved.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
      AND  name      = N'ReviewOverride'
)
BEGIN
    ALTER TABLE [dbo].[LegajoExtractions]
        ADD [ReviewOverride] BIT NULL;
    PRINT 'Column ReviewOverride added to [dbo].[LegajoExtractions].';
END
ELSE
BEGIN
    PRINT 'Column ReviewOverride already exists on [dbo].[LegajoExtractions] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
      AND  name      = N'ReviewReason'
)
BEGIN
    ALTER TABLE [dbo].[LegajoExtractions]
        ADD [ReviewReason] NVARCHAR(1000) NULL;
    PRINT 'Column ReviewReason added to [dbo].[LegajoExtractions].';
END
ELSE
BEGIN
    PRINT 'Column ReviewReason already exists on [dbo].[LegajoExtractions] — skipped.';
END
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the CHECKs, FKs or indexes below. These queries are the evidence.
--
-- 1. Every CHECK exists, enabled and TRUSTED. Expected: 9 rows, every one 0 0.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name, is_disabled, is_not_trusted
--    FROM   sys.check_constraints
--    WHERE  name IN (N'CK_LegajoItems_Side', N'CK_LegajoItems_Kind', N'CK_LegajoItems_MatchKeyKind',
--                    N'CK_LegajoItems_ReconciliationStatus', N'CK_LegajoItems_OwnershipPct',
--                    N'CK_LegajoIncomes_Source', N'CK_LegajoIncomes_Concept', N'CK_LegajoIncomes_Period',
--                    N'CK_LegajoIncomes_PeriodRange');
--
-- 2. The foreign keys. Expected: 6 rows, every one 0 0 NO_ACTION.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name,
--           is_disabled, is_not_trusted, delete_referential_action_desc
--    FROM   sys.foreign_keys
--    WHERE  name IN (N'FK_LegajoItems_PersonLegajos', N'FK_LegajoItems_MarketValueDocument',
--                    N'FK_LegajoItems_FiscalValueDocument', N'FK_LegajoIncomes_PersonLegajos',
--                    N'FK_LegajoIncomes_Documents', N'FK_LegajoIncomes_LegajoExtractions');
--
-- 3. The indexes. Expected: these four rows.
--
--    SELECT OBJECT_NAME(i.object_id) AS TableName, i.name, i.is_unique, i.has_filter
--    FROM   sys.indexes i
--    WHERE  i.name IN (N'IX_LegajoItems_TenantId_PersonLegajoId', N'UX_LegajoItems_Key',
--                      N'IX_LegajoIncomes_TenantId_PersonLegajoId', N'UX_LegajoIncomes_Key')
--    ORDER  BY TableName, i.name;
--    -- expected: LegajoIncomes  IX_LegajoIncomes_TenantId_PersonLegajoId  0 0
--    --           LegajoIncomes  UX_LegajoIncomes_Key                      1 0
--    --           LegajoItems    IX_LegajoItems_TenantId_PersonLegajoId    0 0
--    --           LegajoItems    UX_LegajoItems_Key                        1 0
--
-- 4. The new [LegajoExtractions] columns, both NULL on every existing run.
--    Expected: 2 rows (is_nullable 1), then 0.
--
--    SELECT name, is_nullable FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
--      AND  name IN (N'ReviewOverride', N'ReviewReason');
--    SELECT COUNT(*) FROM [dbo].[LegajoExtractions]
--    WHERE  ReviewOverride IS NOT NULL OR ReviewReason IS NOT NULL;
--
-- 5. After the deploy, an analyst (or POST /api/person-legajos/{id}/reconciliation/rebuild)
--    fills the tables; until then they are empty. Nothing is backfilled here: the
--    first approval, correction or rebuild of each legajo builds its rows.
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps both tables and both
-- columns below. Run with the same SET options as the script.
--
-- (a) EXPORT FIRST what the analysts wrote. ⚠ PERSONAL DATA: a person's patrimony
--     and income, and the analysts' notes. The rows themselves can be rebuilt from
--     the documents; the analyst columns (EditedFields, AnalystNote,
--     ConfirmedFingerprint, and the values they protect) and the approval
--     overrides cannot.
--
--    SELECT * FROM [dbo].[LegajoItems];
--    SELECT * FROM [dbo].[LegajoIncomes];
--    SELECT ExtractionId, TenantId, DocumentId, ReviewedBy, ReviewedAt, ReviewOverride, ReviewReason
--    FROM   [dbo].[LegajoExtractions] WHERE ReviewOverride IS NOT NULL OR ReviewReason IS NOT NULL;
--
-- (b) The tables and the columns:
--
--    DROP TABLE [dbo].[LegajoIncomes];
--    DROP TABLE [dbo].[LegajoItems];
--    ALTER TABLE [dbo].[LegajoExtractions] DROP COLUMN [ReviewReason], [ReviewOverride];
--    GO
-- ============================================================================
