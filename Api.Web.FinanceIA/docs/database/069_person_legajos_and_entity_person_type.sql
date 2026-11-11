-- ============================================================================
-- Script  : 069_person_legajos_and_entity_person_type.sql
-- Purpose : Let an entity be a PERSONA HUMANA, not only a company (M10,
--           Phase 1):
--             - create [dbo].[PersonLegajos], the legajo of a natural person,
--               ONE row per (TenantId, CUIT);
--             - add [PersonType], [FiscalRegime] and [PersonLegajoId] to
--               [dbo].[Entities], and [PersonLegajoId] to [dbo].[Fiadores];
--             - backfill the existing entities whose CUIT prefix says they are
--               a person, and give each one its legajo;
--             - move CUIT uniqueness on [dbo].[Entities] from GLOBAL
--               (UQ_Entities_CUIT) to PER TENANT (UQ_Entities_TenantId_CUIT).
-- Date    : 2026-09-27
-- Author  : Claude (assisted)
-- Notes   : THE FEATURE. FinancIA has assumed every entity is a company
--           (persona jurídica). From the release that ships with this script,
--           POST /api/entities records whether the CUIT belongs to a company or
--           to a person ([PersonType]), the ARCA fiscal regime ([FiscalRegime])
--           and, for a person, the monotributo category. A persona humana's
--           entity is linked to that person's LEGAJO: the person-level record
--           (name, regime, category, and the person's own ARCA and BCRA
--           snapshots) that later phases hang documents and fiador links from,
--           which is why the person lives in its own row rather than on the
--           entity. One legajo per person per tenant; at most one entity per
--           legajo. Documents, extraction and scoring are NOT part of this
--           phase: nothing here changes a score.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every
--           tenant/business database, then restart the App Service.
--           EF maps every new column (Entities.PersonType / FiscalRegime /
--           PersonLegajoId, Fiadores.PersonLegajoId) and the PersonLegajos
--           table, so publishing first breaks every SELECT and INSERT against
--           [dbo].[Entities] and [dbo].[Fiadores] with "Invalid column name":
--           the entity list, the detail, the create and every refresh fail.
--           In this deployment the business database is SHARED (the API binds
--           one static FinanciaDefault connection and separates tenants by the
--           TenantId column), but the DDL procedure treats each tenant as its
--           own database, so run it on every database that procedure lists.
--           Nothing below is scoped to one tenant, unlike 068's @TenantId:
--           every statement acts on every tenant's rows in the database it
--           runs on.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. Every new column is
--           nullable or defaulted, so the old code's INSERTs satisfy all four
--           CHECKs below, and SqlClient connects with the SET options the
--           filtered indexes need. The old code keeps working from the moment
--           this commits until the publish.
--
--           SET OPTIONS, THE FIRST BATCH. The script opens with SET ANSI_NULLS
--           ON and SET QUOTED_IDENTIFIER ON. 001_initial_schema set both
--           database defaults OFF, and two of the indexes below are FILTERED
--           (UX_Entities_PersonLegajoId, IX_Fiadores_PersonLegajoId). SQL
--           Server refuses to create a filtered index, and refuses any INSERT,
--           UPDATE or DELETE on a table that has one, unless both are ON
--           (error 1934); the backfill in section 4 UPDATEs [Entities] after
--           its filtered index exists. SSMS and Azure Data Studio connect with
--           both ON; sqlcmd connects with QUOTED_IDENTIFIER OFF unless it is
--           given -I. Run it in SSMS/ADS or with sqlcmd -I; the SETs at the top
--           cover a session that did not. (The other options a filtered index
--           needs are ON at connect in those clients, ARITHABORT implicitly
--           through ANSI_WARNINGS at compatibility level >= 90; 001 sets 150.)
--           The application is unaffected: SqlClient connects with both ON.
--           ⚠ Hand-written DML is not: from now on an UPDATE on [Entities] from
--           a session with either option OFF fails with 1934, exactly as one on
--           [Fiadores] already does since 046's UX_Fiadores_ActivePerEntityCuit.
--
--           THE LIVE BUG THIS FIXES. The business database is shared and
--           UQ_Entities_CUIT is GLOBAL, so today a CUIT that ANOTHER tenant has
--           already registered makes POST /api/entities fail with a 500: the
--           create's duplicate pre-check is tenant-scoped (correctly: it must
--           not reveal another tenant's portfolio), finds nothing, and the
--           INSERT hits the constraint. A same-tenant race (two creates of the
--           same CUIT at once) ends in the same 500. After the swap the first
--           case succeeds, and the matching code turns the second into a 409 by
--           recognising the index NAME in the provider's error. That is why the
--           unique index names below are load-bearing: do not rename them.
--
--           CHECK CONSTRAINTS, AND WHY HERE. This script adds four, which is
--           the exception to the folder's usual preference: 036, 046, 052 and
--           067 deliberately validate an EVOLVING vocabulary in the API. These
--           follow CK_Analyses_Status (mirrored by AnalysisStatuses in code)
--           and, on this very table, 001's CK_Entities_MipymeCategory:
--             - [PersonType] is binary and STRUCTURAL.
--               CK_Entities_PersonLegajo_Humana depends on it (only a Humana
--               may point at a legajo), and that rule means nothing if the
--               column can hold a third value.
--             - [FiscalRegime] is a CLOSED set the API derives from ARCA's
--               answer: Monotributo, ResponsableInscripto, Exento, or
--               Desconocido when the answer does not settle it. The code's list
--               is FiscalRegimes.Known, and a test pins it to these CHECKs.
--           The cost is known and accepted: widening either list takes a DDL on
--           every database, exactly as 060 did for CK_Analyses_Status.
--           WITH CHECK, following 043 and 060: every existing row satisfies
--           all four at the moment they are added (the new columns hold
--           'Juridica' / NULL / NULL), so validation cannot fail on legitimate
--           data. [MonotributoCategory] gets NO CHECK: the category scale is
--           ARCA's, and the API validates the letter (A-K) in
--           FiscalRegimes.NormalizeMonotributoCategory.
--
--           THE LEGAJO, [dbo].[PersonLegajos]:
--             - [CUIT] is the person's CUIT/CUIL in the same hyphenated
--               XX-XXXXXXXX-X form as [dbo].[Entities].[CUIT]. The backfill
--               copies it verbatim; the API stores the value it validated.
--             - [FullName] is NVARCHAR(300), the width of Entities.BusinessName
--               it is filled from ("APELLIDO, NOMBRE" as ARCA gives it).
--             - [FiscalRegime] defaults to 'Desconocido', never NULL: a legajo
--               always states what is known about the regime, even "nothing".
--             - [PatrimonialRegime] is reserved: nothing writes it this phase.
--             - [ArcaSnapshotJson] and [BcraDataJson] are the person's own ARCA
--               and BCRA snapshots, in the shapes of Entities.FiscalDataJson and
--               Entities.BcraDataJson. [NosisDataJson] / [NosisDataUpdatedAt]
--               ship NULL and stay NULL this phase.
--             - [CreatedBy] is UNIQUEIDENTIFIER NULL with no FK, the shape of
--               Entities.CreatedBy (032). NULL means a script wrote the row
--               (066's rule, which 062 made the column nullable for).
--             - The NEWSEQUENTIALID() default on the key serves the backfill
--               and rows written by hand; the application supplies its own Guid
--               (the NotificationConfiguration precedent).
--             - UX_PersonLegajos_TenantId_CUIT: one legajo per person per
--               tenant. The API recognises a lost insert race by this name and
--               relinks to the winner instead of failing.
--
--           ⚠ PII. A legajo is a NATURAL PERSON's record: the CUIL, the full
--           name, the ARCA constancia and the BCRA Central de Deudores snapshot
--           of that person. It must NEVER be serialized wholesale into a Claude
--           prompt (the rule Fiador.cs carries for the same reason), and
--           neither its CUIT nor its name goes to a log. Nothing builds a prompt
--           from it in this phase. Treat any export of this table (see the
--           rollback) as personal data.
--
--           THE LINKS:
--             - FK_Entities_PersonLegajos and FK_Fiadores_PersonLegajos, both
--               with NO CASCADE, per 027, 049 and 067: a legajo is a live
--               SUBJECT (049's test), and deleting one must never silently
--               take a credit file's links with it.
--             - UX_Entities_PersonLegajoId: at most ONE entity per legajo,
--               filtered on IS NOT NULL because every Juridica holds NULL.
--             - CK_Entities_PersonLegajo_Humana: only a Humana may point at a
--               legajo.
--             - Fiadores.PersonLegajoId + IX_Fiadores_PersonLegajoId, filtered
--               and NOT unique (one person may guarantee several PyMEs).
--               NOTHING WRITES IT IN THIS PHASE: it stays NULL until the phase
--               that links fiadores to legajos (Phase 5). Shipped now so that
--               phase is a code-only deploy, which is 067's argument.
--
--           THE BACKFILL (section 4). Existing entities are classified by CUIT
--           prefix: 20 / 23 / 24 / 27 are natural persons and become 'Humana';
--           every other prefix keeps 'Juridica'. The script has no ARCA answer
--           to settle an unusual prefix, and the API lets an unambiguous prefix
--           win as well (CuitHelper.ResolvePersonType). Each Humana then gets
--           [FiscalRegime] = 'Desconocido' (what the API writes for a Humana
--           whose regime is not established) and a legajo, and is linked to it.
--           The legajo's regime is the LITERAL 'Desconocido', never
--           Entities.[FiscalRegime]: the API writes a known regime to a legajo
--           only together with the ARCA answer it came from (ArcaSnapshotJson),
--           and the backfill copies no ARCA snapshot, since an old
--           FiscalDataJson predates the regime fields and could not carry one.
--           The next refresh-arca fills both. The BCRA snapshot IS copied: it
--           was queried by this same CUIT, so it is the person's. [CreatedBy]
--           is NULL (066's rule: no application user created the row, the
--           script did). Entities.[UpdatedAt] is NOT touched: nobody edited
--           these entities.
--           Statements (3) and (4) skip a row that is ALREADY linked, and a
--           legajo that another entity already holds: a stale link left by the
--           old code (post-check 3) then shows up in post-checks 2 and 3 instead
--           of failing the whole transaction on UX_Entities_PersonLegajoId.
--
--           TRANSACTIONS: TWO, following 011, 013 and 068. The folder's
--           convention is no transactions, because every guarded block is one
--           atomic statement. Two blocks here are not:
--             - the BACKFILL is all or nothing, so no reader ever sees a Humana
--               without its legajo halfway through;
--             - the CUIT SWAP must never leave a window without uniqueness: the
--               new index and the drop of the old key commit together or not at
--               all.
--           Both run with SET XACT_ABORT ON, so any run-time error rolls the
--           whole block back.
--
--           WHY THE SWAP IS THE LAST BATCH. It first checks that no foreign key
--           references UQ_Entities_CUIT (001 declares none, and 027, 030 and 046
--           all reference EntityId; pre-check 2 shows it). If one does, it rolls
--           back, raises an error and RETURNs. RAISERROR does not abort anything
--           under XACT_ABORT, hence the explicit ROLLBACK; and RETURN ends only
--           the CURRENT batch, because SSMS and sqlcmd carry on with the next
--           one after a GO. Placing the swap last is what makes "stop" mean the
--           end of the script. Nothing before it depends on it: while
--           UQ_Entities_CUIT stands, CUIT is unique globally, which is stricter
--           than anything the backfill needs.
--           If the swap aborts, the application still works (sections 1-4 are
--           committed), but a CUIT that exists in another tenant keeps
--           returning 500 on create until the swap runs.
--
--           UQ_Entities_TenantId_CUIT is a unique INDEX, not a constraint (the
--           form and name the EF mapping declares), and creating it cannot fail
--           while the old global key is in place: a CUIT unique across the
--           database is unique within each tenant. The old key is dropped as
--           whatever it turns out to be: a constraint (001 created it as one,
--           which is the real case) or a plain unique index.
--           ⚠ IX_Entities_TenantId becomes a redundant prefix again, now of
--           this index as 045 said it already was of IX_Entities_TenantId_
--           CreatedAt. Dropping it is a separate DBA decision, not done here.
--
--           The index builds are offline (no WITH (ONLINE = ON), unlike 045):
--           each takes a brief schema lock. Run the script in a quiet window.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; the backfill converges, each statement touching only
--           the rows a previous run did not; the swap skips what is done.
--           ⚠ RE-RUNNING AFTER THE DEPLOY IS USEFUL, not merely safe. Between
--           this script and the publish the OLD code keeps creating entities,
--           and it knows nothing of [PersonType]: its rows get the default
--           'Juridica' even with a 20/23/24/27 prefix, and no legajo. Re-run
--           the script right after the publish and it backfills them. Do it
--           promptly: a straggler the new code has already refreshed from ARCA
--           carries a known Entities.FiscalRegime that its new legajo
--           ('Desconocido') will not match until the next refresh-arca.
--           Post-check 3 finds the one case a re-run cannot repair (an old-code
--           PUT that changed the CUIT of an entity this script had linked).
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. What the backfill will classify, per tenant. Rows under 20 / 23 / 24 / 27
--    become 'Humana' and get one legajo each; every other prefix stays
--    'Juridica'. Keep the output: post-check 1 should agree with it.
--
--    SELECT TenantId, LEFT(CUIT, 2) AS Prefijo, COUNT(*) AS Entities
--    FROM   [dbo].[Entities]
--    GROUP  BY TenantId, LEFT(CUIT, 2)
--    ORDER  BY 1, 2;
--
-- 2. Nothing references UQ_Entities_CUIT. Expected: 0 rows. A row here is a
--    foreign key the swap would have to break: section 5 refuses, rolls back
--    and stops. Resolve it with the DBA before running the script.
--
--    SELECT fk.name AS ForeignKey,
--           OBJECT_NAME(fk.parent_object_id) AS ReferencingTable
--    FROM   sys.foreign_keys fk
--    JOIN   sys.indexes i ON i.object_id = fk.referenced_object_id
--                        AND i.index_id  = fk.key_index_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[Entities]')
--      AND  i.name      = N'UQ_Entities_CUIT';
-- ============================================================================

-- ── 0. Session options (filtered indexes; see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. PersonLegajos table ──────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[PersonLegajos]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[PersonLegajos]
    (
        [PersonLegajoId]      UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_PersonLegajos_PersonLegajoId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]            NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005; NOT a GUID
        [CUIT]                NVARCHAR(13)     NOT NULL,   -- the person's CUIT/CUIL, XX-XXXXXXXX-X like [dbo].[Entities].CUIT
        [FullName]            NVARCHAR(300)    NOT NULL,   -- "APELLIDO, NOMBRE"; the width of [dbo].[Entities].BusinessName
        [FiscalRegime]        NVARCHAR(30)     NOT NULL
            CONSTRAINT [DF_PersonLegajos_FiscalRegime] DEFAULT ('Desconocido'),   -- closed set, see CK below
        [MonotributoCategory] NVARCHAR(2)      NULL,       -- letter A-K, validated in the API; no CHECK
        [ActivityStartDate]   DATE             NULL,
        [MaritalStatus]       NVARCHAR(30)     NULL,
        [PatrimonialRegime]   NVARCHAR(30)     NULL,       -- reserved: nothing writes it this phase
        [ArcaSnapshotJson]    NVARCHAR(MAX)    NULL,       -- ArcaFiscalData shape, as [dbo].[Entities].FiscalDataJson
        [ArcaDataUpdatedAt]   DATETIME2        NULL,
        [BcraDataJson]        NVARCHAR(MAX)    NULL,       -- the person's BCRA snapshot, queried by the CUIL
        [BcraDataUpdatedAt]   DATETIME2        NULL,
        [NosisDataJson]       NVARCHAR(MAX)    NULL,       -- NULL, and stays NULL this phase
        [NosisDataUpdatedAt]  DATETIME2        NULL,       -- NULL, and stays NULL this phase
        [CreatedBy]           UNIQUEIDENTIFIER NULL,       -- [dbo].[Users].UserId like Entities.CreatedBy (032); no FK; NULL = a script
        [CreatedAt]           DATETIME2        NOT NULL
            CONSTRAINT [DF_PersonLegajos_CreatedAt] DEFAULT (GETUTCDATE()),
        [UpdatedAt]           DATETIME2        NOT NULL
            CONSTRAINT [DF_PersonLegajos_UpdatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_PersonLegajos]
            PRIMARY KEY CLUSTERED ([PersonLegajoId] ASC),

        -- The code's list is FiscalRegimes.Known; a test pins it to this one.
        CONSTRAINT [CK_PersonLegajos_FiscalRegime]
            CHECK ([FiscalRegime] IN (N'Monotributo', N'ResponsableInscripto', N'Exento', N'Desconocido'))
    );
    PRINT 'Table [dbo].[PersonLegajos] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[PersonLegajos] already exists — skipped.';
END
GO

-- One legajo per person per tenant. The name is load-bearing: the API recognises
-- a lost insert race by it and relinks to the winning row instead of failing.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[PersonLegajos]')
      AND  name      = N'UX_PersonLegajos_TenantId_CUIT'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_PersonLegajos_TenantId_CUIT]
        ON [dbo].[PersonLegajos] ([TenantId] ASC, [CUIT] ASC);
    PRINT 'Index [UX_PersonLegajos_TenantId_CUIT] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_PersonLegajos_TenantId_CUIT] already exists — skipped.';
END
GO

-- ── 2. [dbo].[Entities] ─────────────────────────────────────────────────────
-- One object per batch: a CHECK, FK or index never sits in the batch that adds
-- its column, so each column is committed before anything refers to it.

-- PersonType. NOT NULL with a named default: the ALTER fills every existing row
-- with 'Juridica' in place, which is what every entity has been assumed to be.
-- Section 4 then corrects the rows whose CUIT prefix says otherwise.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'PersonType'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [PersonType] NVARCHAR(10) NOT NULL
            CONSTRAINT [DF_Entities_PersonType] DEFAULT ('Juridica');
    PRINT 'Column PersonType added to [dbo].[Entities] (existing rows = ''Juridica'').';
END
ELSE
BEGIN
    PRINT 'Column PersonType already exists on [dbo].[Entities] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Entities_PersonType'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Entities]')
)
BEGIN
    ALTER TABLE [dbo].[Entities] WITH CHECK
        ADD CONSTRAINT [CK_Entities_PersonType]
        CHECK ([PersonType] IN (N'Juridica', N'Humana'));
    PRINT 'Constraint CK_Entities_PersonType added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Entities_PersonType already exists on [dbo].[Entities] — skipped.';
END
GO

-- FiscalRegime. Nullable: NULL is "not determined yet", which is what every row
-- that predates this script honestly is. For a Humana the API always writes one
-- ('Desconocido' at least), and section 4 aligns the backfilled rows with that.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'FiscalRegime'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [FiscalRegime] NVARCHAR(30) NULL;
    PRINT 'Column FiscalRegime added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column FiscalRegime already exists on [dbo].[Entities] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Entities_FiscalRegime'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Entities]')
)
BEGIN
    ALTER TABLE [dbo].[Entities] WITH CHECK
        ADD CONSTRAINT [CK_Entities_FiscalRegime]
        CHECK ([FiscalRegime] IS NULL
               OR [FiscalRegime] IN (N'Monotributo', N'ResponsableInscripto', N'Exento', N'Desconocido'));
    PRINT 'Constraint CK_Entities_FiscalRegime added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Entities_FiscalRegime already exists on [dbo].[Entities] — skipped.';
END
GO

-- PersonLegajoId. NULL for every Juridica; set for a Humana, by the API on create
-- and by section 4 for the rows that predate it.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'PersonLegajoId'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [PersonLegajoId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column PersonLegajoId added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column PersonLegajoId already exists on [dbo].[Entities] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Entities_PersonLegajos]')
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD CONSTRAINT [FK_Entities_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]);
    PRINT 'Foreign key [FK_Entities_PersonLegajos] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Entities_PersonLegajos] already exists — skipped.';
END
GO

-- At most ONE entity per legajo. Filtered, because every Juridica holds NULL and
-- a plain unique index would admit only one of them. Needs the SET options of
-- section 0, to be created and for every later write to [Entities].
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'UX_Entities_PersonLegajoId'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_Entities_PersonLegajoId]
        ON [dbo].[Entities] ([PersonLegajoId] ASC)
        WHERE [PersonLegajoId] IS NOT NULL;
    PRINT 'Index [UX_Entities_PersonLegajoId] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_Entities_PersonLegajoId] already exists — skipped.';
END
GO

-- Only a Humana may point at a legajo. Holds for every existing row: the column
-- was added NULL above, and section 4 links Humana rows only.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Entities_PersonLegajo_Humana'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Entities]')
)
BEGIN
    ALTER TABLE [dbo].[Entities] WITH CHECK
        ADD CONSTRAINT [CK_Entities_PersonLegajo_Humana]
        CHECK ([PersonLegajoId] IS NULL OR [PersonType] = N'Humana');
    PRINT 'Constraint CK_Entities_PersonLegajo_Humana added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Entities_PersonLegajo_Humana already exists on [dbo].[Entities] — skipped.';
END
GO

-- ── 3. [dbo].[Fiadores] ─────────────────────────────────────────────────────
-- Provisioned ahead: NOTHING writes this column in this phase (see header).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  name      = N'PersonLegajoId'
)
BEGIN
    ALTER TABLE [dbo].[Fiadores]
        ADD [PersonLegajoId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column PersonLegajoId added to [dbo].[Fiadores] (NULL on every row until a later phase).';
END
ELSE
BEGIN
    PRINT 'Column PersonLegajoId already exists on [dbo].[Fiadores] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Fiadores_PersonLegajos]')
)
BEGIN
    ALTER TABLE [dbo].[Fiadores]
        ADD CONSTRAINT [FK_Fiadores_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]);
    PRINT 'Foreign key [FK_Fiadores_PersonLegajos] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Fiadores_PersonLegajos] already exists — skipped.';
END
GO

-- Serves "every fiador row for this person". NOT unique: one person may guarantee
-- several PyMEs, one Fiadores row each. Filtered, because the column is NULL on
-- every row until the phase that writes it.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  name      = N'IX_Fiadores_PersonLegajoId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Fiadores_PersonLegajoId]
        ON [dbo].[Fiadores] ([PersonLegajoId] ASC)
        WHERE [PersonLegajoId] IS NOT NULL;
    PRINT 'Index [IX_Fiadores_PersonLegajoId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Fiadores_PersonLegajoId] already exists — skipped.';
END
GO

-- ── 4. Backfill: classify the existing entities, give each Humana a legajo ──
-- One transaction, all or nothing (see header). Every statement is idempotent on
-- its own, so a re-run (in particular one AFTER the deploy) only picks up the
-- rows a previous run did not see. Every tenant in this database is covered.

SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @Classified     INT;
DECLARE @RegimeAligned  INT;
DECLARE @LegajosCreated INT;
DECLARE @Linked         INT;

-- (1) The CUIT prefix says natural person: 20 / 23 / 24 / 27.
UPDATE [dbo].[Entities]
SET    [PersonType] = N'Humana'
WHERE  LEFT([CUIT], 2) IN (N'20', N'23', N'24', N'27')
  AND  [PersonType] <> N'Humana';

SET @Classified = @@ROWCOUNT;

-- (2) What the API writes for a Humana whose regime is not established.
UPDATE [dbo].[Entities]
SET    [FiscalRegime] = N'Desconocido'
WHERE  [PersonType]   = N'Humana'
  AND  [FiscalRegime] IS NULL;

SET @RegimeAligned = @@ROWCOUNT;

-- (3) One legajo per Humana (TenantId, CUIT) that has none yet. (TenantId, CUIT)
--     is unique in [Entities] (globally unique CUIT before the swap, per tenant
--     after it), so this can never insert two legajos for one person.
INSERT INTO [dbo].[PersonLegajos]
       ([TenantId], [CUIT], [FullName], [FiscalRegime],
        [BcraDataJson], [BcraDataUpdatedAt], [CreatedBy])
SELECT e.[TenantId],
       e.[CUIT],                -- verbatim: the same hyphenated form
       e.[BusinessName],
       N'Desconocido',          -- a LITERAL, never e.[FiscalRegime]; see header
       e.[BcraDataJson],        -- queried by this same CUIT, so it is the person's
       e.[BcraDataUpdatedAt],
       NULL                     -- no application user created this legajo; the script did
FROM   [dbo].[Entities] e
WHERE  e.[PersonType]     = N'Humana'
  AND  e.[PersonLegajoId] IS NULL      -- an already-linked row (even a stale link) gets no second legajo
  AND  NOT EXISTS (
           SELECT 1
           FROM   [dbo].[PersonLegajos] p
           WHERE  p.[TenantId] = e.[TenantId]
             AND  p.[CUIT]     = e.[CUIT]
       );

SET @LegajosCreated = @@ROWCOUNT;

-- (4) Link each unlinked Humana to its legajo. Humana only, so
--     CK_Entities_PersonLegajo_Humana always holds; never to a legajo another
--     entity already holds, so UX_Entities_PersonLegajoId does too (a row skipped
--     that way shows in post-check 2, and the stale link holding its legajo in
--     post-check 3). [UpdatedAt] is deliberately untouched.
UPDATE e
SET    e.[PersonLegajoId] = p.[PersonLegajoId]
FROM   [dbo].[Entities] e
JOIN   [dbo].[PersonLegajos] p
       ON  p.[TenantId] = e.[TenantId]
       AND p.[CUIT]     = e.[CUIT]
WHERE  e.[PersonLegajoId] IS NULL
  AND  e.[PersonType]     = N'Humana'
  AND  NOT EXISTS (
           SELECT 1
           FROM   [dbo].[Entities] x
           WHERE  x.[PersonLegajoId] = p.[PersonLegajoId]
       );

SET @Linked = @@ROWCOUNT;

COMMIT TRANSACTION;

PRINT 'Backfill committed.';
PRINT '  Entities classified Humana by CUIT prefix (20/23/24/27): ' + CAST(@Classified AS NVARCHAR(20)) + ' row(s).';
PRINT '  Humana entities given FiscalRegime ''Desconocido'':       ' + CAST(@RegimeAligned AS NVARCHAR(20)) + ' row(s).';
PRINT '  Legajos created in [dbo].[PersonLegajos]:                 ' + CAST(@LegajosCreated AS NVARCHAR(20)) + ' row(s).';
PRINT '  Humana entities linked to their legajo:                   ' + CAST(@Linked AS NVARCHAR(20)) + ' row(s).';
PRINT 'All four are 0 on a re-run that found nothing new. After the deploy, non-zero counts are the old code''s stragglers.';
GO

-- ── 5. CUIT uniqueness: global → per tenant (the LAST batch; see header) ────

SET XACT_ABORT ON;

BEGIN TRANSACTION;

-- (a) Refuse to drop a key something depends on. RAISERROR does not abort under
--     XACT_ABORT, so roll back first; RETURN then ends THIS batch, which is why
--     this is the last one.
IF EXISTS (
    SELECT 1
    FROM   sys.foreign_keys fk
    JOIN   sys.indexes i
           ON  i.object_id = fk.referenced_object_id
           AND i.index_id  = fk.key_index_id
    WHERE  i.object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  i.name      = N'UQ_Entities_CUIT'
)
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR (N'069: a foreign key references UQ_Entities_CUIT, so it cannot be dropped. The CUIT uniqueness swap was NOT applied and nothing in this batch changed; sections 1-4 are committed and the application works. List the foreign key with pre-check 2, resolve it with the DBA, then re-run this script.', 16, 1);
    RETURN;
END
ELSE
BEGIN
    PRINT 'No foreign key references UQ_Entities_CUIT — the swap proceeds.';
END

-- (b) The per-tenant key. Cannot fail while the global key is in place: a CUIT
--     unique across the database is unique within each tenant.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'UQ_Entities_TenantId_CUIT'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UQ_Entities_TenantId_CUIT]
        ON [dbo].[Entities] ([TenantId] ASC, [CUIT] ASC);
    PRINT 'Index UQ_Entities_TenantId_CUIT created on [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Index UQ_Entities_TenantId_CUIT already exists on [dbo].[Entities] — skipped.';
END

-- (c) The global key, dropped as whatever it is: 001 created a UNIQUE constraint,
--     but a hand-made environment may hold a plain unique index of the same name.
IF EXISTS (
    SELECT 1
    FROM   sys.key_constraints
    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name             = N'UQ_Entities_CUIT'
      AND  type             = N'UQ'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        DROP CONSTRAINT [UQ_Entities_CUIT];
    PRINT 'Constraint UQ_Entities_CUIT dropped from [dbo].[Entities].';
END
ELSE IF EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'UQ_Entities_CUIT'
)
BEGIN
    DROP INDEX [UQ_Entities_CUIT] ON [dbo].[Entities];
    PRINT 'Index UQ_Entities_CUIT (a plain unique index, not a constraint) dropped from [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'UQ_Entities_CUIT not present on [dbo].[Entities] — nothing to drop, skipped.';
END

COMMIT TRANSACTION;

PRINT 'CUIT uniqueness on [dbo].[Entities] is per tenant (UQ_Entities_TenantId_CUIT).';
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the indexes, CHECKs or foreign keys below, nor the swap, nor the
-- backfill. These queries are the evidence for them.
--
-- 1. Classification per tenant and prefix. Expected: every 20 / 23 / 24 / 27 row
--    'Humana', every other row 'Juridica'. (After the deploy, an entity the API
--    classified from ARCA may differ on an unusual prefix; that is correct.)
--
--    SELECT TenantId, LEFT(CUIT, 2) AS Prefijo, PersonType, COUNT(*) AS Entities
--    FROM   [dbo].[Entities]
--    GROUP  BY TenantId, LEFT(CUIT, 2), PersonType
--    ORDER  BY 1, 2, 3;
--
-- 2. Every Humana has its legajo. Expected: 0.
--
--    SELECT COUNT(*) AS HumanaWithoutLegajo
--    FROM   [dbo].[Entities]
--    WHERE  PersonType     = N'Humana'
--      AND  PersonLegajoId IS NULL;
--
-- 3. Every entity and its legajo agree on tenant and CUIT. Expected: 0 rows.
--    A row here is an entity whose CUIT the OLD code changed through PUT after
--    this script had linked it (the old PUT does not know the legajo). Fix it by
--    hand: set its PersonLegajoId to NULL, and its PersonType to N'Juridica' as
--    well if the new CUIT starts 30 / 33 / 34, in one UPDATE; then re-run this
--    script, which links it to the right legajo.
--
--    SELECT e.EntityId, e.TenantId, p.PersonLegajoId
--    FROM   [dbo].[Entities] e
--    JOIN   [dbo].[PersonLegajos] p ON p.PersonLegajoId = e.PersonLegajoId
--    WHERE  p.TenantId <> e.TenantId
--       OR  p.CUIT     <> e.CUIT;
--
-- 4. The swap and the new indexes. Expected: exactly these four rows, and NO
--    UQ_Entities_CUIT, either as an index here or as a key constraint below.
--
--    SELECT OBJECT_NAME(i.object_id) AS TableName, i.name, i.is_unique,
--           i.has_filter, i.filter_definition
--    FROM   sys.indexes i
--    WHERE  i.object_id IN (OBJECT_ID(N'[dbo].[Entities]'),
--                           OBJECT_ID(N'[dbo].[Fiadores]'),
--                           OBJECT_ID(N'[dbo].[PersonLegajos]'))
--      AND  i.name IN (N'UQ_Entities_CUIT', N'UQ_Entities_TenantId_CUIT',
--                      N'UX_Entities_PersonLegajoId', N'IX_Fiadores_PersonLegajoId',
--                      N'UX_PersonLegajos_TenantId_CUIT')
--    ORDER  BY TableName, i.name;
--    -- expected: Entities      UQ_Entities_TenantId_CUIT       1  0  NULL
--    --           Entities      UX_Entities_PersonLegajoId      1  1  ([PersonLegajoId] IS NOT NULL)
--    --           Fiadores      IX_Fiadores_PersonLegajoId      0  1  ([PersonLegajoId] IS NOT NULL)
--    --           PersonLegajos UX_PersonLegajos_TenantId_CUIT  1  0  NULL
--
--    SELECT name, type_desc
--    FROM   sys.key_constraints
--    WHERE  parent_object_id = OBJECT_ID(N'[dbo].[Entities]');
--    -- expected: PK_Entities only. UQ_Entities_CUIT was the other key 001 declared.
--
-- 5. The four CHECKs and the two foreign keys exist, enabled and TRUSTED.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name,
--           is_disabled, is_not_trusted
--    FROM   sys.check_constraints
--    WHERE  name IN (N'CK_PersonLegajos_FiscalRegime', N'CK_Entities_PersonType',
--                    N'CK_Entities_FiscalRegime', N'CK_Entities_PersonLegajo_Humana');
--    -- expected: 4 rows, every one 0 0. is_not_trusted = 1 would mean the
--    -- WITH CHECK validation was skipped; investigate before deploying.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name,
--           is_disabled, is_not_trusted, delete_referential_action_desc
--    FROM   sys.foreign_keys
--    WHERE  referenced_object_id = OBJECT_ID(N'[dbo].[PersonLegajos]');
--    -- expected: Entities  FK_Entities_PersonLegajos  0  0  NO_ACTION
--    --           Fiadores  FK_Fiadores_PersonLegajos  0  0  NO_ACTION
--
-- 6. Nothing links a fiador yet. Expected in THIS release: 0. A non-zero count
--    means something is writing the column that should not be.
--
--    SELECT COUNT(*) AS LinkedFiadores
--    FROM   [dbo].[Fiadores]
--    WHERE  PersonLegajoId IS NOT NULL;
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps every column below
-- and the PersonLegajos table, so dropping them under it fails every read and
-- write of an entity with "Invalid column name". Run the rollback with the same
-- SET options as the script. Reverse order, and every constraint, index or
-- default goes BEFORE the column it sits on.
--
-- (a) CUIT uniqueness back to global. OPTIONAL, and often impossible. The old
--     code runs fine on per-tenant uniqueness (its pre-check is already
--     tenant-scoped), so the swap can simply stay. The global key can come back
--     only while no CUIT exists in two tenants, so this must return NOTHING:
--
--    SELECT CUIT, COUNT(*) AS Copies
--    FROM   [dbo].[Entities]
--    GROUP  BY CUIT
--    HAVING COUNT(*) > 1;
--
--     If it returns rows, each tenant registered that CUIT legitimately: do not
--     delete one to make room, leave the swap in place. Otherwise, in this order
--     so uniqueness never lapses:
--
--    ALTER TABLE [dbo].[Entities]
--        ADD CONSTRAINT [UQ_Entities_CUIT] UNIQUE NONCLUSTERED ([CUIT] ASC);
--    DROP INDEX [UQ_Entities_TenantId_CUIT] ON [dbo].[Entities];
--    GO
--
-- (b) Fiadores:
--
--    DROP INDEX [IX_Fiadores_PersonLegajoId] ON [dbo].[Fiadores];
--    ALTER TABLE [dbo].[Fiadores] DROP CONSTRAINT [FK_Fiadores_PersonLegajos];
--    ALTER TABLE [dbo].[Fiadores] DROP COLUMN [PersonLegajoId];
--    GO
--
-- (c) Entities:
--
--    ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [CK_Entities_PersonLegajo_Humana];
--    DROP INDEX [UX_Entities_PersonLegajoId] ON [dbo].[Entities];
--    ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [FK_Entities_PersonLegajos];
--    ALTER TABLE [dbo].[Entities] DROP COLUMN [PersonLegajoId];
--    ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [CK_Entities_FiscalRegime];
--    ALTER TABLE [dbo].[Entities] DROP COLUMN [FiscalRegime];
--    ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [CK_Entities_PersonType];
--    ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [DF_Entities_PersonType];
--    ALTER TABLE [dbo].[Entities] DROP COLUMN [PersonType];
--    GO
--
-- (d) The legajos, LAST (both foreign keys into it are gone by now). EXPORT FIRST:
--     the legajos hold data that exists nowhere else — the ARCA snapshot, regime
--     and category the API wrote for each person, and the BCRA snapshots
--     refreshed onto them since. ⚠ The export is PERSONAL DATA (a natural
--     person's CUIL, name and credit record); handle it accordingly.
--
--    SELECT [PersonLegajoId], [TenantId], [CUIT], [FullName], [FiscalRegime],
--           [MonotributoCategory], [ActivityStartDate], [MaritalStatus],
--           [PatrimonialRegime], [ArcaSnapshotJson], [ArcaDataUpdatedAt],
--           [BcraDataJson], [BcraDataUpdatedAt], [NosisDataJson],
--           [NosisDataUpdatedAt], [CreatedBy], [CreatedAt], [UpdatedAt]
--    FROM   [dbo].[PersonLegajos];
--
--     Then, only once the export is safely stored:
--
--    DROP TABLE [dbo].[PersonLegajos];
--    GO
--
-- What SURVIVES: every entity, with its own FiscalDataJson, BcraDataJson and the
-- rest of its snapshots, none of which this script wrote. What is lost: the
-- Humana/Juridica classification, the entity's regime, the category, and the
-- legajos themselves.
-- ============================================================================
