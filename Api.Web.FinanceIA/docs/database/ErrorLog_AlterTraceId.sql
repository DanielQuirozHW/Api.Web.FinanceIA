-- ============================================================
-- Project : Api.Web.ServiciosFinancieros
-- Script  : ErrorLog_AlterTraceId
-- Purpose : Add TraceId column to ErrorLogs table for
--           correlation between Application Insights and
--           SQL Server error logs
-- Author  : DBA / Dev Team
-- Date    : 2025-01-01
-- ============================================================

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'[dbo].[ErrorLogs]')
        AND name = 'TraceId'
    )
    BEGIN
        ALTER TABLE [dbo].[ErrorLogs]
        ADD [TraceId] NVARCHAR(100) NULL;

        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'Correlation ID linking this error to 
                       Application Insights telemetry and 
                       the full request trace.',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'ErrorLogs',
            @level2type = N'COLUMN', @level2name = N'TraceId';

        CREATE NONCLUSTERED INDEX [IX_ErrorLogs_TraceId]
            ON [dbo].[ErrorLogs] ([TraceId] ASC)
            WHERE [TraceId] IS NOT NULL;

        PRINT 'TraceId column added to ErrorLogs successfully.';
    END
    ELSE
    BEGIN
        PRINT 'TraceId column already exists in ErrorLogs. Skipping.';
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    ROLLBACK TRANSACTION;

    DECLARE @errorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @errorLine    INT            = ERROR_LINE();

    PRINT 'Error adding TraceId column at line ' +
          CAST(@errorLine AS NVARCHAR(10)) +
          ': ' + @errorMessage;

    THROW;
END CATCH;