-- Migration : 007_scoring_config
-- Created   : 2026-05-08
-- Purpose   : Create [dbo].[ScoringConfig] table for parametrisable credit-scoring
--             engine and seed default values for SgrOneClickSandboxFB2 tenant.
-- Executed by DBA — do NOT run dotnet ef migrations add

CREATE TABLE [dbo].[ScoringConfig] (
    ConfigId        UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID() PRIMARY KEY,
    TenantId        NVARCHAR(100)    NOT NULL,
    ConfigKey       NVARCHAR(100)    NOT NULL,
    ConfigValue     DECIMAL(10,4)    NOT NULL,
    Description     NVARCHAR(500)    NULL,
    UpdatedAt       DATETIME2        NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_ScoringConfig_Tenant_Key UNIQUE (TenantId, ConfigKey)
);
GO

-- ── Default values for SgrOneClickSandboxFB2 ────────────────────────────────

DECLARE @t NVARCHAR(100) = N'SgrOneClickSandboxFB2';

-- Ratio weights (sum = 1.0)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'weight_liquidez_corriente',  0.2000, N'Peso del ratio Liquidez Corriente en el score de ratios'),
(@t, 'weight_prueba_acida',        0.1000, N'Peso del ratio Prueba Ácida en el score de ratios'),
(@t, 'weight_endeudamiento',       0.2000, N'Peso del ratio Endeudamiento en el score de ratios'),
(@t, 'weight_leverage',            0.1000, N'Peso del ratio Leverage en el score de ratios'),
(@t, 'weight_cobertura_intereses', 0.1500, N'Peso del ratio Cobertura de Intereses en el score de ratios'),
(@t, 'weight_roe',                 0.1000, N'Peso del ratio ROE en el score de ratios'),
(@t, 'weight_roa',                 0.0800, N'Peso del ratio ROA en el score de ratios'),
(@t, 'weight_margen_neto',         0.0700, N'Peso del ratio Margen Neto en el score de ratios');

-- Thresholds: liquidez_corriente (higher is better)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_liquidez_corriente_ok',     1.5000, N'Liquidez Corriente OK (score 0)'),
(@t, 'threshold_liquidez_corriente_warn',   1.0000, N'Liquidez Corriente Warning (score 50)'),
(@t, 'threshold_liquidez_corriente_danger', 0.7000, N'Liquidez Corriente Danger (score 100)');

-- Thresholds: prueba_acida (higher is better)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_prueba_acida_ok',     1.0000, N'Prueba Ácida OK (score 0)'),
(@t, 'threshold_prueba_acida_warn',   0.7000, N'Prueba Ácida Warning (score 50)'),
(@t, 'threshold_prueba_acida_danger', 0.5000, N'Prueba Ácida Danger (score 100)');

-- Thresholds: endeudamiento (lower is better)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_endeudamiento_ok',     0.5000, N'Endeudamiento OK — menor es mejor (score 0)'),
(@t, 'threshold_endeudamiento_warn',   0.7000, N'Endeudamiento Warning (score 50)'),
(@t, 'threshold_endeudamiento_danger', 0.8500, N'Endeudamiento Danger (score 100)');

-- Thresholds: leverage (lower is better)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_leverage_ok',     1.0000, N'Leverage OK — menor es mejor (score 0)'),
(@t, 'threshold_leverage_warn',   1.5000, N'Leverage Warning (score 50)'),
(@t, 'threshold_leverage_danger', 2.5000, N'Leverage Danger (score 100)');

-- Thresholds: cobertura_intereses (higher is better)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_cobertura_intereses_ok',     3.0000, N'Cobertura de Intereses OK (score 0)'),
(@t, 'threshold_cobertura_intereses_warn',   1.5000, N'Cobertura de Intereses Warning (score 50)'),
(@t, 'threshold_cobertura_intereses_danger', 1.0000, N'Cobertura de Intereses Danger (score 100)');

-- Thresholds: roe (higher is better, en %)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_roe_ok',     15.0000, N'ROE OK — mayor es mejor, en % (score 0)'),
(@t, 'threshold_roe_warn',    5.0000, N'ROE Warning (score 50)'),
(@t, 'threshold_roe_danger',  0.0000, N'ROE Danger (score 100)');

-- Thresholds: roa (higher is better, en %)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_roa_ok',     8.0000, N'ROA OK — mayor es mejor, en % (score 0)'),
(@t, 'threshold_roa_warn',   3.0000, N'ROA Warning (score 50)'),
(@t, 'threshold_roa_danger', 0.0000, N'ROA Danger (score 100)');

-- Thresholds: margen_neto (higher is better, en %)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'threshold_margen_neto_ok',     10.0000, N'Margen Neto OK — mayor es mejor, en % (score 0)'),
(@t, 'threshold_margen_neto_warn',    4.0000, N'Margen Neto Warning (score 50)'),
(@t, 'threshold_margen_neto_danger',  0.0000, N'Margen Neto Danger (score 100)');

-- Component weights (sum = 1.0)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'weight_component_ratios', 0.5000, N'Peso del componente Ratios Financieros en el score final'),
(@t, 'weight_component_bcra',   0.3500, N'Peso del componente BCRA en el score final'),
(@t, 'weight_component_macro',  0.1500, N'Peso del componente Macro en el score final');

-- Final score thresholds (semáforo)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'score_threshold_low',    35.0000, N'Score <= 35 → Verde (Low)'),
(@t, 'score_threshold_medium', 60.0000, N'Score 36-60 → Amarillo (Medium)'),
(@t, 'score_threshold_high',   80.0000, N'Score 61-80 → Rojo (High); > 80 → Critical');

-- Macro penalization
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'macro_inflacion_penalizacion_threshold', 10.0000, N'Umbral de inflación mensual (%) a partir del cual se aplica penalización'),
(@t, 'macro_penalizacion_puntos',               5.0000, N'Puntos a sumar al score macro cuando inflación supera el umbral');
GO
