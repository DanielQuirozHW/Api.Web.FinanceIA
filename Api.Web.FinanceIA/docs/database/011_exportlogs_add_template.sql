-- ============================================================================
-- Script  : 011_exportlogs_add_template.sql
-- Purpose : Add nullable Template column to ExportLogs for template-based exports
-- Date    : 2026-06-07
-- Author  : Claude (assisted)
-- Notes   : Nullable, no default, no constraint. Old rows stay NULL.
-- ============================================================================

ALTER TABLE [dbo].[ExportLogs]
    ADD [Template] NVARCHAR(20) NULL;
GO
