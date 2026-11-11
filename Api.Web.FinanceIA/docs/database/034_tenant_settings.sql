-- ============================================================================
-- Script  : 034_tenant_settings.sql
-- Purpose : Create [dbo].[TenantSettings] — per-tenant product-behaviour config
--           as free-form key/value pairs (booleans, strings or JSON). The first
--           consumer is 'creditline.mode' ('sgr' default / 'simple'); more settings
--           (e.g. which integrations feed the scoring engine) follow.
-- Date    : 2026-08-03
-- Author  : Claude (assisted)
-- Notes   : This is DISTINCT from [dbo].[ScoringConfig], which holds numeric engine
--           parameters (ConfigValue DECIMAL(10,4)). ScoringConfig is intentionally
--           NOT touched here — numeric engine parameters vs. product behaviour are
--           different concerns.
--           Tenant-scoped like every other table (TenantId NVARCHAR(100) NOT NULL,
--           filtered in the repository). Exactly one row per (TenantId, SettingKey),
--           enforced by UQ_TenantSettings_Tenant_Key.
--           Defaults live in application code (TenantSettingKeys), NOT in this table:
--           a tenant with no row must resolve to a sensible default and must never
--           fail, so new settings can be added later without inserting a row for
--           every existing tenant. There is deliberately NO seed here.
--           Idempotent: the table is guarded so the script can be re-run safely.
-- ============================================================================

-- ── TenantSettings table ────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[TenantSettings]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[TenantSettings]
    (
        [SettingId]     UNIQUEIDENTIFIER    NOT NULL
            CONSTRAINT [DF_TenantSettings_SettingId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]      NVARCHAR(100)       NOT NULL,
        [SettingKey]    NVARCHAR(100)       NOT NULL,
        [SettingValue]  NVARCHAR(MAX)       NOT NULL,
        [Description]   NVARCHAR(500)       NULL,
        [UpdatedAt]     DATETIME2           NOT NULL
            CONSTRAINT [DF_TenantSettings_UpdatedAt] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_TenantSettings]
            PRIMARY KEY CLUSTERED ([SettingId] ASC),

        CONSTRAINT [UQ_TenantSettings_Tenant_Key]
            UNIQUE ([TenantId] ASC, [SettingKey] ASC)
    );
    PRINT 'Table [dbo].[TenantSettings] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[TenantSettings] already exists — skipped.';
END
GO

-- ── Seeding (DBA reference only — NOT executed) ─────────────────────────────
-- Defaults live in code, so no row is required. To OVERRIDE the credit-line mode
-- for a tenant to 'simple', the DBA runs (replacing the tenant slug):
--
--   INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description])
--   VALUES (N'SgrOneClickSandboxFB2', N'creditline.mode', N'simple',
--           N'Colapsa cupo/operación en la UI para entidades no-SGR.');
--
-- Omitting the row leaves the tenant on the 'sgr' default.
