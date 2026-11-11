# 055 — The chat attachment stops dying with the request

## What changed and why

A cash-flow `.xlsx` the analyst uploads to `POST /api/analyses/{resultId}/chat/attachment` is parsed
into Markdown tables, handed back to the caller, and posted straight into the next
`POST /api/analyses/{resultId}/chat` turn as `ChatRequest.AttachmentMarkdown`. It rides that one
request as a second text block and then it is gone. `AnalysisController.ChatAsync` wrote the user
turn as `Content = request.Message` and nothing else, so the file was stored nowhere.

That was the right call for the model — the attachment must not become history, see below — but it
made the *analyst's* copy collateral damage. Reopening the conversation a day later shows a question
with no visible subject: "¿Cierra el flujo de fondos?" against nothing. Asking a follow-up means
finding and uploading the same file again.

**055 adds the column that keeps it.** This is step 1 of the work and it does exactly one thing:
persistence. Nothing reads the column back into a prompt, `BuildChatSystemPrompt` is untouched, the
context gate and the billing path are untouched, and the request sent to Claude is byte-for-byte
what it was. Surfacing the stored Markdown to the analyst and letting them re-use it without
re-uploading is step 2.

### Why a new column rather than `[Content]`

`[Content]` is replayed **verbatim** as history on every later turn of the conversation —
`ChatAsync` rebuilds the prompt from `ChatRepository.GetMessagesAsync`. Writing the Markdown there
would re-send the whole spreadsheet for the rest of the conversation: unbounded prompt growth, real
money on every turn, and the exact behaviour the ephemeral design exists to prevent. It is also the
property `AnalysisControllerChatAttachmentTurnTests` was written to protect, and those tests stay
green unchanged — they assert on `[Content]`, which still holds the analyst's typed sentence and
nothing else.

A separate column keeps the attachment *readable* without making it *history*. "Ephemeral" was
always two claims bundled together: not stored, and not replayed. Only the second one was
load-bearing. 055 drops the first and keeps the second.

### Why it lives on the USER turn, and how it differs from 053

The user turn is the one the upload arrived on: the analyst attached the file to their own question,
and this column records what they attached.

Script 053's `[HadEphemeralAttachment]` sits on the **assistant** turn and answers a different
question. It is a billing marker: one bit saying that row's `[InputTokens]` — Anthropic's count for
the whole request — includes a spreadsheet no later turn will replay, so the per-conversation
context gate must skip the row rather than measure the conversation by it.

The two are deliberately not merged. One records what the analyst sent; the other records what the
meter saw. So after this deploy:

| Column | Turn | Records |
|---|---|---|
| `AttachmentMarkdown` (055) | `Role = 'user'` | the capped Markdown the analyst attached |
| `HadEphemeralAttachment` (053) | `Role = 'assistant'` | that this turn's token count is not measurable |

053's behaviour is unchanged by this delivery, including its flag being written from the same capped
local. Nothing about the gate moved.

### Why `NVARCHAR(MAX) NULL` with no default and no backfill

The opposite call from 053, for the opposite reason. 053 modelled a decision with two states and no
third, so `BIT NOT NULL DEFAULT (0)` said something true about every pre-existing row. Here the
value **is** the text: a turn that carried nothing has no Markdown, and `NULL` is the honest way to
say so. An empty string would be a third state the application never writes.

There is nothing to backfill either. The text of every attachment sent before this deploy exists
nowhere in the system to be recovered, so the feature is **forward-only** in the same way 053 is:
past turns stay blank, the analyst re-uploads once, and from then on it is kept.

`NVARCHAR(MAX)` matches `[Content]` and the vendor JSON columns elsewhere in this schema.

### Size

`AnalysisController.CapAttachmentMarkdown` bounds a stored value at 200.000 characters (~400 KB of
UTF-16) plus a short truncation note, and `/chat` carries a 1 MB `RequestSizeLimit`, so one row
cannot exceed that regardless of what a caller posts. **The stored value is the capped one** — the
same string handed to Claude — not `request.AttachmentMarkdown`: persisting the raw field would let
a caller store an attachment larger than the one the model was ever shown.

Only turns that actually carried a file are affected, and the table already holds `nvarchar(max)`
transcripts, so the *shape* of its growth does not change — the rate does, on the minority of turns
that attach a spreadsheet. The verification block in the script includes a sizing query for once the
feature has been in use.

## DDL

`docs/database/055_chatmessages_add_attachmentmarkdown.sql`, one guarded block:

```sql
ALTER TABLE [dbo].[ChatMessages]
    ADD [AttachmentMarkdown] NVARCHAR(MAX) NULL;
```

Guarded on `sys.columns`, `PRINT` in both branches, `GO`, no transaction — the conventions in
`docs/database/README.md`. **No index**: the column is never a predicate, it is only ever read as
part of a conversation's own message list (already served by
`IX_ChatMessages_ConversationId_ChatMessageId`), and an `nvarchar(max)` column cannot be indexed in
any case. Re-running never rewrites an attachment already stored.

## How the application uses it

- `ChatMessage.AttachmentMarkdown` (`string?`) is mapped in `ChatMessageConfiguration` beside
  `HadEphemeralAttachment`, as `.HasColumnName("AttachmentMarkdown").HasColumnType("nvarchar(max)")`
  with no `IsRequired()` and no default.
- `AnalysisController.ChatAsync` writes it on the **user** turn, from the same `attachmentMarkdown`
  local the Claude call is handed. `CapAttachmentMarkdown` answers `null` for null, empty and
  whitespace alike, so a Portal that always sends the field stores `NULL` when nothing was uploaded
  — the same blank-is-absent rule the prompt flag and `ClaudeService` already share.
- One reordering was required and it is the only one: `CapAttachmentMarkdown` used to run *after*
  the user turn was written, so the capped value did not exist yet. It now runs immediately before
  that write, still **behind** the context gate, so a refused turn continues to leave no trace.
  `CapAttachmentMarkdown` is a pure static with no logging and no side effects, so moving it changes
  nothing observable.
- `GET /api/analyses/{resultId}/chat/{conversationId}` gains `hasAttachment` on each message,
  computed as `m.AttachmentMarkdown is not null`. Free — the repository already materialises the
  full entity — and it keeps that payload bounded: the endpoint returns every turn of the
  conversation, and one stored attachment can be 200.000 characters.

Nothing else reads the column. **No prompt reads it**: `BuildChatSystemPrompt`, its `hasAttachment`
flag, `ClaudeService.ChatAsync`, the history rebuilt from `[Content]`, the context gate,
`LastAssistantInputTokens` and the `[BillingQuota]` filter are all untouched.

## Effect on the PII carve-out in CLAUDE.md

The carve-out said the attachment "is never persisted to ChatMessages, never replayed as history,
and never logged as content". The first clause stops being true here, and CLAUDE.md is updated in
the same delivery rather than left asserting it.

The rule the carve-out scopes is about **prompts and logs**, and neither moves: nothing re-injects
the stored Markdown into a prompt, and the log line still records the attachment's size and
truncation flag and never a character of its content. What changes is that the analyst's own file is
now stored in the analyst's own tenant database, beside the analysis it belongs to — the same
database that already holds entity names and CUITs in `[dbo].[Entities]`. It is row-scoped by
`TenantId` like every other business row and reachable only through the conversation it belongs to.

## What this does NOT do

- **It does not show the analyst anything yet.** `hasAttachment` is a marker for step 2 to render
  against; there is no endpoint returning the stored Markdown. Step 2 adds the on-demand fetch.
- **It does not let the attachment be re-used.** A follow-up question still sends no spreadsheet
  unless the caller attaches one again. Re-injection is a prompt change and belongs with the work
  that decides what the model should be told about a re-sent file.
- **It is forward-only.** Turns taken before this deploy stay blank; see above.

## Pre-conditions

**Run this script before publishing the matching code, on every tenant database.** The column is
mapped, and the chat repository reads the **full entity** (`ChatRepository.GetMessagesAsync` is a
plain `ToListAsync` with no projection) and inserts the full entity (`AddMessageAsync`). EF Core
therefore names this column in every `SELECT` and every `INSERT` against `[dbo].[ChatMessages]`.

Against a database without it, **every chat turn and the whole conversation-history endpoint** fail
with `Invalid column name 'AttachmentMarkdown'`, and no application-level `try`/`catch` can protect
against it. This is the same deploy-order constraint as script 053, for the same reason, on the same
table. The monthly chat-token read in `UsageRepository` is a projection over `InputTokens` /
`OutputTokens` and is **not** affected; billing keeps working while the chat is down.

Deploy order: **DDL 055 → publish → restart the App Service** (so EF's model cache is rebuilt) →
verify by sending one chat turn with an attachment and one without, then reading the conversation
back and checking `hasAttachment`.

The Portal does **not** need to ship first: `hasAttachment` is an additive boolean a Portal that does
not know it simply ignores, and no enum value changes.

## Rollback

```sql
ALTER TABLE [dbo].[ChatMessages] DROP COLUMN [AttachmentMarkdown];
```

No default constraint to drop first — the column takes none. Nothing else references it and no other
table points at it, but this must be **preceded** by rolling the API back to a build without the
mapping, otherwise the deploy-order failure above is exactly what happens, on every chat turn.

Unlike 053's flag, what is lost here is real and unrecoverable: the attachments themselves. Every
spreadsheet an analyst attached since the deploy goes with the column, and no other copy of that
text exists anywhere in the system. The script's rollback section carries the `SELECT` to export
them first.
