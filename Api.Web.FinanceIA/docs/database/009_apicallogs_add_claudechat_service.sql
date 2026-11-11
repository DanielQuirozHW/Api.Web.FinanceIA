-- Migration : 009_apicallogs_add_claudechat_service
-- Created   : 2026-05-08
-- Purpose   : Extend CK_ApiCallLogs_Service constraint to allow the 'ClaudeChat'
--             service value used by the new conversational chat endpoint
--             (POST /api/analyses/{resultId}/chat).
-- Executed by DBA — do NOT run dotnet ef migrations add

-- Drop the existing constraint and recreate it with the new allowed value.
ALTER TABLE [dbo].[ApiCallLogs]
    DROP CONSTRAINT [CK_ApiCallLogs_Service];
GO

ALTER TABLE [dbo].[ApiCallLogs]
    ADD CONSTRAINT [CK_ApiCallLogs_Service]
    CHECK ([Service] IN ('BYMA', 'Dynamics', 'BCRA', 'Claude', 'ClaudeChat'));
GO
