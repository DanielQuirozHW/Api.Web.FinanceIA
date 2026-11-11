-- ============================================================================
-- Script  : 030_credit_lines_tables.sql
-- Purpose : Create [dbo].[CreditLines] and [dbo].[CreditLineOperations] — the
--           credit-lines module (Delivery 1: data foundation only).
--           [dbo].[CreditLines] is the general credit line (cupo) per
--           (Entity, Currency). [dbo].[CreditLineOperations] is a child line
--           under a cupo, tied to an approved analysis.
-- Date    : 2026-07-20
-- Author  : Claude (assisted)
-- Notes   : Tenant-scoped like every other table (TenantId NVARCHAR(100) NOT NULL,
--           filtered in the repository).
--           ONE Active CreditLine per (TenantId, EntityId, Currency): ARS and USD
--           may coexist for the same entity, but not two Active of the same
--           currency. Enforced by the FILTERED UNIQUE INDEX
--           [UX_CreditLines_ActivePerEntityCurrency]. A new cupo supersedes the
--           previous one of that currency (Status flips Active -> Superseded).
--           NOTE: this is the FIRST filtered index in this database.
--           CreditLineOperations.Status is 'Vigente' or 'Cancelada' ONLY.
--           'Vencida' is DERIVED at read time (ValidUntil < today) — it is never
--           stored, and there is no background job that flips it.
--           CreditLineOperations.Type is the SGR One Click catalog code 0..17
--           (same catalog as [dbo].[Analyses].CreditLineType). The 0..17 range is
--           validated in the API on purpose — NO CHECK constraint here.
--           The existing credit-line columns on [dbo].[Analyses] (ApprovedAmount,
--           CreditLineType, etc.) are left untouched as read-only history.
--           Idempotent: tables, FK constraints and indexes are each guarded so the
--           script can be re-run safely.
-- ============================================================================

-- ── CreditLines table ───────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLines]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[CreditLines]
    (
        [CreditLineId]    UNIQUEIDENTIFIER   NOT NULL
            CONSTRAINT [DF_CreditLines_CreditLineId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]        NVARCHAR(100)      NOT NULL,
        [EntityId]        UNIQUEIDENTIFIER   NOT NULL,   -- FK -> [dbo].[Entities].EntityId
        [Amount]          DECIMAL(18, 2)     NOT NULL,
        [Currency]        NVARCHAR(3)        NOT NULL,   -- ISO 4217 code (e.g. 'ARS', 'USD')
        [Status]          NVARCHAR(20)       NOT NULL
            CONSTRAINT [DF_CreditLines_Status] DEFAULT ('Active'),   -- Active | Superseded
        [CreatedByUserId] UNIQUEIDENTIFIER   NOT NULL,
        [CreatedAt]       DATETIME2          NOT NULL
            CONSTRAINT [DF_CreditLines_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_CreditLines]
            PRIMARY KEY CLUSTERED ([CreditLineId] ASC)
    );
    PRINT 'Table [dbo].[CreditLines] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[CreditLines] already exists — skipped.';
END
GO

-- ── CreditLineOperations table ──────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[CreditLineOperations]
    (
        [OperationId]     UNIQUEIDENTIFIER   NOT NULL
            CONSTRAINT [DF_CreditLineOperations_OperationId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]        NVARCHAR(100)      NOT NULL,
        [CreditLineId]    UNIQUEIDENTIFIER   NOT NULL,   -- FK -> [dbo].[CreditLines].CreditLineId
        [AnalysisId]      UNIQUEIDENTIFIER   NOT NULL,   -- FK -> [dbo].[Analyses].AnalysisId
        [Type]            INT                NOT NULL,   -- SGR One Click catalog 0..17 (validated in API, no CHECK)
        [Amount]          DECIMAL(18, 2)     NOT NULL,
        [Currency]        NVARCHAR(3)        NOT NULL,   -- ISO 4217 code (e.g. 'ARS', 'USD')
        [ValidUntil]      DATE               NULL,       -- vigencia; 'Vencida' is derived when this < today
        [Notes]           NVARCHAR(1000)     NULL,
        [Status]          NVARCHAR(20)       NOT NULL
            CONSTRAINT [DF_CreditLineOperations_Status] DEFAULT ('Vigente'),   -- Vigente | Cancelada ONLY
        [CreatedByUserId] UNIQUEIDENTIFIER   NOT NULL,
        [CreatedAt]       DATETIME2          NOT NULL
            CONSTRAINT [DF_CreditLineOperations_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_CreditLineOperations]
            PRIMARY KEY CLUSTERED ([OperationId] ASC)
    );
    PRINT 'Table [dbo].[CreditLineOperations] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[CreditLineOperations] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_CreditLines_Entities]')
)
BEGIN
    ALTER TABLE [dbo].[CreditLines]
        ADD CONSTRAINT [FK_CreditLines_Entities]
            FOREIGN KEY ([EntityId]) REFERENCES [dbo].[Entities] ([EntityId]);
    PRINT 'Foreign key [FK_CreditLines_Entities] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_CreditLines_Entities] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_CreditLineOperations_CreditLines]')
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD CONSTRAINT [FK_CreditLineOperations_CreditLines]
            FOREIGN KEY ([CreditLineId]) REFERENCES [dbo].[CreditLines] ([CreditLineId]);
    PRINT 'Foreign key [FK_CreditLineOperations_CreditLines] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_CreditLineOperations_CreditLines] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_CreditLineOperations_Analyses]')
)
BEGIN
    ALTER TABLE [dbo].[CreditLineOperations]
        ADD CONSTRAINT [FK_CreditLineOperations_Analyses]
            FOREIGN KEY ([AnalysisId]) REFERENCES [dbo].[Analyses] ([AnalysisId]);
    PRINT 'Foreign key [FK_CreditLineOperations_Analyses] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_CreditLineOperations_Analyses] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────

-- Enforces ONE Active credit line per (TenantId, EntityId, Currency).
-- FIRST filtered index in this database.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLines]')
      AND  name      = N'UX_CreditLines_ActivePerEntityCurrency'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_CreditLines_ActivePerEntityCurrency]
        ON [dbo].[CreditLines] ([TenantId] ASC, [EntityId] ASC, [Currency] ASC)
        WHERE [Status] = 'Active';
    PRINT 'Index [UX_CreditLines_ActivePerEntityCurrency] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_CreditLines_ActivePerEntityCurrency] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLines]')
      AND  name      = N'IX_CreditLines_TenantId_EntityId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_CreditLines_TenantId_EntityId]
        ON [dbo].[CreditLines] ([TenantId] ASC, [EntityId] ASC);
    PRINT 'Index [IX_CreditLines_TenantId_EntityId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_CreditLines_TenantId_EntityId] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'IX_CreditLineOperations_TenantId_CreditLineId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_CreditLineOperations_TenantId_CreditLineId]
        ON [dbo].[CreditLineOperations] ([TenantId] ASC, [CreditLineId] ASC);
    PRINT 'Index [IX_CreditLineOperations_TenantId_CreditLineId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_CreditLineOperations_TenantId_CreditLineId] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
      AND  name      = N'IX_CreditLineOperations_TenantId_AnalysisId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_CreditLineOperations_TenantId_AnalysisId]
        ON [dbo].[CreditLineOperations] ([TenantId] ASC, [AnalysisId] ASC);
    PRINT 'Index [IX_CreditLineOperations_TenantId_AnalysisId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_CreditLineOperations_TenantId_AnalysisId] already exists — skipped.';
END
GO
