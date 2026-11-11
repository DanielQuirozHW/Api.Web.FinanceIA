-- ============================================================================
-- Script  : 038_alert_rule_catalogue.sql
-- Purpose : Create [dbo].[AlertRuleCatalogue] (per-tenant deterministic alert
--           rules) and add [dbo].[AnalysisResults].[CatalogueAlerts]
-- Date    : 2026-08-16
-- Author  : Claude (assisted)
-- Notes   : Alerts today are authored entirely by the model. Measuring six runs of
--           the SAME balance-sheet PDF (prompt v2.16) showed extraction is
--           deterministic — all 16 balance_check fields and all 8 computed ratios
--           were byte-identical every run — while the alert layer is not: the same
--           finding was emitted under different `code` strings, and four findings
--           oscillated between the alerts and anomalies channels, producing either
--           a 7-alert or an 11-alert report for one document.
--
--           This table is the closed set the model's free-form `code` never had.
--           A deterministic rule engine evaluates every ENABLED row for the tenant
--           against the already-stable BalanceJson, so the same input always yields
--           the same alerts.
--
--           EVERY SEEDED ROW IS DISABLED (Enabled = 0). Nothing fires until an
--           analyst enables it. The seeded ThresholdValue figures are round
--           synthetic placeholders chosen to be obviously provisional — they are
--           NOT recommendations. The analyst sets the real values.
--
--           AUDIT COLUMNS ARE NOT OPTIONAL. A threshold change alters which alerts
--           fire and therefore what the SGR decides on, so CreatedAt / UpdatedAt /
--           UpdatedBy record who moved a number and when.
--
--           Severity is stored LOWERCASE (low|medium|high|critical) to match the
--           severity vocabulary the model already emits in an alert's "level" key,
--           and is pinned by CK_AlertRuleCatalogue_Severity. Note [dbo].[Alerts]
--           uses TitleCase for its own Severity column; the two are bridged in code
--           by AnalysisController.NormalizeSeverity, not here.
--
--           Code is NVARCHAR(50) and DisplayNameEs is NVARCHAR(300) so both fit
--           [dbo].[Alerts].[AlertType] and [dbo].[Alerts].[Title] unchanged, should
--           catalogue alerts later be fanned out to the alerts inbox (deliberately
--           NOT done in this change).
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE. The new
--           [CatalogueAlerts] column is mapped in AnalysisResultConfiguration, so
--           EF Core emits it in every SELECT and INSERT against AnalysisResults.
--           Deploying the code against a database without this column takes
--           analysis creation AND retrieval down. The new table degrades safely
--           (its read is fault-isolated); the new column does not.
--
--           Idempotent: table, index, column, check constraint and every seed row
--           are guarded, so the script can be re-run safely.
-- ============================================================================

-- ── AlertRuleCatalogue table ────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[AlertRuleCatalogue]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[AlertRuleCatalogue]
    (
        [RuleId]                UNIQUEIDENTIFIER    NOT NULL
            CONSTRAINT [DF_AlertRuleCatalogue_RuleId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]              NVARCHAR(100)       NOT NULL,
        [Code]                  NVARCHAR(50)        NOT NULL,
        [Enabled]               BIT                 NOT NULL
            CONSTRAINT [DF_AlertRuleCatalogue_Enabled] DEFAULT (0),
        [Severity]              NVARCHAR(20)        NOT NULL,
        [ThresholdValue]        DECIMAL(10,4)       NULL,
        [DisplayNameEs]         NVARCHAR(300)       NOT NULL,
        [DescriptionTemplateEs] NVARCHAR(1000)      NOT NULL,
        [CreatedAt]             DATETIME2           NOT NULL
            CONSTRAINT [DF_AlertRuleCatalogue_CreatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAt]             DATETIME2           NOT NULL
            CONSTRAINT [DF_AlertRuleCatalogue_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedBy]             UNIQUEIDENTIFIER    NULL,

        CONSTRAINT [PK_AlertRuleCatalogue]
            PRIMARY KEY CLUSTERED ([RuleId] ASC),

        CONSTRAINT [UQ_AlertRuleCatalogue_Tenant_Code]
            UNIQUE ([TenantId] ASC, [Code] ASC),

        CONSTRAINT [CK_AlertRuleCatalogue_Severity]
            CHECK ([Severity] IN (N'low', N'medium', N'high', N'critical'))
    );
    PRINT 'Table [dbo].[AlertRuleCatalogue] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[AlertRuleCatalogue] already exists — skipped.';
END
GO

-- ── Engine lookup index ─────────────────────────────────────────────────────
-- Matches the rule engine's only query: enabled rules for one tenant.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AlertRuleCatalogue]')
      AND  name      = N'IX_AlertRuleCatalogue_TenantId_Enabled'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_AlertRuleCatalogue_TenantId_Enabled]
        ON [dbo].[AlertRuleCatalogue] ([TenantId] ASC, [Enabled] ASC);
    PRINT 'Index [IX_AlertRuleCatalogue_TenantId_Enabled] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_AlertRuleCatalogue_TenantId_Enabled] already exists — skipped.';
END
GO

-- ── AnalysisResults.CatalogueAlerts column ──────────────────────────────────
-- Second channel. [RiskAlerts] keeps its present meaning and content unchanged
-- (the model's findings, surfaced as `observations`); this column holds the
-- deterministic, catalogue-driven alerts. NULL for every analysis persisted
-- before this change — deliberately NOT backfilled, because the catalogue rules
-- ship disabled and no historical verdict can be reconstructed honestly.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'CatalogueAlerts'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [CatalogueAlerts] NVARCHAR(MAX) NULL;
    PRINT 'Column CatalogueAlerts added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column CatalogueAlerts already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

-- JSON shape guard, mirroring the existing CK_AnalysisResults_RiskAlerts.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  object_id = OBJECT_ID(N'[dbo].[CK_AnalysisResults_CatalogueAlerts]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisResults_CatalogueAlerts]
        CHECK ([CatalogueAlerts] IS NULL OR ISJSON([CatalogueAlerts]) = 1);
    PRINT 'Check constraint [CK_AnalysisResults_CatalogueAlerts] added.';
END
ELSE
BEGIN
    PRINT 'Check constraint [CK_AnalysisResults_CatalogueAlerts] already exists — skipped.';
END
GO

-- ── Seed: the five deterministic rules, ALL DISABLED ────────────────────────
-- Guarded per row so re-running never duplicates and never silently resets an
-- analyst's Enabled flag or ThresholdValue back to the placeholder.

DECLARE @t NVARCHAR(100) = N'SgrOneClickSandboxFB2';

-- No threshold. Fires when the entire liability side is current.
IF NOT EXISTS (SELECT 1 FROM [dbo].[AlertRuleCatalogue] WHERE TenantId = @t AND Code = N'PASIVO_100_CORRIENTE')
INSERT INTO [dbo].[AlertRuleCatalogue] (TenantId, Code, Enabled, Severity, ThresholdValue, DisplayNameEs, DescriptionTemplateEs) VALUES
(@t, N'PASIVO_100_CORRIENTE', 0, N'high', NULL,
 N'Pasivo íntegramente corriente',
 N'La totalidad del pasivo es corriente: no se informa pasivo no corriente. El vencimiento de las obligaciones se concentra dentro de los doce meses.');

-- Threshold = proporción de bienes de cambio sobre el activo corriente (0.5000 = 50 %).
-- Valor sintético provisional: el analista define el umbral real.
IF NOT EXISTS (SELECT 1 FROM [dbo].[AlertRuleCatalogue] WHERE TenantId = @t AND Code = N'INVENTARIO_ELEVADO')
INSERT INTO [dbo].[AlertRuleCatalogue] (TenantId, Code, Enabled, Severity, ThresholdValue, DisplayNameEs, DescriptionTemplateEs) VALUES
(@t, N'INVENTARIO_ELEVADO', 0, N'medium', 0.5000,
 N'Participación elevada de bienes de cambio',
 N'Los bienes de cambio representan el {valor} % del activo corriente, por encima del umbral configurado de {umbral} %.');

-- No threshold. Fires when the income statement does not state financial expenses separately.
IF NOT EXISTS (SELECT 1 FROM [dbo].[AlertRuleCatalogue] WHERE TenantId = @t AND Code = N'COBERTURA_INTERESES_INDETERMINABLE')
INSERT INTO [dbo].[AlertRuleCatalogue] (TenantId, Code, Enabled, Severity, ThresholdValue, DisplayNameEs, DescriptionTemplateEs) VALUES
(@t, N'COBERTURA_INTERESES_INDETERMINABLE', 0, N'medium', NULL,
 N'Cobertura de intereses indeterminable',
 N'El estado de resultados no expone los gastos financieros por separado, por lo que la cobertura de intereses no puede calcularse.');

-- No threshold: the tolerance lives in CashFlowReconciliationService (0,5 % relativo
-- Y 1,00 absoluto, ambos deben superarse), which this rule delegates to.
IF NOT EXISTS (SELECT 1 FROM [dbo].[AlertRuleCatalogue] WHERE TenantId = @t AND Code = N'FLUJO_NO_CONCILIA')
INSERT INTO [dbo].[AlertRuleCatalogue] (TenantId, Code, Enabled, Severity, ThresholdValue, DisplayNameEs, DescriptionTemplateEs) VALUES
(@t, N'FLUJO_NO_CONCILIA', 0, N'high', NULL,
 N'El estado de flujo de efectivo no concilia',
 N'La suma de las secciones del estado de flujo de efectivo no coincide con la variación informada. Las cifras se muestran tal como fueron extraídas.');

-- No threshold. Fires only when a cash flow statement is present.
IF NOT EXISTS (SELECT 1 FROM [dbo].[AlertRuleCatalogue] WHERE TenantId = @t AND Code = N'FLUJO_FINANCIACION_CERO')
INSERT INTO [dbo].[AlertRuleCatalogue] (TenantId, Code, Enabled, Severity, ThresholdValue, DisplayNameEs, DescriptionTemplateEs) VALUES
(@t, N'FLUJO_FINANCIACION_CERO', 0, N'low', NULL,
 N'Actividades de financiación en cero',
 N'El estado de flujo de efectivo informa actividades de financiación por cero en el ejercicio.');

PRINT 'Seed rules ensured for tenant SgrOneClickSandboxFB2 (all DISABLED).';
GO
