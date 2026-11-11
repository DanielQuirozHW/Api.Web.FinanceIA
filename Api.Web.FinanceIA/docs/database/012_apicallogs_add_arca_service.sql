-- Migration : 012_apicallogs_add_arca_service
-- Created   : 2026-05-12
-- Purpose   : Extend CK_ApiCallLogs_Service constraint to allow the 'ARCA'
--             service value used by the new ARCA Padrón A13 lookup endpoints
--             (POST /api/entities/lookup-arca, POST /api/entities/lookup-arca/by-dni).
-- Executed by DBA — do NOT run dotnet ef migrations add

-- Drop the existing constraint and recreate it with the new allowed value.
ALTER TABLE [dbo].[ApiCallLogs]
    DROP CONSTRAINT [CK_ApiCallLogs_Service];
GO

ALTER TABLE [dbo].[ApiCallLogs]
    ADD CONSTRAINT [CK_ApiCallLogs_Service]
    CHECK ([Service] IN ('BYMA', 'Dynamics', 'BCRA', 'Claude', 'ClaudeChat', 'ARCA'));
GO
