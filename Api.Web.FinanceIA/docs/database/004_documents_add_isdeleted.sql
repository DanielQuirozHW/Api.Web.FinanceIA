-- Migration : 004_documents_add_isdeleted
-- Created   : 2026-05-04
-- Purpose   : Add soft-delete columns to [dbo].[Documents]
-- Executed by DBA — do NOT run dotnet ef migrations add

ALTER TABLE [dbo].[Documents]
    ADD [IsDeleted]  BIT          NOT NULL CONSTRAINT [DF_Documents_IsDeleted]  DEFAULT (0),
        [DeletedAt]  DATETIME2    NULL;
GO

-- Index to speed up queries that filter out deleted rows
CREATE NONCLUSTERED INDEX [IX_Documents_TenantId_IsDeleted]
    ON [dbo].[Documents] ([TenantId], [IsDeleted]);
GO
