# 053 — The context gate stops measuring a turn that carried an attachment

## What changed and why

The per-conversation context gate delivered with script 051 reads
`[dbo].[ChatMessages].[InputTokens]` off the most recent assistant turn and compares it to the
tenant plan's `[ChatContextBudgetTokens]`. That was chosen precisely because it is a number the
application does not compute: Anthropic billed it, so the gate cannot be wrong about it.

It is, however, a number about the **whole request** — and one part of a chat request is deliberately
not part of the conversation. A cash-flow `.xlsx` the analyst uploads to
`POST /api/analyses/{resultId}/chat/attachment` comes back as Markdown and rides the **next** turn as
a second text block. It is never written to `[Content]` and never replayed as history; the cap in
`AnalysisController.CapAttachmentMarkdown` is 200.000 characters, roughly 67.000 tokens, against a
**minimum budget of 40.000**.

> Script **055** later added `[AttachmentMarkdown]` on the **user** turn, so the analyst can read back
> what they attached. That does not change anything below: it is storage, nothing rebuilds a prompt
> from it, and it is not what the gate reads. The two columns sit on different turns and answer
> different questions — 053's flag records what the *meter* saw, 055's column records what the
> *analyst* sent. See `055_chatmessages_add_attachmentmarkdown.md`.

So a single attachment turn persisted an `[InputTokens]` several times larger than the transcript the
next turn would actually rebuild, and the gate — reading that figure — held the conversation above
budget from then on. The comment at `AnalysisController.AttachmentMarkdownMaxChars` had already
conceded the point: "cannot compound" was true of the history and false of the recorded count. This
is the §6.4 caveat in `Models/Financia/BillingPlans.cs` and the KNOWN CAVEAT on
`BillingOptions.EnforceChatContextBudget`; both are rewritten by this delivery.

**053 adds the one bit that tells the two kinds of turn apart.**

### Why a marker flag rather than a corrected count

Two more precise designs were considered and rejected.

*Subtracting an estimate of the attachment's tokens* puts a tokenizer, or a chars-per-token constant,
into the one measurement whose whole appeal was that nobody in this codebase computed it. The
estimate would then be wrong in an unbounded direction — a cell of CJK text and a cell of Spanish
labels do not cost the same per character — and it would be wrong **downwards** as often as upwards,
which is the direction this gate must never be wrong in.

*Re-counting the prompt without the attachment* means a second call to Anthropic's token-count
endpoint on every attachment turn: real latency and real money, in the hot path, to refine a gate
that ships with enforcement **off**.

The flag buys neither of those. It says only "this turn's number includes something that will never
be replayed", which is the single fact the gate needs and the only one that can be recorded for free
at write time.

### Why nothing already in the schema could answer it

`[InputTokens]` is one figure for the whole request and cannot be decomposed after the fact.
`[Content]` holds the analyst's typed sentence alone — deliberately, and that is the property the
attachment work exists to protect, so it must not start carrying the attachment now.
`[dbo].[ApiCallLogs]` records the same conflated total from the same response. There was no third
place to look, which is why this needed a column and therefore a DDL.

### Why `BIT NOT NULL DEFAULT (0)`

There is no third state worth modelling. Either the turn carried an attachment or it did not, and
"unknown" for a row written before this script is the same **decision** as "no": treat it as
measurable, which is exactly what the gate did with every row before the column existed. A nullable
`BIT` would buy a distinction with no consumer and force every read to handle it.

`NOT NULL` with a named default (`DF_ChatMessages_HadEphemeralAttachment`) backfills every existing
row inside the `ALTER` — no data migration, no separate `UPDATE`, and a future script can drop the
constraint by name rather than hunting a system-generated one.

## DDL

`docs/database/053_chatmessages_add_hadattachment.sql`, one guarded block:

```sql
ALTER TABLE [dbo].[ChatMessages]
    ADD [HadEphemeralAttachment] BIT NOT NULL
        CONSTRAINT [DF_ChatMessages_HadEphemeralAttachment] DEFAULT (0);
```

Guarded on `sys.columns`, `PRINT` in both branches, `GO`, no transaction — the conventions in
`docs/database/README.md`. **No index**: the column is only ever read as part of a conversation's own
message list, already served by `IX_ChatMessages_ConversationId_ChatMessageId`, and is never a
predicate. Re-running never rewrites a flag already recorded.

## How the application uses it

- `ChatMessage.HadEphemeralAttachment` is mapped in `ChatMessageConfiguration` beside `InputTokens` /
  `OutputTokens` / `Model`. The store `DEFAULT` is not mapped, matching the note on
  `TenantBillingConfigConfiguration`: the controller always sets it explicitly, so the default only
  ever applies to the backfilled rows.
- `AnalysisController.ChatAsync` writes it on the **assistant** turn only, as
  `attachmentMarkdown is not null` — the value *after* `CapAttachmentMarkdown`, which answers `null`
  for null, empty and whitespace alike, so it is true exactly when a real block was sent. The user
  turn stores nothing the GATE reads — since script 055 it does store the attachment's Markdown, in
  a different column no measurement looks at.
- `AnalysisController.LastAssistantInputTokens` **skips** a flagged row, continuing back to the most
  recent clean assistant turn, reusing the tolerance it already had for a row with no count at all.

Nothing else reads it. No response payload carries it, no endpoint filters on it, and the enforcement
switch is untouched: `Billing:EnforceChatContextBudget` still defaults to `false`, so the gate
continues to measure and log rather than refuse.

## What this does NOT fix

Worth stating plainly, because the fix is deliberately incomplete in one direction and completely
absent in another.

**It under-reports.** The skipped turn's history-only count is not reconstructed — it was never
computed — so the conversation is measured by an *older*, smaller clean turn. An analyst who attaches
on every turn leaves nothing measurable at all, and the conversation is not gated. That is the same
fail-open direction `LastAssistantInputTokens` already documented for a turn with no count, and the
same one the gate takes on a degraded billing read.

**It is forward-only.** Every row that predates this script reads `0`, so a conversation that took an
attachment turn *before* the deploy keeps its inflated figure and is still measured by it. Nothing
here rewrites history and nothing should: the true history-only count for a past turn does not exist
anywhere to be recovered. The remedy for those conversations is the one the 429 already offers —
start a new one.

**Consequence for the numbers this gate was shipped to collect.** The Warning logs behind
`EnforceChatContextBudget` are now a *lower* bound on real transcript sizes, with a residue of
pre-053 conversations reading high. Read them with both facts in hand before settling the three
per-plan budgets.

## Pre-conditions

**Run this script before publishing the matching code, on every tenant database.** The column is
mapped and `NOT NULL`, and the chat repository reads the **full entity**
(`ChatRepository.GetMessagesAsync` is a plain `ToListAsync` with no projection) and inserts the full
entity (`AddMessageAsync`). EF Core therefore names this column in every `SELECT` and every `INSERT`
against `[dbo].[ChatMessages]`.

Against a database without it, **every chat turn and the whole conversation-history endpoint** fail
with `Invalid column name 'HadEphemeralAttachment'`, and no application-level `try`/`catch` can
protect against it. Unlike script 051 this failure is loud rather than silently swallowed — the chat
simply stops working. The monthly chat-token read in `UsageRepository` is a projection over
`InputTokens` / `OutputTokens` and is **not** affected; billing keeps working while the chat is down.

Deploy order: **DDL 053 → publish → restart the App Service** (so EF's model cache is rebuilt) →
verify by sending one chat turn with an attachment and one without.

The Portal does **not** need to ship first: this adds no field to any response payload and no new enum
value.

## Rollback

```sql
ALTER TABLE [dbo].[ChatMessages]
    DROP CONSTRAINT [DF_ChatMessages_HadEphemeralAttachment];
ALTER TABLE [dbo].[ChatMessages] DROP COLUMN [HadEphemeralAttachment];
```

Nothing else references the column and no other table points at it, but this must be **preceded** by
rolling the API back to a build without the mapping — otherwise the deploy-order failure above is
exactly what happens, on every chat turn. What is lost is the flag itself: conversations measured
afterwards go back to reading an attachment turn's inflated count, which is the behaviour this script
exists to correct, not a loss of data.
