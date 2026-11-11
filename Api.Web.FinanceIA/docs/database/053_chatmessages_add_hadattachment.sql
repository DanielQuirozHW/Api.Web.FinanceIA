-- ============================================================================
-- Script  : 053_chatmessages_add_hadattachment.sql
-- Purpose : Add [HadEphemeralAttachment] to [dbo].[ChatMessages] so the
--           per-conversation context gate can tell which assistant turns carry
--           an input_tokens figure it must not measure.
-- Date    : 2026-09-02
-- Author  : Claude (assisted)
-- Notes   : WHY THIS COLUMN EXISTS AT ALL.
--           The context gate added by script 051 is REACTIVE and free: it reads
--           [InputTokens] off the most recent assistant turn of the conversation
--           and compares it to the tenant plan's [ChatContextBudgetTokens]. That
--           figure is Anthropic's count for the WHOLE request — and the chat
--           attachment is part of the request.
--
--           The attachment reaches the model ONCE. A cash-flow .xlsx the analyst
--           uploads to POST /api/analyses/{resultId}/chat/attachment rides exactly
--           ONE turn as a second text block. It is never written to [Content] and
--           never replayed as history. (Script 055 later added
--           [AttachmentMarkdown] on the USER turn so the analyst can read back what
--           they attached — storage, not history: nothing rebuilds a prompt from it,
--           and it does not change anything this script says. The two columns sit on
--           different turns and answer different questions; see 055's header.) The
--           attachment's tokens still reach [InputTokens] on the assistant turn
--           without reaching the transcript, which is the whole problem here. The
--           cap is 200.000 characters
--           (~67k tokens) against a MINIMUM budget of 40.000, so one attachment
--           turn can persist an [InputTokens] several times larger than the
--           transcript that will actually be replayed on the next turn — and the
--           gate, reading that number, pins the conversation above budget for good.
--
--           Nothing already in the schema can tell the two apart. [InputTokens] is
--           one number for the whole request and cannot be decomposed after the
--           fact; [Content] holds the analyst's typed message only, precisely
--           because the attachment must never come back as history; and
--           [dbo].[ApiCallLogs] records the same conflated total. Hence a marker
--           column: not the attachment's size, not a corrected count — one bit
--           saying "this turn's number includes something that will never be
--           replayed, so do not measure it".
--
--           WHAT THE APPLICATION DOES WITH IT. AnalysisController writes 1 on the
--           assistant turn when that turn actually carried a non-blank attachment,
--           and LastAssistantInputTokens SKIPS such rows, scanning back to the most
--           recent CLEAN assistant turn — reusing the same tolerance it already had
--           for a row with no count at all. That deliberately UNDER-reports when the
--           last clean turn is old, which is the safe direction and the one that
--           helper already documents: it can let a turn through that a complete
--           measurement would have refused, never the reverse. A conversation whose
--           only assistant turn carried an attachment measures as null and is simply
--           not gated, exactly like a brand-new one.
--
--           NOT NULL WITH DEFAULT (0), so every existing row backfills in the ALTER
--           itself with no data migration and no separate UPDATE. Zero is the honest
--           value for them: a turn for which we never recorded an attachment is
--           treated as carrying none, which is precisely today's behaviour. Note the
--           consequence — this fix is FORWARD-ONLY. A conversation that took an
--           attachment turn BEFORE this deploy keeps its inflated measurement and
--           stays pinned; the remedy for that one is the remedy the 429 already
--           offers, a new conversation. Nothing here rewrites history, and nothing
--           should: the true history-only count for a past turn does not exist
--           anywhere to be recovered.
--
--           The default constraint is NAMED (DF_ChatMessages_HadEphemeralAttachment)
--           rather than system-generated so a future script can drop it by name.
--           BIT rather than a nullable BIT: there is no third state. Either the turn
--           carried an attachment or it did not, and "unknown" for a pre-053 row is
--           the same decision as "no" — see above.
--
--           NO INDEX. The column is only ever read as part of the conversation's own
--           message list, which is already served by
--           IX_ChatMessages_ConversationId_ChatMessageId; it is never a predicate.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. The column is mapped in ChatMessageConfiguration, and the chat
--           repository reads the FULL entity (ChatRepository.GetMessagesAsync is a
--           plain ToListAsync, no projection) and INSERTs the full entity
--           (AddMessageAsync). EF Core therefore names this column in every SELECT
--           and every INSERT against [dbo].[ChatMessages]. Against a database without
--           it, EVERY chat turn and the whole conversation-history endpoint fail with
--           "Invalid column name 'HadEphemeralAttachment'", and no application-level
--           try/catch can protect against it. (The monthly chat-token read in
--           UsageRepository is a projection over InputTokens/OutputTokens and is NOT
--           affected — it is the chat itself that breaks, not billing.) Restart the
--           App Service after deploying so EF's model cache is rebuilt.
--
--           The Portal does not need to ship first: this adds no field to any
--           response payload and no new enum value.
--
--           Idempotent: guarded on sys.columns, so the script can be re-run safely.
--           Re-running never rewrites a flag already recorded.
-- ============================================================================

-- ── ChatMessages.HadEphemeralAttachment ─────────────────────────────────────
-- NOT NULL with a named default constraint so existing rows backfill to 0 in the
-- ALTER itself. 0 = this turn carried no ephemeral attachment, so its InputTokens
-- measures the replayable transcript and the context gate may read it.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[ChatMessages]')
      AND  name      = N'HadEphemeralAttachment'
)
BEGIN
    ALTER TABLE [dbo].[ChatMessages]
        ADD [HadEphemeralAttachment] BIT NOT NULL
            CONSTRAINT [DF_ChatMessages_HadEphemeralAttachment] DEFAULT (0);
    PRINT 'Column HadEphemeralAttachment added to [dbo].[ChatMessages].';
END
ELSE
BEGIN
    PRINT 'Column HadEphemeralAttachment already exists on [dbo].[ChatMessages] — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
-- Every existing row must read 0 after the ALTER, and no row may be NULL:
--
--   SELECT [HadEphemeralAttachment], COUNT(*) AS Turns
--   FROM   [dbo].[ChatMessages]
--   GROUP  BY [HadEphemeralAttachment];
--
-- After the deploy, only assistant turns are ever flagged — the user turn stores
-- nothing the GATE reads. (Since script 055 it does store the attachment's Markdown,
-- in a different column that no measurement looks at.) A row with Role = 'user' and
-- HadEphemeralAttachment = 1 would mean the application wrote the flag in the wrong
-- place:
--
--   SELECT COUNT(*)
--   FROM   [dbo].[ChatMessages]
--   WHERE  [HadEphemeralAttachment] = 1
--     AND  [Role] <> N'assistant';
--
-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Drop the named constraint first, then the column:
--
--   ALTER TABLE [dbo].[ChatMessages]
--       DROP CONSTRAINT [DF_ChatMessages_HadEphemeralAttachment];
--   ALTER TABLE [dbo].[ChatMessages] DROP COLUMN [HadEphemeralAttachment];
--
-- Nothing else references the column and no other table points at it, but this must
-- be PRECEDED by rolling the API back to a build without the mapping — otherwise the
-- deploy-order failure described above is exactly what happens, on every chat turn.
-- What is lost is the flag itself: conversations measured after the rollback go back
-- to reading an attachment turn's inflated count, which is the behaviour this script
-- exists to correct, not a data loss.
