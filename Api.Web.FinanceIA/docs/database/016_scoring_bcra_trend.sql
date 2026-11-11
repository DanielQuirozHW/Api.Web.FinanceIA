-- Migration : 016_scoring_bcra_trend
-- Created   : 2026-06-04
-- Purpose   : Seed 5 BCRA trend config keys for the scoring engine (Feature 5c).
--             No ALTER TABLE needed — history data lives inside BcraDataJson.
-- Executed by DBA — do NOT run dotnet ef migrations add

DECLARE @t NVARCHAR(100) = N'SgrOneClickSandboxFB2';

-- BCRA trend parameters (Feature 5c)
INSERT INTO [dbo].[ScoringConfig] (TenantId, ConfigKey, ConfigValue, Description) VALUES
(@t, 'bcra_trend_window_months',        6.0000, N'Ventana de meses para calcular la tendencia BCRA'),
(@t, 'bcra_trend_pts_per_step_improve', 5.0000, N'Puntos a reducir del score BCRA por escalón de mejora'),
(@t, 'bcra_trend_pts_per_step_worsen',  8.0000, N'Puntos a sumar al score BCRA por escalón de empeoramiento'),
(@t, 'bcra_trend_max_improve_adj',      20.0000, N'Máximo ajuste por mejora (puntos a descontar)'),
(@t, 'bcra_trend_max_worsen_adj',       30.0000, N'Máximo ajuste por empeoramiento (puntos a sumar)');
GO
