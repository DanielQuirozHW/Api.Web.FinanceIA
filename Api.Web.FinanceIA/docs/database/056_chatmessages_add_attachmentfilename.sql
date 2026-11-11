-- ============================================================================
-- Script  : 056_chatmessages_add_attachmentfilename.sql
-- Purpose : Add [AttachmentFileName] to [dbo].[ChatMessages] so the analyst
--           can tell WHICH spreadsheet a turn carried, not merely that one was
--           attached.
-- Date    : 2026-09-03
-- Author  : Claude (assisted)
-- Notes   : WHAT IS MISSING TODAY.
--           Script 055 keeps the attachment's Markdown, and the
--           conversation-history endpoint reports a `hasAttachment` marker
--           against the turn that carried it. What it cannot report is the
--           file's NAME, because nothing stored it. The analyst reopens the
--           conversation and reads "este turno traía un adjunto" with no way to
--           know whether it was `flujo-2026-final.xlsx` or the draft they
--           replaced an hour later. On an analysis where three versions of the
--           same cash flow were tried, the marker is nearly useless without it.
--
--           This column stores it. Like 055 this delivery is PERSISTENCE AND
--           READ-BACK ONLY: nothing reads the column into a prompt, and not one
--           token of what is sent to Claude changes. The name never reaches
--           ClaudeService, BuildChatSystemPrompt or its hasAttachment flag.
--
--           WHY IT LIVES ON THE USER TURN, BESIDE 055. Same turn, same reason:
--           the analyst attached the file to their own question, and these two
--           columns together record what they attached — the content and the
--           label for it. 053's [HadEphemeralAttachment] remains on the
--           ASSISTANT turn and remains a billing marker; it is untouched here.
--
--           NULL EXACTLY WHEN [AttachmentMarkdown] IS NULL. This is an
--           invariant, not a coincidence, and it is enforced in code rather
--           than by a constraint: AnalysisController derives the stored name
--           from the CAPPED Markdown local, so a request carrying a file name
--           and no attachment stores NULL for both, and a request carrying an
--           attachment and no file name stores NULL here and the Markdown
--           there. The verification block below has the query that proves it.
--           No CHECK constraint is added: the pre-056 rows would all have to
--           satisfy it too (they do, both being NULL), but a constraint on this
--           table would have to be dropped and rebuilt by any later script
--           touching either column, for a rule the single writing code path
--           already guarantees.
--
--           NVARCHAR(255), NOT NVARCHAR(MAX). This is a label, not content —
--           the output of Path.GetFileName over what the browser sent, which
--           every mainstream filesystem already bounds well below 255. The
--           controller trims defensively to the same 255 with the existing
--           Truncate helper, so an oversized name is CUT rather than rejected:
--           refusing a turn over the length of a file name would dead-end an
--           analyst whose file the server had already accepted and parsed. That
--           is the same call 055 made for the Markdown itself.
--
--           NULLABLE, NO DEFAULT, NO BACKFILL — same reasoning as 055, for the
--           same reason. A turn that carried nothing has no file name, and NULL
--           is the honest way to say so. There is nothing to backfill: the name
--           of every attachment sent before this deploy was never stored on the
--           message. It exists only inside the free-text newValueJson of the
--           'Analysis.ChatAttachment.Parsed' audit row, which is not addressed
--           by message id and is not a source to migrate from. So the feature
--           is FORWARD-ONLY, exactly like 055: past turns stay blank, the
--           analyst re-uploads once, and from then on it is kept. An empty
--           string would be a third state the application never writes.
--
--           NO INDEX. The column is never a predicate. It is only ever read as
--           part of one conversation's message list, already served by
--           IX_ChatMessages_ConversationId_ChatMessageId.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant database. The column is mapped in ChatMessageConfiguration,
--           and the chat repository reads the FULL entity
--           (ChatRepository.GetMessagesAsync is a plain ToListAsync, no
--           projection; the new GetMessageByIdAsync is a FirstOrDefaultAsync
--           over the same entity) and INSERTs the full entity (AddMessageAsync).
--           EF Core therefore names this column in every SELECT and every
--           INSERT against [dbo].[ChatMessages]. Against a database without it,
--           EVERY chat turn, the whole conversation-history endpoint and the new
--           attachment endpoint fail with "Invalid column name
--           'AttachmentFileName'", and no application-level try/catch can
--           protect against it. This is the same deploy-order constraint as
--           scripts 053 and 055, for the same reason, on the same table.
--           (Billing is unaffected: the monthly chat-token read in
--           UsageRepository is a projection over InputTokens/OutputTokens.)
--           Restart the App Service after deploying so EF's model cache is
--           rebuilt.
--
--           The Portal does not need to ship first. The conversation-history
--           payload gains two additive fields — `attachmentFileName` (the name,
--           tens of bytes) and `messageId` — and a NEW endpoint serves the
--           Markdown on demand. A Portal that does not know the fields ignores
--           them; no enum value changes and no existing field changes shape.
--
--           Idempotent: guarded on sys.columns, so the script can be re-run
--           safely. Re-running never rewrites a name already stored.
-- ============================================================================

-- ── ChatMessages.AttachmentFileName ─────────────────────────────────────────
-- NULL = this turn carried no attachment. Every existing row reads NULL after
-- the ALTER with no data migration and no separate UPDATE, because the name was
-- never stored on the message — see the forward-only note above.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[ChatMessages]')
      AND  name      = N'AttachmentFileName'
)
BEGIN
    ALTER TABLE [dbo].[ChatMessages]
        ADD [AttachmentFileName] NVARCHAR(255) NULL;
    PRINT 'Column AttachmentFileName added to [dbo].[ChatMessages].';
END
ELSE
BEGIN
    PRINT 'Column AttachmentFileName already exists on [dbo].[ChatMessages] — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
-- Every existing row must read NULL after the ALTER:
--
--   SELECT COUNT(*) AS TurnsWithFileName
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentFileName] IS NOT NULL;
--
-- THE INVARIANT. Name and Markdown are written from the same capped local, so
-- after the deploy neither may appear without the other. BOTH queries must
-- return 0 — a non-zero result means the controller's two writes have drifted
-- apart and the analyst is being shown a label for content that is not there,
-- or content with no label:
--
--   SELECT COUNT(*) AS NameWithoutMarkdown
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentFileName] IS NOT NULL
--     AND  [AttachmentMarkdown] IS NULL;
--
--   SELECT COUNT(*) AS MarkdownWithoutName
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentMarkdown] IS NOT NULL
--     AND  [AttachmentFileName] IS NULL;
--
-- (The second is 0 for turns taken after this deploy only. Rows written between
-- the 055 and 056 deploys legitimately carry Markdown with no name — that is
-- the forward-only gap, not a fault. Bound it by CreatedAt when checking.)
--
-- Like 055's Markdown, only USER turns ever carry one:
--
--   SELECT COUNT(*)
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentFileName] IS NOT NULL
--     AND  [Role] <> N'user';
--
-- And it must never land on the same row as 053's assistant-side billing flag:
--
--   SELECT COUNT(*)
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentFileName] IS NOT NULL
--     AND  [HadEphemeralAttachment] = 1;
--
-- What the analysts are actually attaching, once the feature has been in use:
--
--   SELECT TOP (50) [AttachmentFileName], COUNT(*) AS Turns
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentFileName] IS NOT NULL
--   GROUP  BY [AttachmentFileName]
--   ORDER  BY Turns DESC;
--
-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- No default constraint to drop first — the column takes none:
--
--   ALTER TABLE [dbo].[ChatMessages] DROP COLUMN [AttachmentFileName];
--
-- Nothing else references the column and no other table points at it, but this
-- must be PRECEDED by rolling the API back to a build without the mapping —
-- otherwise the deploy-order failure described above is exactly what happens, on
-- every chat turn. What is lost is the labels only, not the attachments: 055's
-- [AttachmentMarkdown] is a separate column and survives this rollback intact,
-- so the analyst keeps the spreadsheets and loses only their names. Export them
-- first if that still matters:
--
--   SELECT [ChatMessageId], [ConversationId], [TenantId], [CreatedAt],
--          [AttachmentFileName]
--   FROM   [dbo].[ChatMessages]
--   WHERE  [AttachmentFileName] IS NOT NULL;
