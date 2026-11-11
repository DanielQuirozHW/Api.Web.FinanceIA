-- ============================================================================
-- Script  : 055_chatmessages_add_attachmentmarkdown.sql
-- Purpose : Add [AttachmentMarkdown] to [dbo].[ChatMessages] so the spreadsheet
--           an analyst attaches to a chat turn survives the request and can be
--           read back when they reopen the conversation.
-- Date    : 2026-09-03
-- Author  : Claude (assisted)
-- Notes   : WHAT IS MISSING TODAY.
--           A cash-flow .xlsx the analyst uploads to
--           POST /api/analyses/{resultId}/chat/attachment comes back as Markdown
--           tables, is posted straight into the NEXT chat turn as a second text
--           block, and dies with that request. Nothing keeps it. Reopening the
--           conversation a day later shows the question with no visible subject —
--           "¿Cierra el flujo de fondos?" against nothing — and a follow-up means
--           uploading the same file again.
--
--           This column stores it. It is step 1 of the work: PERSISTENCE ONLY.
--           Nothing reads the column back into a prompt, and this delivery does
--           not change one token of what is sent to Claude.
--
--           WHY IT LIVES ON THE USER TURN. That is the turn that carried the
--           upload: the analyst attached the file to their own question, and this
--           column records what they attached. The assistant turn's
--           [HadEphemeralAttachment] (script 053) is a different column answering
--           a different question — it is a BILLING marker, one bit saying that
--           row's [InputTokens] counts a spreadsheet no later turn will replay, so
--           the per-conversation context gate must skip it rather than measure the
--           conversation by it. 053 is untouched here, and deliberately so: one
--           column records what the analyst sent, the other records what the meter
--           saw. Conflating them would make either meaningless. A row with
--           Role = 'user' therefore carries the Markdown and never the flag, and a
--           row with Role = 'assistant' carries the flag and never the Markdown.
--
--           WHY NOT [Content]. [Content] is replayed verbatim as history on every
--           later turn of the conversation. Writing the Markdown there would
--           re-send the whole spreadsheet for the rest of the conversation — the
--           exact behaviour the ephemeral design exists to prevent, and the
--           property AnalysisControllerChatAttachmentTurnTests exists to protect.
--           A separate column keeps the attachment readable without making it
--           history: nothing rebuilds a prompt from this column.
--
--           NULLABLE, NO DEFAULT, NO BACKFILL — the opposite call from 053, for
--           the opposite reason. 053 modelled a decision with two states and no
--           third, so a NOT NULL BIT defaulting to 0 said something true about
--           every pre-existing row. Here the value is the text itself: a turn that
--           carried nothing has no Markdown, and NULL is the honest way to say so.
--           There is nothing to backfill either — the text of every attachment
--           sent before this deploy exists nowhere to be recovered, so the feature
--           is FORWARD-ONLY. Past turns stay blank; the analyst re-uploads once,
--           and from then on it is kept. An empty string would be a third state
--           the application never writes, which is why the column takes NULL.
--
--           NVARCHAR(MAX) matches [Content] and the vendor JSON columns elsewhere
--           in this schema. AnalysisController.CapAttachmentMarkdown bounds what
--           can land here at 200.000 characters (~400 KB of UTF-16) plus a short
--           truncation note, and the /chat endpoint's own RequestSizeLimit is 1 MB,
--           so a single row cannot exceed that regardless of what a caller posts.
--           GROWTH: only turns that actually carried a file are affected, and the
--           table already holds nvarchar(max) transcripts, so the shape of this
--           table's growth does not change — its rate does, on the minority of
--           turns that attach a spreadsheet.
--
--           NO INDEX. The column is never a predicate. It is only ever read as
--           part of one conversation's message list, already served by
--           IX_ChatMessages_ConversationId_ChatMessageId, and an index on an
--           nvarchar(max) column is not possible in any case.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. The column is mapped in ChatMessageConfiguration, and the
--           chat repository reads the FULL entity (ChatRepository.GetMessagesAsync
--           is a plain ToListAsync, no projection) and INSERTs the full entity
--           (AddMessageAsync). EF Core therefore names this column in every SELECT
--           and every INSERT against [dbo].[ChatMessages]. Against a database
--           without it, EVERY chat turn and the whole conversation-history endpoint
--           fail with "Invalid column name 'AttachmentMarkdown'", and no
--           application-level try/catch can protect against it. This is the same
--           deploy-order constraint as script 053, for the same reason, on the same
--           table. (Billing is unaffected: the monthly chat-token read in
--           UsageRepository is a projection over InputTokens/OutputTokens.) Restart
--           the App Service after deploying so EF's model cache is rebuilt.
--
--           The Portal does not need to ship first. The conversation-history
--           payload gains one additive boolean, `hasAttachment` — a marker, not the
--           Markdown, because one attachment can be 200.000 characters and that
--           endpoint returns every turn of the conversation. A Portal that does not
--           know the field ignores it; no enum value changes.
--
--           Idempotent: guarded on sys.columns, so the script can be re-run safely.
--           Re-running never rewrites an attachment already stored.
-- ============================================================================

-- ── ChatMessages.AttachmentMarkdown ─────────────────────────────────────────
-- NULL = this turn carried no attachment. Every existing row reads NULL after the
-- ALTER with no data migration and no separate UPDATE, because there is no text
-- anywhere to migrate — see the forward-only note above.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[ChatMessages]')
      AND  name      = N'AttachmentMarkdown'
)
BEGIN
    ALTER TABLE [dbo].[ChatMessages]
        ADD [AttachmentMarkdown] NVARCHAR(MAX) NULL;
    PRINT 'Column AttachmentMarkdown added to [dbo].[ChatMessages].';
END
ELSE
BEGIN
    PRINT 'Column AttachmentMarkdown already exists on [dbo].[ChatMessages] — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
-- Every existing row must read NULL after the ALTER:
--
--   SELECT COUNT(*) AS TurnsWithAttachment
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentMarkdown] IS NOT NULL;
--
-- After the deploy, only USER turns ever carry one — the analyst's turn is the one
-- the upload arrived on. A row with Role <> 'user' and a non-NULL value would mean
-- the application wrote it in the wrong place:
--
--   SELECT COUNT(*)
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentMarkdown] IS NOT NULL
--     AND  [Role] <> N'user';
--
-- And the two attachment columns must stay on opposite turns — 053's flag on the
-- assistant row, this Markdown on the user row. Neither should ever appear on the
-- same row as the other:
--
--   SELECT COUNT(*)
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentMarkdown] IS NOT NULL
--     AND  [HadEphemeralAttachment] = 1;
--
-- Size, once the feature has been in use — the column is capped per row at 200.000
-- characters but nothing caps how many rows carry one:
--
--   SELECT COUNT(*)                        AS StoredAttachments,
--          MAX(DATALENGTH([AttachmentMarkdown])) / 1024 AS LargestKB,
--          SUM(DATALENGTH([AttachmentMarkdown])) / 1048576 AS TotalMB
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentMarkdown] IS NOT NULL;
--
-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- No default constraint to drop first — the column takes none:
--
--   ALTER TABLE [dbo].[ChatMessages] DROP COLUMN [AttachmentMarkdown];
--
-- Nothing else references the column and no other table points at it, but this must
-- be PRECEDED by rolling the API back to a build without the mapping — otherwise the
-- deploy-order failure described above is exactly what happens, on every chat turn.
-- What is lost here is real and unrecoverable, unlike 053's flag: the attachments
-- themselves. Every spreadsheet an analyst attached since the deploy goes with the
-- column, and no other copy of that text exists anywhere in the system. Export it
-- first if any of it still matters:
--
--   SELECT [ChatMessageId], [ConversationId], [TenantId], [CreatedAt],
--          [AttachmentMarkdown]
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentMarkdown] IS NOT NULL;
