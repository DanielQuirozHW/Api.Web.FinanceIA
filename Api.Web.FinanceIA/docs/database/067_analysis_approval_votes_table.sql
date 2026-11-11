-- ============================================================================
-- Script  : 067_analysis_approval_votes_table.sql
-- Purpose : Create [dbo].[AnalysisApprovalVotes] — one row per manager per
--           analysis, recording how that manager voted. This is what Phase 2
--           counts against [dbo].[Teams].[ApprovalQuorum] to decide whether a
--           pre-approval has collected enough signatures to be confirmed.
-- Date    : 2026-09-16
-- Author  : Claude (assisted)
-- Notes   : ⚠⚠ THIS TABLE IS PROVISIONED AHEAD OF THE CODE THAT USES IT.
--           NOTHING IN THE RELEASE THAT SHIPS ALONGSIDE THIS SCRIPT READS OR
--           WRITES IT. There is no EF entity mapping it, no repository, no
--           controller and no DbSet. It will stay EMPTY until Phase 2 deploys,
--           and an empty table here is CORRECT, not a bug and not a failed
--           migration. Do not go looking for the code that fills it; do not
--           "fix" the schema-verification gate to expect it (see below).
--
--           Why ship it now, then. The DDL procedure treats every tenant as its
--           own database, so each script is a manual visit to N databases by a
--           human. Phase 1 already requires three of those visits (064, 065,
--           066). Adding the fourth to the same trip costs the DBA nothing and
--           means Phase 2 is a code-only deploy — no schema change, no
--           ddl_aplicada attestation, no window where the code is live and the
--           table is not. That asymmetry is the whole argument: creating an
--           unused table is free, while deploying code against a missing one
--           breaks production.
--
--           ⚠ NOT COVERED BY --verify-schema, AND THAT IS BY DESIGN. The deploy
--           gate (Clases/SchemaVerification) compares THIS BUILD'S EF MODEL
--           against sys.objects / sys.columns. No entity maps this table, so
--           the model does not mention it and the gate neither expects nor
--           checks it. Extra tables in the database are invisible to the gate,
--           which is exactly why provisioning ahead is safe. When Phase 2 adds
--           the entity, the gate starts covering it automatically and
--           SchemaExpectationTests should gain the matching InlineData then —
--           NOT now, because a gate that expects a table no build maps would
--           fail on a model that is correct.
--
--           ⚠ THE KEY IS AnalysisId, NOT ResultId. This is the trap in this
--           script and it is worth being explicit about, because the route does
--           the opposite. PATCH /api/analyses/{id}/decision takes a RESULT id:
--           AnalysisController.DecideAsync resolves [dbo].[AnalysisResults] by
--           it and only then hops to the analysis via [AnalysisId]. But the
--           DECISION STATE MACHINE lives entirely on [dbo].[Analyses] —
--           [Status], [PreApprovedBy], [PreApprovedAt], [ReviewedBy] are all
--           columns there (058, 060, 061). A vote is a signature on that state
--           machine, so it must key on the row that carries it. [ResultId] is
--           the deep-link id and nothing more (see commit for PR #219, which
--           fixed [Notifications].[SourceId] to carry it for exactly that
--           reason) — keying votes on it would mean a re-scored analysis with a
--           second result row could collect two independent quorums.
--
--           TENANTID IS NVARCHAR(100), NOT UNIQUEIDENTIFIER, per script 005 —
--           a Firebase tenant SLUG. Matches [dbo].[Analyses].[TenantId].
--
--           SURROGATE PRIMARY KEY HERE, COMPOSITE IN 064 — AND THE DIFFERENCE
--           IS DELIBERATE, not an inconsistency. A [dbo].[TeamManagers] row is
--           a MEMBERSHIP FACT: it has no life of its own, nothing addresses one,
--           and (TeamId, UserId) is its whole identity. A vote is an EVENT: it
--           happened at a time, by a person, and Phase 2 will want to address
--           one individually — to amend it, to show it in an audit trail, to
--           reference it from a future decision-log row. Events get surrogate
--           keys in this schema ([dbo].[Notes], [dbo].[Alerts],
--           [dbo].[Notifications] all do); membership facts do not.
--
--           UQ_AnalysisApprovalVotes_Analysis_User IS THE BUSINESS RULE: one
--           manager, one vote per analysis. Declared as a UNIQUE CONSTRAINT
--           rather than left to application checking, because Phase 2's failure
--           mode is a RACE — two browser tabs, or a retried request, confirming
--           the same analysis at the same moment. An application-side "have
--           they voted already?" read cannot close that window; a unique
--           constraint can, and the NAMED constraint is what Phase 2 catches to
--           turn the second one into a clean 409 rather than a 500.
--
--           It leads with [AnalysisId], so it doubles as the index for the only
--           read Phase 2 needs — "every vote on this analysis", to tally them.
--           Hence NO SEPARATE INDEX, the same reasoning 064 gives for its
--           clustered composite PK.
--
--           NO CHECK CONSTRAINT ON [Vote], and this one has hard evidence
--           behind it rather than just the 036/046/052/059/062 convention:
--           [dbo].[Analyses] HAS such a constraint, CK_Analyses_Status, and
--           script 060 exists for no other purpose than to ALTER it on every
--           tenant database so it would admit one single new value,
--           'PreApproved'. That is the cost of freezing a vocabulary into the
--           schema. Phase 2 has not settled this one — whether an abstention or
--           a "returned" vote belongs here is an open question — so the API
--           validates the value and the schema stays out of it.
--
--           [VotedAt] IS DATETIME2(7) AND ALWAYS UTC, per the convention in
--           this folder. The DEFAULT is for rows written by hand; Phase 2's
--           application code will supply it explicitly, exactly as every other
--           timestamp in this schema is supplied.
--
--           BOTH FOREIGN KEYS ARE DECLARED, following 027, 049, 054, 059 and
--           062: each column is a live SUBJECT (049's test), not a record of a
--           past action. A vote pointing at an analysis or a user that does not
--           exist can never be tallied. NO CASCADE, per 027, 049, 059 and 062 —
--           accounts are deactivated via [IsActive], never deleted, and a
--           cascade would silently destroy the signatures that justified a
--           credit decision because the signatory left the company. Those are
--           exactly the rows an auditor asks for.
--
--           NO APP SERVICE RESTART is needed for this script on its own — no
--           deployed code maps this table, so EF's model cache is unaffected.
--           Run it in the same window as 064/065, which do need one.
--
--           Idempotent: the table and both foreign keys are each guarded on
--           sys.objects / sys.foreign_keys, so the script can be re-run safely
--           and prints what it skipped.
-- ============================================================================

-- ── AnalysisApprovalVotes table ─────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisApprovalVotes]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[AnalysisApprovalVotes]
    (
        [VoteId]     UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_AnalysisApprovalVotes_VoteId] DEFAULT (NEWSEQUENTIALID()),
        [AnalysisId] UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Analyses].AnalysisId — NOT ResultId, see header
        [UserId]     UNIQUEIDENTIFIER NOT NULL,   -- FK -> [dbo].[Users].UserId; the manager who voted
        [TenantId]   NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005 — NOT a GUID
        [Vote]       NVARCHAR(20)     NOT NULL,   -- 'Approved' | 'Rejected'; validated in the API, no CHECK — see header
        [VotedAt]    DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_AnalysisApprovalVotes_VotedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_AnalysisApprovalVotes]
            PRIMARY KEY CLUSTERED ([VoteId] ASC),

        -- One manager, one vote per analysis. A UNIQUE CONSTRAINT rather than an
        -- application check because the failure mode is a race, and it leads
        -- with [AnalysisId] so it also serves the tally read. See header.
        CONSTRAINT [UQ_AnalysisApprovalVotes_Analysis_User]
            UNIQUE ([AnalysisId] ASC, [UserId] ASC)
    );
    PRINT 'Table [dbo].[AnalysisApprovalVotes] created (unused until Phase 2 — an empty table here is correct).';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[AnalysisApprovalVotes] already exists — skipped.';
END
GO

-- ── Foreign keys ────────────────────────────────────────────────────────────
-- Separate batches: the table must be committed by the GO above before a
-- constraint can reference it in the same script run.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_AnalysisApprovalVotes_Analysis]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisApprovalVotes]
        ADD CONSTRAINT [FK_AnalysisApprovalVotes_Analysis]
            FOREIGN KEY ([AnalysisId]) REFERENCES [dbo].[Analyses] ([AnalysisId]);
    PRINT 'Foreign key [FK_AnalysisApprovalVotes_Analysis] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_AnalysisApprovalVotes_Analysis] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_AnalysisApprovalVotes_User]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisApprovalVotes]
        ADD CONSTRAINT [FK_AnalysisApprovalVotes_User]
            FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);
    PRINT 'Foreign key [FK_AnalysisApprovalVotes_User] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_AnalysisApprovalVotes_User] already exists — skipped.';
END
GO

-- ── Indexes ─────────────────────────────────────────────────────────────────
-- NONE beyond the two the constraints create. PK_AnalysisApprovalVotes is
-- clustered on [VoteId]; UQ_AnalysisApprovalVotes_Analysis_User is a
-- nonclustered unique index leading with [AnalysisId], which serves Phase 2's
-- only read — "every vote on this analysis", to tally them against the team's
-- quorum. A third index on [UserId] ("everything this manager has voted on")
-- is NOT added: no screen asks for it today, and 063's argument applies —
-- an index written on every vote and read by nothing is a cost, not a win.
-- Add it in Phase 2 if a "my pending approvals" view actually needs it.

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. The table exists with all six columns, correctly typed → 6 rows
--
--    SELECT c.name AS ColumnName, t.name AS DataType,
--           c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisApprovalVotes]')
--    ORDER  BY c.column_id;
--    -- expected: VoteId uniqueidentifier NOT NULL / AnalysisId uniqueidentifier NOT NULL
--    --           UserId uniqueidentifier NOT NULL / TenantId nvarchar 200 NOT NULL
--    --           (max_length is BYTES, not characters — 200 = nvarchar(100), 40 = nvarchar(20))
--    --           Vote nvarchar 40 NOT NULL / VotedAt datetime2 NOT NULL
--
-- 2. Primary key is clustered on [VoteId] → 1 row
--
--    SELECT i.name, i.type_desc, c.name AS KeyColumn
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[AnalysisApprovalVotes]')
--      AND  i.is_primary_key = 1;
--    -- expected: PK_AnalysisApprovalVotes CLUSTERED VoteId
--
-- 3. The unique constraint exists, is UNIQUE, and leads with [AnalysisId] → 2 rows
--
--    SELECT i.name, i.is_unique, i.type_desc, ic.key_ordinal, c.name AS KeyColumn
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[AnalysisApprovalVotes]')
--      AND  i.name = N'UQ_AnalysisApprovalVotes_Analysis_User'
--    ORDER  BY ic.key_ordinal;
--    -- expected: ... 1 1 NONCLUSTERED 1 AnalysisId
--    --           ... 1 1 NONCLUSTERED 2 UserId
--    -- AnalysisId MUST be key_ordinal 1 — Phase 2's tally read seeks on it.
--
-- 4. Both foreign keys exist, enabled and trusted → 2 rows
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.foreign_keys
--    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisApprovalVotes]');
--    -- expected: FK_AnalysisApprovalVotes_Analysis  0  0
--    --           FK_AnalysisApprovalVotes_User      0  0
--
-- 5. ⚠ THE TABLE IS EMPTY, AND MUST STAY EMPTY UNTIL PHASE 2 DEPLOYS.
--
--    SELECT COUNT(*) AS Votes FROM [dbo].[AnalysisApprovalVotes];
--    -- expected in THIS release: 0, permanently. A non-zero count before Phase 2
--    -- ships means something is writing this table that should not be — the
--    -- application in this release has no code path that can.
--
-- 6. From Phase 2 on: no vote may cross a tenant boundary. This should return
--    NOTHING. Worth keeping in the run sheet even though it cannot fire yet.
--
--    SELECT v.[VoteId], v.[TenantId] AS VoteTenant,
--           a.[TenantId] AS AnalysisTenant, u.[TenantId] AS UserTenant
--    FROM   [dbo].[AnalysisApprovalVotes] v
--    JOIN   [dbo].[Analyses] a ON a.[AnalysisId] = v.[AnalysisId]
--    JOIN   [dbo].[Users]    u ON u.[UserId]     = v.[UserId]
--    WHERE  v.[TenantId] <> a.[TenantId] OR v.[TenantId] <> u.[TenantId];
--    -- expected: 0 rows.
--
-- 7. From Phase 2 on: the tally, and whether it met the team's quorum.
--
--    SELECT a.[AnalysisId], a.[Status], t.[ApprovalQuorum],
--           SUM(CASE WHEN v.[Vote] = N'Approved' THEN 1 ELSE 0 END) AS Approvals,
--           SUM(CASE WHEN v.[Vote] = N'Rejected' THEN 1 ELSE 0 END) AS Rejections
--    FROM   [dbo].[Analyses] a
--    JOIN   [dbo].[AnalysisApprovalVotes] v ON v.[AnalysisId] = a.[AnalysisId]
--    LEFT   JOIN [dbo].[Users] pu ON pu.[UserId] = a.[PreApprovedBy]
--    LEFT   JOIN [dbo].[Teams] t  ON t.[TeamId]  = pu.[TeamId]
--    GROUP  BY a.[AnalysisId], a.[Status], t.[ApprovalQuorum];
--
-- 8. From Phase 2 on: the constraint doing its job. Always 0 rows — a result
--    here means UQ_AnalysisApprovalVotes_Analysis_User is not what this script
--    declared.
--
--    SELECT [AnalysisId], [UserId], COUNT(*) AS Votes
--    FROM   [dbo].[AnalysisApprovalVotes]
--    GROUP  BY [AnalysisId], [UserId]
--    HAVING COUNT(*) > 1;
--    -- expected: 0 rows, always.
-- ============================================================================

-- ── Rollback (DBA reference only — NOT executed) ────────────────────────────
-- In THIS release the rollback is free and needs no application change first:
-- no deployed code maps this table, so dropping it is invisible to the running
-- API. Nothing references it either — both foreign keys point OUT of it.
--
--    DROP TABLE [dbo].[AnalysisApprovalVotes];
--    GO
--
-- Nothing is lost, because in this release the table is empty by construction.
--
-- ⚠ FROM PHASE 2 ON THIS IS NO LONGER TRUE. Once the entity exists, roll the
-- APPLICATION back first — dropping the table under a running Phase 2
-- deployment breaks the decision endpoint with "Invalid object name" — and the
-- rows become the audit record of who signed off which credit decision. Export
-- before dropping:
--
--    SELECT [VoteId], [AnalysisId], [UserId], [TenantId], [Vote], [VotedAt]
--    FROM   [dbo].[AnalysisApprovalVotes];
--
-- What is lost then is the per-manager signature trail. The decisions
-- themselves are NOT affected — [dbo].[Analyses].[Status], [PreApprovedBy] and
-- [ReviewedBy] carry the outcome and live on their own table — but the record
-- of WHO made up the quorum is gone, and every in-flight pre-approval reverts
-- to needing a single confirmation.
-- ============================================================================
