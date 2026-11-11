# 056 — The stored attachment gets its name back, and a way to be read

## What changed and why

Script 055 kept the analyst's spreadsheet: `ChatMessages.AttachmentMarkdown` holds the capped
Markdown on the **user** turn, and `GET /api/analyses/{resultId}/chat/{conversationId}` reports a
`hasAttachment` marker against the turn that carried it. 055's own rationale then listed, explicitly,
the two things it did not do:

> - **It does not show the analyst anything yet.** `hasAttachment` is a marker for step 2 to render
>   against; there is no endpoint returning the stored Markdown. Step 2 adds the on-demand fetch.

**056 is that step 2, on the BFF side.** It closes both halves of the gap:

1. **The name.** `hasAttachment` says *something* was attached; it cannot say *what*. The analyst
   reopens a conversation and reads "este turno traía un adjunto" with no way to tell
   `flujo-2026-final.xlsx` from the draft they replaced an hour later. On an analysis where three
   versions of the same cash flow were tried — the normal case — the marker on its own is close to
   useless. `[AttachmentFileName]` stores the name so the turn can be labelled.
2. **The content, on demand.** A new endpoint returns the stored Markdown for **one** message, so the
   Portal can render a viewer and a "reusar esta planilla" action without the history list carrying
   200.000 characters per turn.

**Not one token of what is sent to Claude changes.** The name never reaches `ClaudeService`, never
enters `BuildChatSystemPrompt` or its `hasAttachment` flag, and is never written to `[Content]`. The
context gate, `LastAssistantInputTokens` and the billing path are untouched, exactly as in 055.
Nothing re-injects the stored Markdown into a prompt either — the new endpoint is a **read path for
the analyst**, not a prompt path.

### Why it lives on the USER turn, beside 055

Same turn, same reason. The analyst attached the file to their own question; these two columns
together record what they attached — the content, and the label for it. 053's
`[HadEphemeralAttachment]` remains on the **assistant** turn and remains a billing marker. Nothing
about it moves.

| Column | Turn | Records |
|---|---|---|
| `AttachmentFileName` (056) | `Role = 'user'` | the name of the file the analyst attached |
| `AttachmentMarkdown` (055) | `Role = 'user'` | the capped Markdown of that file |
| `HadEphemeralAttachment` (053) | `Role = 'assistant'` | that this turn's token count is not measurable |

### The invariant: NULL exactly when `[AttachmentMarkdown]` is NULL

The two 055/056 columns are written from the **same capped local**, in the same object initialiser:

```csharp
var (attachmentMarkdown, attachmentTruncated) = CapAttachmentMarkdown(request.AttachmentMarkdown);

var attachmentFileName = attachmentMarkdown is null
    ? null
    : NormalizeAttachmentFileName(request.AttachmentFileName);
```

Deriving the name from the **capped Markdown** rather than from `request.AttachmentFileName` is the
whole point: a request that sends a file name and no attachment stores NULL for both, and neither
column can ever be a label for content that is not there. This is enforced in code, not by a `CHECK`
constraint — the constraint would have to be dropped and rebuilt by any later script touching either
column, for a rule the single writing code path already guarantees. The script's verification block
carries the queries that prove it holds in production.

The one legitimate asymmetry: rows written **between** the 055 and 056 deploys carry Markdown with no
name. That is the forward-only gap, not a fault; bound the `MarkdownWithoutName` check by `CreatedAt`
when running it.

### Why `NVARCHAR(255)` and not `NVARCHAR(MAX)`

It is a label, not content — the output of `Path.GetFileName` over what the browser sent, which every
mainstream filesystem already bounds well below 255. Storing it as `MAX` would put a second
off-row-capable column on a table that already has two, to hold forty characters.

The controller trims defensively to the same 255 with the existing `Truncate` helper, so an oversized
name is **cut, not rejected**. Refusing a turn over the length of a file name would dead-end an
analyst whose file the server had already accepted and parsed — the same call 055 made for the
Markdown itself, and the reason `ChatRequest.AttachmentFileName` carries no `[MaxLength]` attribute.
`Path.GetFileName` also strips any directory a caller puts in the field, so what is stored is a leaf
name and never a path.

### Why `NULL` with no default and no backfill

Same reasoning as 055. A turn that carried nothing has no file name, and `NULL` is the honest way to
say so. An empty string would be a third state the application never writes.

There is nothing to backfill. The name of every attachment sent before this deploy was never stored
on the message: it exists only inside the free-text `newValueJson` of the
`Analysis.ChatAttachment.Parsed` audit row, which is not addressed by message id and is not a source
to migrate from. So the feature is **forward-only**, exactly like 055 — past turns stay blank, the
analyst re-uploads once, and from then on it is kept.

## DDL

`docs/database/056_chatmessages_add_attachmentfilename.sql`, one guarded block:

```sql
ALTER TABLE [dbo].[ChatMessages]
    ADD [AttachmentFileName] NVARCHAR(255) NULL;
```

Guarded on `sys.columns`, `PRINT` in both branches, `GO`, no transaction — the conventions in
`docs/database/README.md`. **No index**: the column is never a predicate, it is only ever read as
part of a conversation's own message list, already served by
`IX_ChatMessages_ConversationId_ChatMessageId`. Re-running never rewrites a name already stored.

## How the application uses it

- `ChatMessage.AttachmentFileName` (`string?`) is mapped in `ChatMessageConfiguration` beside
  `AttachmentMarkdown`, as `.HasColumnName("AttachmentFileName").HasMaxLength(255)` with no
  `IsRequired()` and no default.
- `ChatRequest.AttachmentFileName` (`string?`, optional) is the Portal's way to send the name of the
  file whose Markdown rides `AttachmentMarkdown`. It is never sent to Claude and never written to
  `[Content]`.
- `AnalysisController.ChatAsync` writes it on the **user** turn only, derived from the capped
  Markdown as above, through `NormalizeAttachmentFileName` (leaf name → blank becomes `null` →
  `Truncate(name, 255)`).
- `GET /api/analyses/{resultId}/chat/{conversationId}` gains two additive fields per message:
  `attachmentFileName` (tens of bytes, which is what makes `hasAttachment` legible) and `messageId`
  (the handle the Portal needs to fetch the content). `hasAttachment` is unchanged and the **Markdown
  stays off this payload** — the endpoint returns every turn, and one attachment can be 200.000
  characters.
- **New:** `GET /api/analyses/{resultId}/chat/{conversationId}/messages/{messageId}/attachment`
  returns `{ messageId, fileName, markdown }` for one message. It runs the same auth/tenant/ownership
  chain as the history endpoint — tenant → result → conversation → message — with the message read
  through a new `IChatRepository.GetMessageByIdAsync` that scopes the predicate by conversation
  **and** tenant. 404 covers both "no such message" and "that message has no attachment", one shape
  for both so the route cannot be used to probe which ids exist. It logs the returned length, never a
  character of the content.

Note `messageId` is a **`long`**, not a GUID: `ChatMessages.ChatMessageId` is a `BIGINT IDENTITY` and
the entity types it as `long`. The route constraint is `{messageId:long}` accordingly. Exposing the
sequential id is not a leak — every call re-verifies tenant → result → conversation → message, so an
id guessed from another tenant resolves to nothing.

Nothing else reads either column. **No prompt reads them**: `BuildChatSystemPrompt`, its
`hasAttachment` flag, `ClaudeService.ChatAsync`, the history rebuilt from `[Content]`, the context
gate, `LastAssistantInputTokens` and the `[BillingQuota]` filter are all untouched.

## Effect on the PII carve-out in CLAUDE.md

None that changes the rule, and the carve-out's wording already covers this delivery. It says the
capped Markdown is persisted on the user turn as storage — "not a prompt and not a log" — and that
nothing reads the column into a prompt. 056 adds a second stored field of the same kind (a file name
an analyst chose, which may well contain a company name) and one endpoint that reads the stored
Markdown **back to that same analyst**, in their own tenant, through the conversation it belongs to.
Neither is a prompt and neither is a log: the new endpoint logs the length only, and the chat log
line still records the attachment's size and truncation flag and never a character of its content.

## What this does NOT do

- **It does not re-inject anything into a prompt.** A follow-up question still sends no spreadsheet
  unless the caller attaches one again. The Portal's "reusar esta planilla" action works by fetching
  the stored Markdown and posting it as an ordinary `attachmentMarkdown` on the next turn — the same
  path any upload takes — so the model is never handed anything by a route the analyst did not drive.
- **It does not change billing or the context gate.** 053's marker, `LastAssistantInputTokens` and
  the `[BillingQuota]` filter are untouched.
- **It is forward-only.** Turns taken before this deploy carry no name. Turns taken between the 055
  and 056 deploys carry Markdown with no name, and the new endpoint serves them with
  `fileName: null` — the viewer still works, it just has no label.

## Pre-conditions

**Run this script before publishing the matching code, on every tenant database.** The column is
mapped, and the chat repository reads the **full entity** (`GetMessagesAsync` is a plain `ToListAsync`
with no projection; the new `GetMessageByIdAsync` is a `FirstOrDefaultAsync` over the same entity)
and inserts the full entity (`AddMessageAsync`). EF Core therefore names this column in every
`SELECT` and every `INSERT` against `[dbo].[ChatMessages]`.

Against a database without it, **every chat turn, the whole conversation-history endpoint and the new
attachment endpoint** fail with `Invalid column name 'AttachmentFileName'`, and no application-level
`try`/`catch` can protect against it. This is the same deploy-order constraint as scripts 053 and
055, for the same reason, on the same table. The monthly chat-token read in `UsageRepository` is a
projection over `InputTokens` / `OutputTokens` and is **not** affected; billing keeps working while
the chat is down.

Deploy order: **DDL 056 → publish → restart the App Service** (so EF's model cache is rebuilt) →
verify by sending one chat turn with an attachment and one without, reading the conversation back and
checking that `attachmentFileName` is the uploaded name on the first and `null` on the second, then
calling the new endpoint with that `messageId` and confirming the Markdown comes back.

056 is independent of the still-pending 055 only in the sense that it does not require it to have run
first — but the feature does: **055 must be applied before or with 056**, since a name with no
Markdown column to label is useless. Apply them in order.

The Portal does **not** need to ship first: `attachmentFileName` and `messageId` are additive fields
a Portal that does not know them simply ignores, the new endpoint is new, and no enum value changes
and no existing field changes shape.

## Rollback

```sql
ALTER TABLE [dbo].[ChatMessages] DROP COLUMN [AttachmentFileName];
```

No default constraint to drop first — the column takes none. Nothing else references it and no other
table points at it, but this must be **preceded** by rolling the API back to a build without the
mapping, otherwise the deploy-order failure above is exactly what happens, on every chat turn.

What is lost is the **labels only**. 055's `[AttachmentMarkdown]` is a separate column and survives
this rollback intact, so the analyst keeps every spreadsheet and loses only their names — a materially
smaller loss than 055's own rollback. The script's rollback section carries the `SELECT` to export
them first if that still matters.
