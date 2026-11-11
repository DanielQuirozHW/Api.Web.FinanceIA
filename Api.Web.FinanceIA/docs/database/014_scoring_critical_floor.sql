-- Migration : 014_scoring_critical_floor
-- Created   : 2026-06-04
-- Purpose   : Seed critical-ratio floor parameters into [dbo].[ScoringConfig].
--             When any ratio's partial score reaches the trigger threshold,
--             the final composite score cannot drop below the configured floor.
--             Valores por defecto pendientes de calibración por el equipo de riesgo.
-- Executed by DBA — do NOT run dotnet ef migrations add

DECLARE @t NVARCHAR(100) = N'SgrOneClickSandboxFB2';

-- Critical-ratio floor (Feature 5a)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'critical_floor_trigger_threshold',  85.0000, N'Umbral de score parcial a partir del cual un ratio se considera crítico'),
(@t, 'critical_floor_liquidez_corriente', 60.0000, N'Piso mínimo del score final cuando Liquidez Corriente es crítica'),
(@t, 'critical_floor_prueba_acida',       60.0000, N'Piso mínimo del score final cuando Prueba Ácida es crítica'),
(@t, 'critical_floor_endeudamiento',      65.0000, N'Piso mínimo del score final cuando Endeudamiento es crítico'),
(@t, 'critical_floor_leverage',           65.0000, N'Piso mínimo del score final cuando Leverage es crítico'),
(@t, 'critical_floor_cobertura_intereses',70.0000, N'Piso mínimo del score final cuando Cobertura de Intereses es crítica'),
(@t, 'critical_floor_roe',               55.0000, N'Piso mínimo del score final cuando ROE es crítico'),
(@t, 'critical_floor_roa',               55.0000, N'Piso mínimo del score final cuando ROA es crítico'),
(@t, 'critical_floor_margen_neto',        55.0000, N'Piso mínimo del score final cuando Margen Neto es crítico');
GO
