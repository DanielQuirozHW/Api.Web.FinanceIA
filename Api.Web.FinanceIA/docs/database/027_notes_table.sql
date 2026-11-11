-- ============================================================================
-- Script  : 027_notes_table.sql
-- Purpose : Create [dbo].[Notes] — PyME (Entity) level internal analyst notes.
--           Free-text internal commentary that lives in no integrated data
--           source (e.g. "owner is retiring", "expecting a large invoice in Q3").
--           Append-only: rows are inserted, never updated or deleted.
-- Date    : 2026-07-17
-- Author  : Claude (assisted)
-- Notes   : Tenant-scoped like every other table (TenantId NVARCHAR(100) NOT NULL,
--           filtered in the repository). Modeled after [dbo].[ChatMessages]
--           (append-only child of an aggregate) and [dbo].[ExportLogs] (table
--           creation idiom).
--           AnalysisId is reserved for future analysis-level notes and is always
--           NULL for now; the PyME-level repository query filters AnalysisId IS
--           NULL to keep the two scopes separate. No FK on AnalysisId yet.
--           AuthorName is a denormalized snapshot of the author's display name at
--           write time, so historic notes keep the name as it was even if the
--           user later renames themselves.
--           Idempotent: table, FK constraints and index are each guarded so the
--           script can be re-run safely.
-- ============================================================================

-- ── Notes table ─────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notes]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[Notes]
    (
        [NoteId]        BIGINT           IDENTITY(1,1)  NOT NULL,
        [TenantId]      NVARCHAR(100)                   NOT NULL,
        [EntityId]      UNIQUEIDENTIFIER                NOT NULL,
        [AnalysisId]    UNIQUEIDENTIFIER                NULL,       -- reserved for future analysis-level notes; always NULL for now
        [Content]       NVARCHAR(MAX)                   NOT NULL,
        [AuthorUserId]  UNIQUEIDENTIFIER                NOT NULL,
        [AuthorName]    NVARCHAR(200)                   NOT NULL,   -- denormalized snapshot of author display name at write time
        [CreatedAt]     DATETIME2                       NOT NULL
            CONSTRAINT [DF_Notes_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_Notes]
            PRIMARY KEY CLUSTERED ([NoteId] ASC)
    );
    PRINT 'Table [dbo].[Notes] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[Notes] already exists — skipped.';
END
GO

-- ── Foreign keys ──────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Notes_Entities]')
)
BEGIN
    ALTER TABLE [dbo].[Notes]
        ADD CONSTRAINT [FK_Notes_Entities]
            FOREIGN KEY ([EntityId]) REFERENCES [dbo].[Entities] ([EntityId]);
    PRINT 'Foreign key [FK_Notes_Entities] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Notes_Entities] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Notes_Users]')
)
BEGIN
    ALTER TABLE [dbo].[Notes]
        ADD CONSTRAINT [FK_Notes_Users]
            FOREIGN KEY ([AuthorUserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_Notes_Users] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Notes_Users] already exists — skipped.';
END
GO

-- ── Indexes ───────────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notes]')
      AND  name      = N'IX_Notes_TenantId_EntityId_NoteId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Notes_TenantId_EntityId_NoteId]
        ON [dbo].[Notes] ([TenantId] ASC, [EntityId] ASC, [NoteId] ASC);
    PRINT 'Index [IX_Notes_TenantId_EntityId_NoteId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Notes_TenantId_EntityId_NoteId] already exists — skipped.';
END
GO
