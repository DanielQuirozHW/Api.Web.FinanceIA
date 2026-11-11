-- ============================================================================
-- Script  : 061_analyses_add_proposed_confirmed_operations.sql
-- Purpose : Add the two operation records to [dbo].[Analyses] — what the analyst
--           PROPOSED at pre-approval and what the confirmer SETTLED ON — so the
--           operations behind a credit decision survive the hand-off between the
--           two signatures 058 introduced.
-- Date    : 2026-09-07
-- Author  : Claude (assisted)
-- Notes   : Two nullable NVARCHAR(MAX) columns, each holding a JSON array.
--
--             ProposedOperationsJson  — the operations the ANALYST proposed
--             ConfirmedOperationsJson — the operations the CONFIRMER settled on
--
--           WHAT IS MISSING TODAY. 058 gave a decision two amounts, and that was
--           only half of it. An analyst deciding above their limit enters the
--           OPERATIONS the cupo is made up of — type, importe, vigencia, a sola
--           firma, contragarantía real — and the API had nowhere to put them:
--           the pre-approval branch wrote [PreApprovedAmount] and
--           [PreApprovedCurrency] and dropped the rest. The RiskManager who
--           confirms therefore received a bare figure and had to reconstruct the
--           proposal by hand, or guess. These two columns are where it goes.
--
--           WHY TWO COLUMNS AND NOT ONE. The same reason 058 keeps two amounts.
--           A confirmer may change the operations, not just the total — drop one,
--           split another, shorten a vigencia — and the pair
--           (ProposedOperationsJson, ConfirmedOperationsJson) is the only place
--           that adjustment is visible. One column would record the last writer
--           and destroy the proposal it overwrote, which is precisely the loss
--           this script exists to stop.
--
--           WHY A JSON COLUMN AND NOT A CHILD TABLE. These are the TERMS of a
--           decision, frozen on the analysis, not live credit. The live rows keep
--           living in [dbo].[CreditLineOperations], created by CreditLinesController
--           once the analysis is Approved, with their own lifecycle, their own
--           FKs and their own audit columns (040, 041, 057). A child table here
--           would be a second, mutable home for the same shape and would invite
--           exactly the confusion of asking which of the two is the real
--           operation. Stored as a plain JSON blob, this follows
--           [dbo].[Entities].[LufeDataJson] and [dbo].[AnalysisResults].[BcraDataJson]:
--           serialised manually at the controller boundary, read back as one
--           value, never joined. Note this is the FIRST JSON column on
--           [dbo].[Analyses] — [ReviewNotes] is NVARCHAR(MAX) but free text.
--
--           THE ELEMENT SHAPE IS THE OPERATION SHAPE. Each element carries type,
--           amount, currency, validUntil, notes, aSolaFirma and
--           requiereContragarantiaReal — matching CreateOperationRequest field for
--           field, minus AnalysisId (the operation is stored INSIDE the analysis,
--           so carrying the id again would create a second contradictable copy of
--           the same fact). A confirmed element can therefore be handed straight
--           to POST /api/credit-lines/{id}/operations with no translation. The
--           API normalises the currency before storing, so the blob is canonical.
--
--           BOTH NULLABLE, no default, no backfill — following 058 next door
--           rather than 057. Like the pre-approval columns, these record an EVENT
--           that either happened or did not, so NULL is the honest "no operations
--           were recorded on this side" and not a guess. An empty array is never
--           stored: the API writes NULL when the caller sends none, so "nothing
--           was recorded" stays ONE value in the column instead of two. Every
--           analysis already decided stays valid untouched, reads as having no
--           operation record (which is true), and needs no data migration.
--
--           THE TWO ARE INDEPENDENT, and the asymmetry is deliberate:
--             · proposed non-NULL  -> an analyst escalated a proposal above their
--                                     limit; it is never rewritten afterwards
--             · confirmed non-NULL -> a figure was actually committed, in one step
--                                     or two
--           A one-step approval (within the analyst's limit) therefore fills
--           CONFIRMED and leaves PROPOSED NULL, exactly as it leaves
--           [PreApprovedAmount] NULL today. A rejection or a return leaves
--           CONFIRMED NULL, exactly as it leaves [DecisionAmount] NULL. Reading
--           proposed as "the operations of this decision" would therefore be
--           wrong for the common case — read confirmed for what was granted, and
--           the pair only when both are present.
--
--           NO CHECK CONSTRAINT and no ISJSON constraint. The element contract is
--           validated in the API (Models/Financia/AnalysisDecisionOperation.cs,
--           bounded to 50 operations, types against Clases/CreditLineTypes.cs,
--           currency against Clases/Currencies.cs), following the reasoning
--           recorded in 036, 046, 052 and 058: a value list or a payload shape in
--           the schema turns adding a line type into a migration on every tenant
--           database. The read path is defensive — a blob that will not parse
--           reads as "no operations recorded" rather than failing the endpoint —
--           so a hand-edited row cannot break the analysis detail either.
--
--           NO FOREIGN KEY. There is nothing to point at: the elements are frozen
--           terms, not rows in [dbo].[CreditLineOperations]. Deliberately so — an
--           FK would tie a historical decision to a live operation that can later
--           be cancelled, and rewrite history when it was.
--
--           NO INDEX. Neither column is a predicate anywhere: both are read as
--           attributes of an analysis already located by its primary key, or
--           through the existing result -> analysis path. An index on an
--           NVARCHAR(MAX) column is not possible in any case.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on EVERY tenant
--           database. Both columns are mapped in AnalysisConfiguration, and
--           AnalysisRepository.GetByIdAsync selects the FULL entity with no
--           projection while UpdateAsync calls .Update() (a full-row write), so EF
--           Core emits them in every SELECT and every UPDATE against
--           [dbo].[Analyses]. Against a database without them, the decision
--           endpoint, the analysis detail AND the analyses list all fail with
--           "Invalid column name", and no application-level try/catch can protect
--           against it. Same deploy-order constraint as 040, 041, 057 and 058.
--           Restart the App Service after deploying so EF's model cache is rebuilt.
--
--           ⚠ APPLY 058 + 059 + 060 IN THE SAME SITTING. None of the four has been
--           executed yet and they are one feature: 058 adds the two amounts these
--           two columns qualify, 059 the limits that decide whether a decision
--           escalates at all, 060 the 'PreApproved' status the escalation writes.
--           061 alone is harmless but pointless — with no pre-approval step there
--           is no proposal to keep.
--
--           ⚠ PORTAL FIRST, for the reason 058 already gives, and this script adds
--           nothing new to it: the API only starts ACCEPTING an optional
--           operations array and RETURNING two new nullable arrays. A Portal
--           deployed after this API keeps working (it simply sends none and
--           renders none) — it is 058's required amount/currency and its new
--           'PreApproved' status that force the order. Sending and rendering the
--           operations is a separate Portal change that can follow at its own pace.
--
--           Idempotent: both columns are guarded, so the script can be re-run
--           safely. Re-running never rewrites a decision already recorded.
-- ============================================================================

-- ── Analyses.ProposedOperationsJson ─────────────────────────────────────────
-- NULL = no pre-approval proposed any operations. True of every analysis approved
-- directly within an analyst's limit, of every rejection, and of every analysis
-- that predates the two-step flow. Never overwritten by a confirmation — that is
-- what makes the second signature legible.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ProposedOperationsJson'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [ProposedOperationsJson] NVARCHAR(MAX) NULL;
    PRINT 'Column ProposedOperationsJson added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ProposedOperationsJson already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Analyses.ConfirmedOperationsJson ────────────────────────────────────────
-- NULL = the final decision committed no operations. Stays NULL on a rejection and
-- on a return, and while an analysis is only pre-approved: none of the three
-- commits anything, and an array there would read as credit that was granted.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ConfirmedOperationsJson'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [ConfirmedOperationsJson] NVARCHAR(MAX) NULL;
    PRINT 'Column ConfirmedOperationsJson added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ConfirmedOperationsJson already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. Both columns exist, correctly typed, both nullable → 2 rows
--
--    SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
--    FROM   INFORMATION_SCHEMA.COLUMNS
--    WHERE  TABLE_SCHEMA = 'dbo'
--      AND  TABLE_NAME   = 'Analyses'
--      AND  COLUMN_NAME IN ('ProposedOperationsJson', 'ConfirmedOperationsJson')
--    ORDER  BY COLUMN_NAME;
--    -- expected: ConfirmedOperationsJson  nvarchar  -1  YES
--    --           ProposedOperationsJson   nvarchar  -1  YES
--    -- (CHARACTER_MAXIMUM_LENGTH -1 = MAX; IS_NULLABLE YES on both.)
--
-- 2. Nothing was backfilled — every pre-existing row is NULL on both. Run this
--    IMMEDIATELY after the ALTERs, before the deploy.
--
--    SELECT COUNT(*) AS TotalAnalyses,
--           SUM(CASE WHEN [ProposedOperationsJson]  IS NOT NULL THEN 1 ELSE 0 END) AS WithProposed,
--           SUM(CASE WHEN [ConfirmedOperationsJson] IS NOT NULL THEN 1 ELSE 0 END) AS WithConfirmed
--    FROM   [dbo].[Analyses];
--    -- expected immediately after this script: WithProposed = 0 and
--    --           WithConfirmed = 0, whatever TotalAnalyses is.
--
-- 3. After exercising the flow through the API (an analyst pre-approves above
--    their limit WITH operations, a RiskManager confirms with a different set),
--    both sides are present and differ → 1 row
--
--    SELECT TOP (1) [AnalysisId], [Status],
--                   [PreApprovedAmount], [DecisionAmount],
--                   ISJSON([ProposedOperationsJson])  AS ProposedIsJson,
--                   ISJSON([ConfirmedOperationsJson]) AS ConfirmedIsJson,
--                   DATALENGTH([ProposedOperationsJson])  AS ProposedBytes,
--                   DATALENGTH([ConfirmedOperationsJson]) AS ConfirmedBytes
--    FROM   [dbo].[Analyses]
--    WHERE  [ProposedOperationsJson] IS NOT NULL
--    ORDER  BY [PreApprovedAt] DESC;
--    -- expected: Status = 'Approved', both ISJSON = 1, and the two payloads
--    --           differing wherever the confirmer changed the operations.
--
-- 4. The element shape is what the credit-line routes expect — one row per
--    operation, with the seven fields and no analysisId:
--
--    SELECT a.[AnalysisId], o.[type], o.[amount], o.[currency], o.[validUntil],
--           o.[aSolaFirma], o.[requiereContragarantiaReal]
--    FROM   [dbo].[Analyses] a
--    CROSS  APPLY OPENJSON(a.[ConfirmedOperationsJson])
--           WITH ([type]                       INT,
--                 [amount]                     DECIMAL(18, 2),
--                 [currency]                   NVARCHAR(3),
--                 [validUntil]                 DATETIME2(7),
--                 [aSolaFirma]                 BIT,
--                 [requiereContragarantiaReal] BIT) AS o
--    WHERE  a.[ConfirmedOperationsJson] IS NOT NULL;
--    -- expected: every [currency] equal to that analysis's [DecisionCurrency],
--    --           every [type] between 0 and 17, every [amount] > 0.
--
-- 5. Never an empty array. The API stores NULL when a decision carries no
--    operations, so this must return 0 — two spellings of "nothing recorded"
--    would make every reader check for both.
--
--    SELECT COUNT(*) AS EmptyArrays
--    FROM   [dbo].[Analyses]
--    WHERE  [ProposedOperationsJson]  = N'[]'
--       OR  [ConfirmedOperationsJson] = N'[]';
--    -- expected: 0
--
-- 6. Old rows still read correctly — an analysis approved under the single-step
--    model has a decision and no operation record:
--
--    SELECT COUNT(*) AS SingleStepApprovals
--    FROM   [dbo].[Analyses]
--    WHERE  [Status] = N'Approved' AND [ConfirmedOperationsJson] IS NULL;
--    -- expected: every approval that predates this release, plus any approval
--    --           made without operations. These are valid, not broken, and must
--    --           NOT be backfilled.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- Roll the APPLICATION back first: the deployed code maps both columns, so
-- dropping them under a running deployment breaks every read and write of an
-- analysis. Export before dropping — the operations behind a decision exist in no
-- other column, and the proposal in particular exists nowhere else at all:
--
--    SELECT [AnalysisId], [Status],
--           [PreApprovedAmount], [PreApprovedCurrency],
--           [DecisionAmount],    [DecisionCurrency],
--           [ProposedOperationsJson], [ConfirmedOperationsJson]
--    FROM   [dbo].[Analyses]
--    WHERE  [ProposedOperationsJson]  IS NOT NULL
--       OR  [ConfirmedOperationsJson] IS NOT NULL;
--
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [ConfirmedOperationsJson];
--    ALTER TABLE [dbo].[Analyses] DROP COLUMN [ProposedOperationsJson];
--    GO
--
-- What survives a rollback is every decision itself ([Status], [ReviewedBy],
-- [ReviewedAt], [ReviewNotes], both amount pairs are untouched here), so no
-- approval is lost — only the record of which operations it was made up of. The
-- operations that were actually GRANTED also survive independently, as rows in
-- [dbo].[CreditLineOperations]; it is the proposal, and the difference between
-- the two sides, that this rollback destroys.
-- ============================================================================
