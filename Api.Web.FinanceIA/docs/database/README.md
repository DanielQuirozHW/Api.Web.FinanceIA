# Database Scripts — Api.Web.FinanceIA

## Approach
Database First exclusively. EF Core maps existing tables only.
Never run dotnet ef migrations add or dotnet ef database update.
All schema changes are managed via DDL scripts in this folder.

## Which database a script targets — read this first
This folder holds scripts for TWO different databases:

- **`NNN_*.sql` in this folder → a TENANT BUSINESS database**
  (`ConnectionStrings:FinanciaDefault`, mapped by `FinanciaDbContext`).
  The DDL procedure treats each tenant as its own database, so these are
  executed **once per tenant DB**.
- **`default-db/NNN_*.sql` → the SHARED "Default" database**
  (`ConnectionStrings:Default`, mapped by `AppDbContext`: `Credenciales`,
  `TenantFirebaseConfig`, `ErrorLogs`). Executed **once**, on the single shared
  DB. See `default-db/README.md`.

The two sequences are independent — `default-db/002` and `002` in this folder
are unrelated scripts against different databases.

The two unnumbered scripts below (`ErrorLog_AlterTraceId.sql`,
`TenantFirebaseConfig.sql`) also target the shared Default database; they
predate the split and stay where they are because they have already been run.
**New Default-DB work goes in `default-db/`, not here.**

## Execution Order
Run scripts in this exact order on a fresh environment:

1. ErrorLog_AlterTraceId.sql
   → Adds TraceId column to existing ErrorLogs table
   → Safe to run multiple times (checks column existence first)

2. TenantFirebaseConfig.sql
   → Creates TenantFirebaseConfig table
   → Safe to run multiple times (checks table existence first)

## Existing Tables (no script needed)
These tables already exist and are managed externally:

Credenciales
  → Master tenant registry
  → Contains Dynamics 365 OAuth credentials per tenant
  → Fields: id, conexion (obsolete), url, clientid,
            clientsecret, tenantid, cliente

ErrorLogs
  → API error log for operational review
  → Fields: ErrorId, Timestamp, Level, Message,
            ExceptionDetails, Source, Url, UserId,
            IPAddress, StackTrace, TraceId (added by script 1)

## Script Conventions
- Every script checks existence before creating/altering, using
  IF NOT EXISTS (SELECT 1 FROM sys.columns / sys.objects /
  sys.indexes / sys.check_constraints / sys.foreign_keys ...)
- Every script is safe to run multiple times (idempotent)
- Every guarded block ends with a PRINT saying what it did or
  that it was skipped, and is followed by GO
- Every script uses NVARCHAR never VARCHAR
- Every script uses DATETIME2 never DATETIME
- Sensitive columns (credentials, keys) always marked
  as AES-256 encrypted in column descriptions
- Numbered scripts use neither transactions nor BEGIN TRY /
  BEGIN CATCH: each guarded block is independently idempotent,
  so a partial run is re-runnable rather than needing rollback.
  The exceptions are 011_userinvitations_tenantid_to_nvarchar,
  013, 068, 069 and 070: where a partial run would leave a window,
  they wrap the multi-statement change (a column retype, a data
  change, a key swap) in an explicit transaction, with
  SET XACT_ABORT ON in all but 013

## Naming Convention for New Scripts
NNN_snake_case_description.sql — the next free number in the
sequence, then the table and the change. For a change to the SHARED
Default database, use the separate sequence in `default-db/` instead.

Examples:
  037_documents_add_sourceref.sql
  038_alert_rule_catalogue.sql
  039_analysisresults_add_enrichment_status.sql

The two unnumbered scripts below (ErrorLog_AlterTraceId.sql,
TenantFirebaseConfig.sql) predate this convention. Do not
follow their {TableName}_{Action}_{Date} shape for new work.

## Diagnostics (diagnostics/)
Read-only audit queries, NOT migrations. They perform SELECTs only — no DDL, no
data changes — and are re-run on demand rather than executed once in order. They are
deliberately NOT numbered into the NNN_ sequence so they never look like a pending
migration for the DBA.

- diagnostics/Documents_LufeFiscalYear_Audit_20260803.sql
  → Lists Lufe-imported documents whose stored FiscalYear disagrees with the year
    parsed from the document's own periodo in its entity's LufeDataJson. Sizes the
    data left mislabelled by the pre-#89 import bug (rows imported before the fix do
    not self-correct). Run per-tenant business DB ("FinanciaDefault").

## Before Executing on Production
1. Always execute on development environment first
2. Review script with DBA before production execution
3. Take database backup before executing on production
4. Never execute directly on production without DBA approval
5. Document execution in this README under Execution History

## Execution History
| Script                                        | Environment | Date       | Executed by |
|-----------------------------------------------|-------------|------------|-------------|
| ErrorLog_AlterTraceId.sql                     | Development |            |             |
| TenantFirebaseConfig.sql                      | Development |            |             |
| 038_alert_rule_catalogue.sql                  | Azure       |            |             |
| 039_analysisresults_add_enrichment_status.sql |             |            |             |
| 040_creditlineoperations_add_vigencia_audit.sql |           |            |             |
| 041_creditlineoperations_add_cancellation_audit.sql |       |            |             |
| 042_entities_add_nosisdatajson.sql            |             |            |             |
| 043_apicallogs_fix_tenantid_and_add_services.sql |          |            |             |
| 044_analysisresults_add_tenant_generatedat_index.sql |      |            |             |
| 045_entities_add_tenant_createdat_index.sql   |             |            |             |
| 046_fiadores_table.sql                        |             |            |             |
| 047_tenant_billing_config.sql                 |             |            |             |
| 048_alerts_entityid_nullable.sql              |             |            |             |
| 049_notifications_table.sql                   |             |            |             |
| 050_documents_add_extraction_attempts.sql     |             |            |             |
| 051_tenantbillingconfig_add_chatcontextbudget.sql |         |            |             |
| 052_tenantbillingconfig_add_chatenabledmodels.sql |         |            |             |
| 053_chatmessages_add_hadattachment.sql        |             |            |             |
| 054_mfa_backup_codes.sql                      |             |            |             |
| 055_chatmessages_add_attachmentmarkdown.sql   |             |            |             |
| 056_chatmessages_add_attachmentfilename.sql   |             |            |             |
| 057_creditlineoperations_add_condition_flags.sql |          |            |             |
| 058_analyses_add_preapproval_columns.sql      |             |            |             |
| 059_user_approval_limits.sql                  |             |            |             |
| 060_analyses_status_add_preapproved.sql       |             |            |             |
| 061_analyses_add_proposed_confirmed_operations.sql |        |            |             |
| 062_teams_table.sql                           |             |            |             |
| 063_users_add_teamid.sql                      |             |            |             |
| 064_team_managers_table.sql                   |             |            |             |
| 065_teams_add_approval_quorum.sql             |             |            |             |
| 066_migrate_manager_to_team_managers.sql      |             |            |             |
| 067_analysis_approval_votes_table.sql         |             |            |             |
| 068_scoring_config_v9_calibration.sql         |             |            |             |
| 069_person_legajos_and_entity_person_type.sql | Financia-db | 2026-09-28 | Juan Nasif  |
| 070_legajo_documents_and_extractions.sql      | Financia-db | 2026-09-29 | Juan Nasif  |
| 071_legajo_items_and_incomes.sql              |             |            |             |
| 072_person_analyses_and_participants.sql      |             |            |             |
| 073_analysis_rescores.sql                     |             |            |             |
| 074_fiadores_manual_edit.sql                  |             |            |             |
| 075_user_invitations_inviter_nullable.sql     |             |            |             |
| 076_users_role_admit_superadmin.sql           |             |            |             |
| 077_analysis_participants_fiador.sql          |             |            |             |
| 078_analysis_participants_agro_terms.sql      |             |            |             |
| 079_person_installments_and_cash_kind.sql     |             |            |             |
| 080_legajo_items_investment_class.sql         |             |            |             |
| 081_analysis_edits_tenantid.sql               |             |            |             |
| 082_scoring_v11_acid_test_floor.sql           |             |            |             |
| 083_notifications_add_keyset_index.sql        |             |            |             |
| 084_entities_businessname_strip_wrapping_quotes.sql |       |            |             |
| 085_documents_soft_delete_duplicate_pending.sql |           |            |             |
| 086_alert_rule_catalogue_seed_endeudamiento_elevado.sql |   |            |             |
| 087_entities_nosis_report.sql                 |             |            |             |

> `087_entities_nosis_report.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING** the
> release that maps it (NOSIS Manager report import) — in **EVERY** Financia (tenant business) database the
> DDL procedure lists — then **restart the App Service**. It adds three NULLABLE columns with no default to
> `[Entities]`: `[NosisReportJson]` (NVARCHAR(MAX) — the parsed company-level report; the PDF itself is never
> stored), `[NosisReportDate]` (DATETIME2 — the report's own footer date, Argentina wall-clock time) and
> `[NosisReportImportedAt]` (DATETIME2, UTC). EF maps them, so publishing first breaks every read and write
> that materialises an `[Entities]` row with "Invalid column name"; `--verify-schema` refuses that publish.
> DDL first is safe for the running code. Rationale and rollback: `docs/db-changes/087_entities_nosis_report.md`.

> `086_alert_rule_catalogue_seed_endeudamiento_elevado.sql` is a **DATA script (DML)**, like 084 and 085 — no
> schema change, **optional per tenant, not a deploy blocker**, and **safe before or after the deploy** (no
> restart). It seeds the `ENDEUDAMIENTO_ELEVADO` rule, which `AlertRuleEngine` implements but DDL 038 never
> seeded, for every tenant that already has catalogue rows: `Enabled = 0`, severity `high`, threshold `0.8500`
> (a placeholder), es-AR name and template. Disabled, it changes no analysis; the analyst enables it in
> Configuración. Run it once in **each** Financia database whose tenants should have it (`@Excluded` leaves one
> out): `@Apply = 0` (default) previews the tenants, `@Apply = 1` writes them in one transaction. Idempotent;
> bypasses the audit trail — record the run in the table above. Rationale:
> `docs/db-changes/086_alert_rule_catalogue_seed_endeudamiento_elevado.md`.

> `085_documents_soft_delete_duplicate_pending.sql` is a **DATA script (DML)**, like 084 — no schema change,
> **optional, not a deploy blocker**, and **safe before or after the deploy** (no restart). It soft-deletes
> (`IsDeleted = 1`, never a physical DELETE) the orphaned company documents a double submit left behind: a
> `Pending` document with no analysis and no legajo, that has a non-deleted sibling with the same TenantId,
> EntityId, DocumentType and FileHashSHA256 uploaded within 120 s which outranks it (has an analysis, then is
> not Pending, then was uploaded first). The API now answers such a repeat upload with the first document, so
> no new ones appear. Run it once in **each** Financia database: `@Apply = 0` (default) previews result set 1,
> `@Apply = 1` writes it in one transaction. Idempotent; bypasses the audit trail — record the run in the table
> above. Rationale: `docs/db-changes/085_documents_soft_delete_duplicate_pending.md`.

> `084_entities_businessname_strip_wrapping_quotes.sql` is a **DATA script (DML)**, like 066, 068 and 082 —
> no schema change, **optional**, and **safe before or after the deploy** (no restart). It strips the quotes
> that wrap a stored `Entities.BusinessName` (`"silo Chico S.A."` → `silo Chico S.A.`), which the API now
> normalizes on create/update. Run it once in **each** Financia database: with `@Apply = 0` (default) it only
> previews — result set 1 is what it would fix, result set 2 needs a person — then set `@Apply = 1` to write
> result set 1 in one transaction. Idempotent; leaves `UpdatedAt` alone; bypasses the audit trail — record the
> run in the table above. Rationale: `docs/db-changes/084_entities_businessname_strip_wrapping_quotes.md`.

> `083_notifications_add_keyset_index.sql` is **INDEX ONLY** — no table or column change, so it has **no
> deploy order**: EF does not depend on indexes and `--verify-schema` checks tables and columns only. Run it in
> every Financia database, before or after the release that pages `GET /api/notifications`; no restart. Without
> it the listing is still correct, just a sort instead of a seek. It keeps 049's
> `IX_Notifications_TenantId_UserId_CreatedAt`, now redundant. Rationale:
> `docs/db-changes/083_notifications_add_keyset_index.md`.

> `082_scoring_v11_acid_test_floor.sql` is a **DATA script (DML)**, like 066 and 068 — no schema change, so
> the API does not depend on it, `--verify-schema` neither needs nor sees it, and no restart is needed. **Code
> first is safe:** scoring algorithm 11 lowered the DEFAULT `critical_floor_prueba_acida` 60 → 50 (and applies
> that floor only while liquidez corriente is below its warn threshold), so a tenant with no row already scores
> on 50; the script moves a row still at exactly 60 to 50 and leaves any other value alone. Run it once per
> tenant with `@TenantId` set (the `N'<tenant>'` placeholder THROWs). It bypasses the Settings audit trail —
> record the run in the table above. The new switch `critical_floor_acid_test_requires_weak_current_ratio`
> needs no row (code default 1). Rationale: `docs/db-changes/082_scoring_v11_acid_test_floor.md`.

> `081_analysis_edits_tenantid.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING** the
> release that maps it (Phase 10-F3a, manual correction of extracted balance figures) — in **EVERY** Financia
> (tenant business) database the DDL procedure lists, after 080 — then **restart the App Service**.
> `[AnalysisEdits]` has existed since 001 but was never mapped, and 005 did not migrate it, so its `[TenantId]`
> is still UNIQUEIDENTIFIER. The script CREATEs the table when it is missing (TenantId NVARCHAR(100)); otherwise
> it retypes `[TenantId]` to NVARCHAR(100) NOT NULL — only while it is still uniqueidentifier, and only on an
> EMPTY table (it RAISEs and changes nothing when rows exist: report the row count first, it should be 0) —
> dropping `IX_AnalysisEdits_TenantId_AnalysisId`, which SQL Server requires before the ALTER, and creating
> `IX_AnalysisEdits_TenantId_AnalysisId_EditedAt` (TenantId, AnalysisId, EditedAt DESC). Every name already
> matches before 081, so `--verify-schema` ALSO checks this one column's TYPE and refuses the publish while it
> is uniqueidentifier; it does not see the index — run the script's post-check. DDL first is safe for the
> running code, which never touches the table. Rationale and rollback: `docs/db-changes/081_analysis_edits_tenantid.md`.

> `080_legajo_items_investment_class.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING**
> the release that maps it (persona humana engine 0.9, investment classes) — in **EVERY** Financia (tenant
> business) database the DDL procedure lists, after 079 — then **restart the App Service**. It adds one NULLABLE
> column with no default to `[LegajoItems]`: `[InvestmentClass]` (NVARCHAR(30) — TituloPublico, AccionCotizante,
> ObligacionNegociable, CuotaparteFci or Otra on an Inversion item; NULL = not classified), plus
> `CK_LegajoItems_InvestmentClass` (NULL, or a known class on a row of Kind Inversion). EF maps the column, so
> publishing first breaks every read and write of `[LegajoItems]` (the legajo inventory and the persona analysis)
> with "Invalid column name"; `--verify-schema` refuses that publish but does NOT see the CHECK — run the
> script's post-check. DDL first is safe for the running code. Rationale and rollback:
> `docs/db-changes/080_legajo_items_investment_class.md`.

> `079_person_installments_and_cash_kind.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING**
> the release that maps it (persona humana engine 0.8, declared installments) — in **EVERY** Financia (tenant
> business) database the DDL procedure lists — then **restart the App Service**. It adds one NULLABLE column
> with no default to `[AnalysisParticipants]`: `[ExistingInstallmentsAmount]` (DECIMAL(18,2) — the monthly
> cuotas vigentes the analyst declared; NULL = estimate), plus `CK_AnalysisParticipants_ExistingInstallmentsAmount`
> (NULL or ≥ 0), and widens `CK_LegajoItems_Kind` with `Efectivo` (Bienes Personales cash). EF maps the column,
> so publishing first breaks every read and write of `[AnalysisParticipants]` (the persona analysis) with
> "Invalid column name"; `--verify-schema` refuses that publish but does NOT see the widened CHECK, whose
> absence fails the reconciliation of a legajo declaring cash — run the script's post-check. DDL first is safe
> for the running code. Rationale and rollback: `docs/db-changes/079_person_installments_and_cash_kind.md`.

> `078_analysis_participants_agro_terms.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING**
> the release that maps it (persona humana engine 0.7, agro annual mode) — in **EVERY** Financia (tenant
> business) database the DDL procedure lists — then **restart the App Service**. It adds two NULLABLE columns
> with no default to `[AnalysisParticipants]`: `[CuotasPorAnio]` (TINYINT — how many cuotas a year the
> solicitante's cuota is) and `[AgroModeOverride]` (BIT — the analyst's agro mode override), plus
> `CK_AnalysisParticipants_CuotasPorAnio` (NULL or 1, 2, 4, 12) and `CK_AnalysisParticipants_AgroTerms_Role`
> (a Fiador row carries neither). EF maps both columns, so publishing first breaks every read and write of
> `[AnalysisParticipants]` (the persona analysis) with "Invalid column name"; `--verify-schema` refuses that
> publish. DDL first is safe for the running code. Rationale and rollback:
> `docs/db-changes/078_analysis_participants_agro_terms.md`.

> `077_analysis_participants_fiador.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING**
> the release that maps it — in **EVERY** Financia (tenant business) database the DDL procedure lists — then
> **restart the App Service**. It adds the fiador columns of `[AnalysisParticipants]`, all NULLABLE with no
> default: `[FiadorId]` (UNIQUEIDENTIFIER, FK to `[Fiadores]`, NO ACTION), `[GuaranteeType]` (NVARCHAR(30)),
> `[SpouseConsent]` and `[HousingInopposable]` (BIT), `[GuaranteesGivenAmount]` (DECIMAL(18,2)), plus
> `CK_AnalysisParticipants_FiadorId_Role` (Fiador ⇒ FiadorId set, Solicitante ⇒ FiadorId NULL) and
> `CK_AnalysisParticipants_GuaranteesGivenAmount` (NULL or ≥ 0). EF maps the five columns, so publishing
> first breaks every read and write of `[AnalysisParticipants]` (the persona analysis) with "Invalid column
> name"; `--verify-schema` refuses that publish. DDL first is safe for the running code. The same release
> also writes `[Fiadores].[PersonLegajoId]` (DDL 069, no change). Rationale and rollback:
> `docs/db-changes/077_analysis_participants_fiador.md`.

> `076_users_role_admit_superadmin.sql` is **PENDING** and **NOT a deploy-blocker** — run it in **EVERY**
> Financia (tenant business) database the DDL procedure lists, before the DBA creates or promotes a
> SuperAdmin user row. It widens `[Users]` CHECK `[CK_Users_Role]` to admit `SuperAdmin` (001 only admitted
> the four client roles, so the role could not be held). `[CK_UserInvitations_Role]` is deliberately NOT
> widened: a SuperAdmin is granted by the DBA, never invited. EF maps no check constraint on `[Users]`, so the
> API may be published before or after; `--verify-schema` cannot see constraints — check the definition with
> the query at the end of the script. No restart needed. Rationale and rollback:
> `docs/db-changes/076_users_role_admit_superadmin.md`.

> `075_user_invitations_inviter_nullable.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING**
> the release that adds `POST /api/bff/setup/first-admin` — in **EVERY** Financia (tenant business) database
> the DDL procedure lists. It makes `[UserInvitations].[InvitedByUserId]` NULLABLE (type and FK unchanged) so
> the first administrator of an empty database can be invited with no inviter. Publishing first breaks
> nothing that works today, but the bootstrap insert fails with "Cannot insert the value NULL" — and
> `--verify-schema` does NOT catch it, because it compares names, not nullability. Check `is_nullable = 1`
> with the query at the end of the script. No restart needed. Rationale and rollback:
> `docs/db-changes/075_user_invitations_inviter_nullable.md`; operator guide: `docs/guides/first-admin.md`.

> `074_fiadores_manual_edit.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING** the
> release that maps it — on Financia-db and on any other business database the DDL procedure lists —
> then **restart the App Service**. It adds `[Fiadores].[UpdatedAt]` (DATETIME2 NULL) and
> `[UpdatedByUserId]` (UNIQUEIDENTIFIER NULL, no FK): when and by whom a fiador was last edited
> (manual add and edit of fiadores). EF maps both, so publishing first breaks every read and write of
> `[Fiadores]` with "Invalid column name"; `--verify-schema` refuses that publish. DDL first is safe for
> the running code. Rationale and rollback: `docs/db-changes/074_fiadores_manual_edit.md`.

> `073_analysis_rescores.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING** the
> release that maps it — on Financia-db and on any other business database the DDL procedure lists —
> then **restart the App Service**. It adds `[AnalysisResults].[RescoredAt]` (DATETIME2 NULL),
> `[RescoredBy]` (UNIQUEIDENTIFIER NULL) and `[RescoreCount]` (INT NOT NULL, `DF_AnalysisResults_RescoreCount`
> DEFAULT 0 — also the optimistic concurrency token of the write), and creates the append-only
> `[dbo].[AnalysisRescores]` (one row per "Reanalizar con datos actualizados": score, level and enrichment
> status before and after, the previous value of every overwritten column, the source dates;
> `FK_AnalysisRescores_AnalysisId`, two `ISJSON` CHECKs, `IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt`
> with `RescoredAt DESC`). EF maps the three columns, so publishing first breaks every read and write of
> `[AnalysisResults]` — the list, the detail, the chat, the PDF, the pipeline — with "Invalid column name";
> `--verify-schema` refuses that publish. DDL first is safe for the running code. Rationale and rollback:
> `docs/db-changes/073_analysis_rescores.md`.

> `072_person_analyses_and_participants.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE
> DEPLOYING** the release that maps it — on Financia-db and on any other business database the DDL
> procedure lists — then **restart the App Service**. It adds `[Analyses].[AnalysisKind]` (NOT NULL,
> default `Balance`, `CK_Analyses_AnalysisKind`), makes `[Analyses].[DocumentId]` NULLABLE with
> `CK_Analyses_DocumentId_Kind` (NULL only on a `PersonaHumana` analysis; the script stops with an error
> instead of dropping an index it did not expect on that column), adds `[Analyses].[PreApprovalChecksJson]`
> and `[ConfirmationChecksJson]` (NVARCHAR(MAX) NULL, each with an `ISJSON` CHECK: the confirmations of a
> persona approval, per signer), and creates
> `[dbo].[AnalysisParticipants]` (the persona humana evaluation of M10 Phase 4a, frozen per analysis).
> EF maps the new columns and table, so publishing first breaks every read and write of `[Analyses]`
> with "Invalid column name"; `--verify-schema` refuses that publish, but it does NOT check
> nullability — post-check 1 is the evidence that `DocumentId` accepts NULL. DDL first is safe for the
> running code. Rationale and rollback: `docs/db-changes/072_person_analyses_and_participants.md`.

> `071_legajo_items_and_incomes.sql` is **PENDING** and a **deploy-blocker: RUN IT BEFORE DEPLOYING**
> the release that maps it — on Financia-db and on any other business database the DDL procedure
> lists — then **restart the App Service**. It creates `[dbo].[LegajoItems]` (a legajo's reconciled
> inventory: market and fiscal value side by side, reconciliation status, analyst edits) and
> `[dbo].[LegajoIncomes]` (incomes and sales by period and source), and adds the nullable
> `ReviewOverride` and `ReviewReason` to `[dbo].[LegajoExtractions]`. EF maps both tables and both
> columns, so publishing first breaks every read of `[LegajoExtractions]` with "Invalid column name";
> `--verify-schema` refuses that publish. DDL first is safe for the running code (two new tables it
> never reads, two nullable columns). Nothing is backfilled: the first approval, reopening correction,
> extraction or `POST /api/person-legajos/{id}/reconciliation/rebuild` of each legajo builds its rows.
> Rationale and rollback: `docs/db-changes/071_legajo_items_and_incomes.md`.

> `070_legajo_documents_and_extractions.sql` was executed on Financia-db on 2026-09-29 (see the
> table). **It is a deploy-blocker: RUN IT BEFORE DEPLOYING** — done on Financia-db; run it on any
> other business database the DDL procedure lists — then **restart the App Service**. It lets a persona
> humana's legajo receive typed documents (M10 Phase 2a): it widens `CK_Documents_DocumentType` with
> the ten legajo types and `CK_Documents_PdfType` with `Mixed` (dropping every check BOUND to those two
> columns, whatever its name, and never a table-level one), adds eight nullable columns to
> `[dbo].[Documents]` (`PersonLegajoId`, `PeriodEnd`, `EngagementType`, `SupersedesDocumentId`,
> `HolderCuit`, `LegajoStatus`, `LegajoStatusUpdatedAt`, `PageQualityJson`) with their CHECKs, FKs, a
> filtered index and the filtered UNIQUE `UX_Documents_Legajo_Hash` (same file once per legajo among
> live rows; the API maps its violation by name to 409 — pre-check 4 must return 0 rows), the consent triple to `[dbo].[PersonLegajos]` (an FK cycle with Documents, both ends
> nullable), `DocumentId` to `[dbo].[ApiCallLogs]`, and creates `[dbo].[LegajoExtractions]` (one
> immutable row per model run, filtered `UX_LegajoExtractions_Current`) and `[dbo].[LegajoCorrections]`
> (Phase 2b, append-only). EF maps all of it, so publishing first fails every read and write of a
> document with `Invalid column name`; the old code keeps working once the script has run. Same SET
> options as 069 (`sqlcmd -I`). Idempotent. Its last batch widens `CK_ApiCallLogs_Operation` where
> the database has one: that CHECK was created **out of band** (no numbered script made it) and did
> not admit the legajo operations. It is recreated as a superset: the legacy `DynamicsSync` and
> `BcraLookup` (written by no code here; another application may write them), every value in
> `ApiCallLogOperations.Known` (`Extraction`, `Narrative`, `Chat`, `LegajoClassification`,
> `LegajoExtraction`) and NULL. A database with no CHECK on `[Operation]` gets none. **Financia-db
> history:** the 2026-09-29 run applied sections 1–6 and stopped on the old section 7, a `RAISERROR`
> guard that refused the out-of-band CHECK; the DBA then recreated `CK_ApiCallLogs_Operation` by hand
> with exactly that list (same drop loop, `WITH CHECK`, one transaction). **Financia-db needs no
> further DDL**; re-running the current 070 there only rewrites that CHECK with the same list. Other
> databases get the widening from 070 itself. Rationale, post-checks and rollback:
> `docs/db-changes/070_legajo_documents_and_extractions.md`.

> `069_person_legajos_and_entity_person_type.sql` was executed on Financia-db on 2026-09-28 (see the
> table). It is the deploy-blocker for the #237 API: that publish must follow it, then an App Service
> restart. It lets an entity be a **persona humana**: it creates
> `[dbo].[PersonLegajos]` (the legajo of a natural person, one row per `(TenantId, CUIT)` under
> `UX_PersonLegajos_TenantId_CUIT`), adds `PersonType` (`NVARCHAR(10) NOT NULL DEFAULT 'Juridica'`),
> `FiscalRegime` and `PersonLegajoId` to `[dbo].[Entities]` and `PersonLegajoId` to
> `[dbo].[Fiadores]`, with four CHECKs, two foreign keys into the new table and two filtered indexes.
> EF maps every one of those columns and the table, so publishing first fails every read and write
> of an entity with `Invalid column name`; the old code, by contrast, keeps working once the script
> has run. **SET options:** the script opens with `SET ANSI_NULLS ON` and `SET QUOTED_IDENTIFIER ON`,
> because `001_initial_schema` left both database defaults OFF and a filtered index can be neither
> created nor written through without them (error 1934). Run it in SSMS/ADS or `sqlcmd -I`; hand-made
> DML on `[Entities]` from a session with either option OFF fails the same way from now on. **The
> backfill**, in one transaction: entities whose CUIT starts 20/23/24/27 become `Humana`, get
> `FiscalRegime = 'Desconocido'` and a legajo each (regime `'Desconocido'`, the entity's BCRA snapshot
> copied, `CreatedBy` NULL), and are linked to it. **The swap**, last and in its own transaction,
> moves CUIT uniqueness from global to per tenant (`UQ_Entities_CUIT` → `UQ_Entities_TenantId_CUIT`).
> That fixes a live bug: the business database is shared, so today a CUIT another tenant already
> registered makes `POST /api/entities` return 500. If a foreign key references the old key, the swap
> rolls itself back and raises an error, and everything before it stays applied. Idempotent, and
> **re-run it right after the publish**: entities the old code creates between the DDL and the deploy
> get `'Juridica'` whatever their prefix, and a re-run backfills them. `--verify-schema` checks only
> that tables and columns exist; it sees neither the swap, the CHECKs, the foreign keys nor the
> backfill, so confirm those with the script's commented post-check. Rationale, rollback and the
> tenant checklist: `docs/db-changes/069_person_legajos_and_entity_person_type.md`.

> `068_scoring_config_v9_calibration.sql` is a **DATA script (DML)**, like 066 — it changes no
> schema, so the API does not depend on it and the v9 deploy is safe before, after or without it.
> Scoring algorithm version 9 moved three DEFAULTS (`critical_floor_trigger_threshold` 85 → 100,
> `threshold_leverage_warn` 1.50 → 2.33, `threshold_leverage_danger` 2.50 → 5.67), and a default
> reaches only the keys a tenant has no row for. So there are three states: **no rows** → the score
> changes at deploy; **full old rows** (trigger 85 + leverage 1.0/1.5/2.5, the sandbox via 007/014) →
> no change until this runs; **mixed** (leverage rows but no trigger row — the Settings page never
> exposes the trigger) → the trigger moves to 100 at deploy while leverage stays 1.50/2.50 until this
> runs. Details in the db-changes note below. It updates only rows still at the exact old default,
> moves leverage as a pair, and never touches `__sector__` rows. **Run it once per tenant, with
> `@TenantId` set, only after that tenant's analyst confirms** — it changes scores from then on. The
> database is shared (row-level tenancy), so `@TenantId` is what picks the tenant; left at its
> `N'<tenant>'` placeholder the script THROWs before touching anything, so it cannot be run by
> accident as a pending numbered script. Its query 0 is the diagnostic SELECT that says whether a
> tenant needs it. Rationale: `docs/db-changes/068_scoring_config_v9_calibration.md`.
>
> `038_alert_rule_catalogue.sql` HAS been executed in Azure. (An earlier version of this note said it
> had not been run anywhere — that was stale, and it was dangerous: it invited a fresh environment to
> skip the DDL, after which the API fails with "Invalid column name" on `AnalysisResults`.)

> `039_analysisresults_add_enrichment_status.sql` has NOT been executed yet, and must be run BEFORE
> the matching application deployment, followed by an App Service restart. It adds
> `AnalysisResults.EnrichmentStatus` (NOT NULL) and `EnrichmentNotes`, both of which EF Core maps and
> therefore emits in every SELECT and INSERT against that table — deploying the code first breaks
> analysis creation and retrieval. See `docs/db-changes/039_analysisresults_add_enrichment_status.md`,
> which also explains why the optional historical backfill in the script is left commented out.

> `040_creditlineoperations_add_vigencia_audit.sql` has NOT been executed yet, and must be run BEFORE
> the matching application deployment, followed by an App Service restart. It adds
> `CreditLineOperations.VigenteConfirmadaEl` / `VigenteConfirmadaPor` (both nullable, so nothing needs
> backfilling) and moves the `Status` default from `'Vigente'` to `'Aprobada'`. Both columns are
> EF-mapped, so deploying the code first breaks operation creation and retrieval with "Invalid column
> name". **The Portal must be deployed before this API**: its operation-status renderer has no branch
> for the new `Aprobada` value. See
> `docs/db-changes/040_creditlineoperations_add_vigencia_audit.md`, which also covers why rolling the
> schema back does not undo the `Aprobada` rows.

> `041_creditlineoperations_add_cancellation_audit.sql` has NOT been executed yet, and must be run
> BEFORE the matching application deployment, followed by an App Service restart. It adds
> `CreditLineOperations.CanceladaEl` / `CanceladaPor` (both nullable, so nothing needs backfilling),
> the mirror of what 040 added for vigencia. Both columns are EF-mapped, so deploying the code first
> breaks operation creation and retrieval with "Invalid column name". **Unlike 040, the Portal does
> not have to be deployed first**: no new status value is introduced, only an optional `canceladaEl`
> read field the Portal may ignore. See
> `docs/db-changes/041_creditlineoperations_add_cancellation_audit.md`, which also explains why the
> conditional UPDATE is guarded on the exact prior status rather than on "not already cancelled".

> `042_entities_add_nosisdatajson.sql` has NOT been executed yet, and must be run BEFORE the matching
> application deployment, followed by an App Service restart. It adds `Entities.NosisDataJson` and
> `Entities.NosisDataUpdatedAt` (both nullable, so nothing needs backfilling). Both are mapped in
> `EntityConfiguration`, so deploying the code first breaks entity creation and retrieval with
> "Invalid column name". `AnalysisResults.NosisDataJson` already exists from script 026 and is not
> touched. See `docs/db-changes/042_entities_add_nosisdatajson.md`.

> `043_apicallogs_fix_tenantid_and_add_services.sql` has NOT been executed yet. It repairs two
> long-standing defects that have kept `[dbo].[ApiCallLogs]` empty: `TenantId` is still
> `UNIQUEIDENTIFIER` (script 005 converted the four business tables but not this one) while the
> application writes Firebase tenant slugs, and `CK_ApiCallLogs_Service` never gained `'LUFE'`, which
> `LufeService` has been writing since it shipped. Both failures are swallowed by
> `ApiCallLogService`'s fire-and-forget catch, so nothing surfaced. Unlike 042 this script is not a
> deploy-blocker — without it the table simply stays as empty as it already is — but run it first so
> the NOSIS release is observable from its first call. See the same db-changes note as 042.

> `044_analysisresults_add_tenant_generatedat_index.sql` has NOT been executed yet. It adds
> `IX_AnalysisResults_TenantId_GeneratedAt` on `(TenantId ASC, GeneratedAt DESC, ResultId ASC)`;
> `[dbo].[AnalysisResults]` had no nonclustered index at all before it, not even on `TenantId`. It
> supports the paged list query `GET /api/analyses` now issues (`ORDER BY GeneratedAt DESC, ResultId`
> + `OFFSET/FETCH`), turning a tenant scan plus sort into a seek plus a page-sized key lookup. Like
> 043 and unlike 039-042 it is **not** a deploy-blocker: it adds no column, EF's model does not
> depend on it, and the endpoint is correct without it — only slower. Run it before publishing the
> code anyway, per the convention above, so the release is fast from its first request. **No App
> Service restart required.** Built `WITH (ONLINE = ON)`; drop that clause on an edition without
> online index builds. See `docs/db-changes/044_analysisresults_add_tenant_generatedat_index.md`.

> `045_entities_add_tenant_createdat_index.sql` has NOT been executed yet. It adds
> `IX_Entities_TenantId_CreatedAt` on `(TenantId ASC, CreatedAt DESC, EntityId ASC)`. `IX_Entities_TenantId`
> already exists but cannot order a page — nothing indexes `CreatedAt` — so the paged list query
> `GET /api/entities` now issues (`ORDER BY CreatedAt DESC, EntityId` + `OFFSET/FETCH`) would seek on
> tenant and then sort the whole tenant. Like 043 and unlike 039-042 it is **not** a deploy-blocker:
> it adds no column, EF's model does not depend on it, and the endpoint is correct without it — only
> slower. Run it before publishing the code anyway, per the convention above, so the release is fast
> from its first request. **No App Service restart required.** Note it makes `IX_Entities_TenantId` a
> redundant prefix; dropping that one is a separate DBA decision and this script does not do it.
> Built `WITH (ONLINE = ON)`; drop that clause on an edition without online index builds. See
> `docs/db-changes/045_entities_add_tenant_createdat_index.md`.

> `048_alerts_entityid_nullable.sql` has NOT been executed yet. It makes `[dbo].[Alerts].[EntityId]`
> nullable, so the table can hold SYSTEM-LEVEL alerts — tenant-scoped, but tied to no PyME and no
> analysis. The first consumer is the billing quota alert (80% / 100% of the monthly plan), which is
> about the tenant's contract rather than any company it analysed. The application already models the
> column this way: `Alert.EntityId` is `Guid?`, `AlertConfiguration` maps it without `.IsRequired()`,
> and `AlertsController` renders `EntityName = "Sistema"` when it is null — the database is the only
> layer still saying `NOT NULL`. Because `EntityId` is a key column of `IX_Alerts_TenantId_EntityId`,
> the script drops that index, alters the column and recreates it to its `001_initial_schema`
> definition — but **only if it found it**, since script 013 implies the live index set on this table
> may already differ from 001. It adds no index and removes none. Unlike 039-042 it is **not** a
> deploy-blocker in the "Invalid column name" sense: it adds no column, EF's model does not change,
> and nothing that works today starts failing. What it blocks is the new feature only — until it runs,
> the emission path's `EntityId = NULL` insert is rejected, and `BillingAlertService` catches that,
> logs a Warning and returns, so quota alerts simply never appear. Run it before publishing the code
> anyway, per the convention above, so the feature works from its first request. **No App Service
> restart required** — EF's model cache already describes this column as nullable.

> `047_tenant_billing_config.sql` was missing from the Execution History table above and has been
> added. Its absence was a bookkeeping gap in this README, not evidence about the script: the row is
> listed with the same blank Environment/Date as its neighbours because whether it has been run is
> **unknown from the repository**. Confirm against the target database before assuming either way —
> `[dbo].[TenantBillingConfig]` is mapped by `TenantBillingConfigConfiguration` and read on the
> billing path, so if it is genuinely absent that path fails on a missing table rather than
> degrading.

> `049_notifications_table.sql` has NOT been executed yet. It creates `[dbo].[Notifications]`, the
> new PER-USER delivery layer that sits beside — and does not alter — the per-tenant `[dbo].[Alerts]`.
> Each row is addressed to one `[dbo].[Users].[UserId]` and carries its own `IsRead`/`ReadAt`, which
> is what gives each analyst an independent unread badge. The shape is generic on purpose: `[Type]`
> is an open code and `[SourceType]`/`[SourceId]` point back at whatever produced the row (`'Alert'`
> plus the `AlertId` in v1), so later emitters are new `[Type]` values rather than schema changes.
> `[TenantId]` is `NVARCHAR(100)`, matching every other business table since script 005 — it holds a
> Firebase tenant slug, never a GUID. Adds `FK_Notifications_Users` (following the precedent in
> `027_notes_table.sql`) and two `(TenantId, UserId, ...)` indexes covering the list and the unread
> count. **This one IS a deploy-blocker**, unlike 048: it creates a table that EF Core maps
> unconditionally, so publishing the code first makes `GET /api/notifications`, the read PATCH and
> `mark-all-read` fail on every request with `Invalid object name 'dbo.Notifications'`. The billing
> emission path degrades gracefully by comparison — `BillingAlertService` fans notifications out
> inside its own try/catch, so until the DDL runs the alerts are still written and `GET
> /api/billing/usage` still returns 200, with only a Warning logged. Run this script on **every**
> tenant database before publishing, then **restart the App Service** so EF's model cache is built
> against the table that now exists.

> `050_documents_add_extraction_attempts.sql` has NOT been executed yet. It adds
> `[ExtractionAttempts]` (`INT NOT NULL DEFAULT (0)`), `[LastExtractionFailedAt]` and
> `[LastExtractionError]` to `[dbo].[Documents]`, so a balance whose extraction keeps failing stops
> buying Claude calls. The monthly analysis quota counts rows in `[dbo].[Analyses]`, and that row is
> written only after Claude answers — so a failed extraction is correctly free, and nothing else
> stood between an unreadable PDF and an unlimited retry loop of real billable extractions. Nothing
> already in the schema could count those attempts: re-triggering the same document inserts no new
> row, so `[ProcessingStatus]` says only "failed at least once" and never how many times. The budget
> is spent per FILE — the application sums the counter across the tenant's non-deleted documents
> sharing a `[FileHashSHA256]`, so re-uploading the identical bytes inherits it instead of resetting
> it. `[ProcessingStatus]` gains **no** new value and no response shape changes, so no Portal deploy
> is needed. **This one IS a deploy-blocker**: all three columns are mapped in
> `DocumentConfiguration`, so EF Core emits them in every `SELECT` and `INSERT` against
> `[dbo].[Documents]` — publishing the code first breaks document upload, the documents list and
> every analysis trigger with `Invalid column name`. Run it on **every** tenant database before
> publishing, then **restart the App Service**. Idempotent; re-running never resets a counter
> already written. Rationale and rollback: `docs/db-changes/050_documents_add_extraction_attempts.md`.

> `051_tenantbillingconfig_add_chatcontextbudget.sql` has NOT been executed yet. It adds
> `[ChatContextBudgetTokens]` (`INT NOT NULL DEFAULT (40000)`) to `[dbo].[TenantBillingConfig]`: the
> token ceiling for a SINGLE chat conversation's context, a sibling of the two monthly limits script
> 047 created and orthogonal to both. Those are tenant-wide pools for a calendar month; this bounds
> how large one prompt may get, which nothing does today — the chat rebuilds a conversation's history
> from every `[dbo].[ChatMessages]` row of that conversation and re-sends the lot on every turn.
> `INT` rather than the `BIGINT` 047 chose next door, deliberately: a monthly token pool passes
> `int.MaxValue`, a single conversation's context cannot (the chat model's window is 200.000 tokens).
> The default backfills every existing row to `BillingPlans.Minimum`'s figure in the `ALTER` itself,
> so there is no data migration — a row that predates this script gets exactly what a tenant with no
> row gets. The figures (40.000 / 60.000 / 80.000 across the three tiers) are **placeholders**, per
> the `TODO(billing)` marker in `BillingPlans.cs`. **This one IS a deploy-blocker**, and quietly:
> the column is mapped in `TenantBillingConfigConfiguration`, so EF Core emits it in every `SELECT`
> and `INSERT` against the table, and `TenantBillingConfigService.GetEffectiveAsync` *catches* the
> resulting `Invalid column name` — publishing first therefore does not error, it silently demotes
> every tenant to the minimum plan with its quotas failing open. Run it on **every** tenant database
> before publishing, then **restart the App Service**. No Portal deploy needed: two additional JSON
> fields, no new enum value. Nothing enforces the budget in this delivery — trimming a prompt against
> it is a later PR. Idempotent; re-running never overwrites a budget already set. Rationale and
> rollback: `docs/db-changes/051_tenantbillingconfig_add_chatcontextbudget.md`.

> `052_tenantbillingconfig_add_chatenabledmodels.sql` has NOT been executed yet. It adds
> `[ChatEnabledModels]` (`NVARCHAR(400) NOT NULL DEFAULT
> (N'claude-haiku-4-5,claude-sonnet-4-6,claude-sonnet-5')`) to `[dbo].[TenantBillingConfig]`: the
> Claude chat models a tenant may use, as a comma-separated list of model ids. Until this delivery the
> chat model was a hardcoded constant, identical for every tenant. **Unlike the three columns beside
> it, this is not an allowance** — nothing is consumed against it, there is no running total, and
> `BillingQuotaFilter` ignores it. It is the set of models the analyst's per-conversation picker may
> offer; which one answered a given turn is recorded per-turn on `[dbo].[ChatMessages].[Model]`.
> **The default is deliberately the FULL catalogue, not the smallest option** — the inverse of 051's
> posture, because a tenant that predates this column already had a working chat and a subset would
> take a capability away as a side effect of a schema change. The `ALTER` backfills every existing row
> in place, so there is no data migration. There is **no `CHECK` constraint and cannot be one**: the
> catalogue of valid ids lives in `Models/Financia/ChatModelCatalog.cs`, so an id misspelled in
> hand-written SQL is accepted by the database and silently dropped by the application on read —
> prefer `PUT /api/billing/config`, which validates each id and answers 400 naming a bad one.
> **This one IS a deploy-blocker, quietly, in exactly the way 051 is**: the column is mapped, so EF
> emits it in every `SELECT` against the table — now including one on every chat turn — and
> `TenantBillingConfigService.GetEffectiveAsync` *catches* the resulting `Invalid column name`.
> Publishing first would not error; it would silently demote every tenant to the minimum plan, and the
> visible symptom would be **quota refusals on paying tenants, not a chat outage** (a degraded
> resolution still carries all three models), so the cause would not be obvious. Run it on **every**
> tenant database before publishing, then **restart the App Service**. No Portal deploy needed for
> this PR: the API gains two JSON fields and one optional request field, no new enum value, and the
> Portal's model selector is a separate later delivery. Idempotent; re-running never overwrites a list
> already set. Rationale and rollback:
> `docs/db-changes/052_tenantbillingconfig_add_chatenabledmodels.md`.

> `053_chatmessages_add_hadattachment.sql` has NOT been executed yet. It adds
> `[HadEphemeralAttachment]` (`BIT NOT NULL DEFAULT (0)`) to `[dbo].[ChatMessages]`: one bit per turn
> saying whether that turn carried an ephemeral chat attachment. It is **not** an allowance and not a
> size — nothing is consumed against it and no total is kept. It exists because the context gate from
> 051 reads `[InputTokens]`, which is Anthropic's count for the WHOLE request *including* an
> attachment that is never replayed, because it stays out of `[Content]` whatever else stores it
> (cap ~67k tokens against a 40.000 minimum
> budget), so one attachment turn used to leave a conversation measured far above the transcript it
> would actually replay and pin it there. The application now skips flagged turns and measures by the
> most recent clean one. **This is the loudest deploy-blocker of the recent scripts**: unlike 051 and
> 052, whose failure is swallowed by `GetEffectiveAsync`, this column is on `[dbo].[ChatMessages]`,
> which `ChatRepository` reads and inserts as a FULL entity with no projection — so publishing before
> the DDL fails **every chat turn and the whole conversation-history endpoint** with `Invalid column
> name 'HadEphemeralAttachment'`, with nothing catching it. (Billing keeps working: the monthly
> chat-token read in `UsageRepository` is a projection over the two token columns and is unaffected.)
> Run it on **every** tenant database before publishing, then **restart the App Service**. The `ALTER`
> backfills every existing row to 0, so there is no data migration — and note that the fix is
> therefore **forward-only**: a conversation that took an attachment turn before this deploy keeps its
> inflated measurement. No Portal deploy needed: no response payload changes and no new enum value.
> Idempotent; re-running never rewrites a flag already recorded. Rationale and rollback:
> `docs/db-changes/053_chatmessages_add_hadattachment.md`.

> `054_mfa_backup_codes.sql` has NOT been executed yet. It creates
> `[dbo].[MfaBackupCodes]` — single-use MFA recovery codes for a user who enrolled a TOTP factor in
> Firebase and lost the authenticator — plus `FK_MfaBackupCodes_Users` and two `(TenantId, UserId)`
> indexes, the second filtered on `[UsedAt] IS NULL`. **It supersedes
> `default-db/001_mfa_backup_codes.sql`, which has been deleted and must not be run**: that script
> created the same table in the shared "Default" database, and the table is FinancIA business data
> belonging to one `[dbo].[Users]` row, so it belongs here — beside `Users`, where it can carry a real
> foreign key to it. Nothing was ever deployed and no backup code has ever been issued, so there is no
> data to migrate. `TenantId` is written from the owning `Users` row rather than from `X-Tenant-ID`, so
> a code can never be filed under a tenant its owner does not belong to. Run it on **every** tenant
> database before publishing, then **restart the App Service**: EF maps the table unconditionally
> through `FinanciaDbContext.MfaBackupCodes`, so publishing first fails all four
> `/api/mfa/backup-codes` endpoints with `Invalid object name 'dbo.MfaBackupCodes'`. Blast radius is
> the new feature only — no existing table is touched. The redeem endpoint additionally needs the
> still-pending `default-db/002_tenantfirebaseconfig_add_webapikey.sql` on the **shared** database;
> the two are independent and both precede the deploy. No Portal deploy needed. Idempotent; re-running
> never drops a row or rewrites a stored hash. Rationale and rollback:
> `docs/db-changes/054_mfa_backup_codes.md`.

> `055_chatmessages_add_attachmentmarkdown.sql` has NOT been executed yet. It adds
> `[AttachmentMarkdown]` (`NVARCHAR(MAX) NULL`) to `[dbo].[ChatMessages]`, so the spreadsheet an
> analyst attaches to a chat turn stops dying with the request. Until now nothing kept it: reopening
> the conversation showed the question with no visible subject, and a follow-up meant re-uploading the
> same file. **Persistence only** — nothing reads the column back into a prompt, and not one token of
> what is sent to Claude changes. It goes on the **user** turn, which is the turn the upload arrived
> on; 053's `[HadEphemeralAttachment]` stays on the **assistant** turn and stays a billing marker, and
> the two are deliberately separate columns answering separate questions. **Nullable with no default
> and no backfill**, the inverse of 053's `BIT NOT NULL DEFAULT (0)`: here the value *is* the text, so
> NULL is the honest "no attachment", and the text of every attachment sent before this deploy exists
> nowhere to be recovered — the feature is forward-only, past turns stay blank. A stored value is
> capped at 200.000 characters by `CapAttachmentMarkdown` (it is the CAPPED string that is stored, the
> same one Claude was handed) and `/chat` carries a 1 MB request limit, so one row cannot exceed that.
> **Same deploy-order constraint as 053, on the same table and for the same reason**: `ChatRepository`
> reads and inserts the FULL entity with no projection, so publishing before the DDL fails **every
> chat turn and the whole conversation-history endpoint** with `Invalid column name
> 'AttachmentMarkdown'`, with nothing catching it. (Billing keeps working — the monthly chat-token
> read in `UsageRepository` is a projection over the two token columns.) Run it on **every** tenant
> database before publishing, then **restart the App Service**. No index: an `nvarchar(max)` column
> cannot be indexed and it is never a predicate. No Portal deploy needed — the history payload gains
> one additive boolean, `hasAttachment`, a marker rather than the Markdown (that endpoint returns
> every turn, and one attachment can be 200.000 characters); a Portal that does not know the field
> ignores it. Idempotent; re-running never rewrites an attachment already stored. Rationale and
> rollback: `docs/db-changes/055_chatmessages_add_attachmentmarkdown.md`.

> `056_chatmessages_add_attachmentfilename.sql` has NOT been executed yet. It adds
> `[AttachmentFileName]` (`NVARCHAR(255) NULL`) to `[dbo].[ChatMessages]`, so the analyst can tell
> WHICH spreadsheet a turn carried and not merely that one was attached. 055's `hasAttachment` marker
> cannot say `flujo-2026-final.xlsx` from the draft replaced an hour later, which on an analysis where
> three versions of the same cash flow were tried is close to useless. Same turn as 055 — the **user**
> turn — and NULL **exactly when** `[AttachmentMarkdown]` is NULL, because both are written from the
> same capped local: a request sending a file name and no attachment stores NULL for both. That
> invariant is enforced in the single writing code path rather than by a CHECK constraint, and the
> script's verification block carries the two queries that prove it in production. `NVARCHAR(255)`
> rather than MAX because it is a label, not content — `Path.GetFileName` output, trimmed defensively
> to 255 by the controller, which CUTS rather than rejects so a long name cannot 400 a turn whose file
> the server had already accepted. **Nullable, no default, no backfill**, like 055: the name of every
> attachment sent before this deploy was never stored on the message (it exists only inside the audit
> row's free-text `newValueJson`, which is not addressed by message id), so the feature is
> forward-only and past turns stay blank. **Same deploy-order constraint as 053 and 055, on the same
> table and for the same reason**: `ChatRepository` reads and inserts the FULL entity with no
> projection, so publishing before the DDL fails **every chat turn, the whole conversation-history
> endpoint and the new attachment endpoint** with `Invalid column name 'AttachmentFileName'`, with
> nothing catching it. (Billing keeps working — the monthly chat-token read in `UsageRepository` is a
> projection over the two token columns.) Run it on **every** tenant database before publishing, then
> **restart the App Service**. Apply **055 before or with 056** — a name with no Markdown column to
> label is useless. No index: never a predicate. No Portal deploy needed — the history payload gains
> two additive fields (`attachmentFileName` and `messageId`) and there is one NEW endpoint,
> `GET /api/analyses/{resultId}/chat/{conversationId}/messages/{messageId}/attachment`, which returns
> the stored Markdown for one message on demand; no existing field changes shape and no enum value
> changes. Note `messageId` is a **bigint**, not a GUID — `ChatMessageId` is `BIGINT IDENTITY`.
> Nothing reads either column into a prompt: not one token of what is sent to Claude changes.
> Idempotent; re-running never rewrites a name already stored. Rationale and rollback:
> `docs/db-changes/056_chatmessages_add_attachmentfilename.md`.

> `057_creditlineoperations_add_condition_flags.sql` has NOT been executed yet. It adds
> `[ASolaFirma]` and `[RequiereContragarantiaReal]` (both `BIT NOT NULL DEFAULT (0)`, each with a
> named default constraint) to `[dbo].[CreditLineOperations]`: the two underwriting conditions an
> operation may carry — granted **a sola firma**, and requiring a **contragarantía real** — which
> until now lived in the free-text `[Notes]` or nowhere. They are **per OPERATION**, deliberately:
> two operations under the same cupo, drawn from the same approved analysis, routinely differ on
> both, so neither `[CreditLines]` nor `[Analyses]` could hold them. They are **pure metadata and
> feed no rule** — `OccupiesCupo` is still "stored status is not `'Cancelada'`", so a flagged
> operation reserves saldo exactly like an unflagged one, `Utilizado`/`Disponible`/`OperationsCount`
> and the create-time sum rule ignore them, the `'Aprobada'` → `'Vigente'` → `'Cancelada'` lifecycle
> is untouched, and nothing validates them (all four combinations are legal, and a `BIT` leaves a
> `CHECK` nothing to say). **NOT NULL with a DEFAULT, the inverse of 040/041's nullable audit
> columns next door, and for a real reason**: those record an EVENT that may genuinely be unknown
> for old rows, whereas these record a CONDITION every operation has an answer for — an operation
> not granted a sola firma simply was not, which is false, not unknown. So the `ALTER` backfills
> every existing row to 0 and there is no data migration and no forward-only gap. **This one IS a
> deploy-blocker**, in exactly the way 040 and 041 are and on the same table: both columns are
> mapped in `CreditLineOperationConfiguration`, and `CreditLineOperationRepository` reads and inserts
> the FULL entity with no projection, so publishing first fails **operation creation,
> confirmar-vigencia, cancellation and the whole `GET /api/credit-lines` structure read** with
> `Invalid column name`, with nothing catching it. Run it on **every** tenant database before
> publishing, then **restart the App Service** so EF's model cache is rebuilt. No index: neither
> column is ever a predicate. **No Portal deploy needed first** — unlike 040 this introduces no new
> status value and changes no existing field's shape; the operation payload gains two additive
> booleans. Idempotent; re-running never rewrites a flag already recorded. Rationale and rollback:
> `docs/db-changes/057_creditlineoperations_add_condition_flags.md`.
>
> The same release also carries a **code-only** change to the same endpoints, with no schema
> component and nothing for the DBA to run: credit-line and operation mutations (raise, suspend,
> reactivate, confirmar-vigencia, cancel) are now restricted to the row's creator or an Admin, and
> answer **403** otherwise. `[CreatedByUserId]` has been stored on both credit-line tables since
> script 030 and was simply never read back — no column is added, altered or backfilled for it. The
> API responses gain `createdByUserId` / `createdByName` on cupos and operations so the Portal can
> hide an affordance rather than discover the 403; that Portal follow-up is worth flagging to that
> team, but it does not gate this deploy.

> `058_analyses_add_preapproval_columns.sql`, `059_user_approval_limits.sql` and
> `060_analyses_status_add_preapproved.sql` have NOT been executed yet. **They are one change and
> must be applied together**, on every tenant database, before the matching deployment. They
> introduce the **two-step analysis decision**: an analyst approves directly up to a personal,
> per-currency ceiling, and above it can only PRE-approve, leaving a RiskManager (or Admin) to
> confirm — possibly at a different amount — reject, or return it for rework.
>
> **058** adds six NULLABLE columns to `[dbo].[Analyses]`: `PreApprovedBy` / `PreApprovedAt` /
> `PreApprovedAmount` / `PreApprovedCurrency` (the first signature) plus `DecisionAmount` /
> `DecisionCurrency` (the figure actually committed), and `FK_Analyses_PreApprovedBy` → `Users`.
> `ReviewedBy` / `ReviewedAt` / `ReviewNotes` are **reused** as the final-confirmation record, so no
> columns are added for those. Nullable **like 040/041 and unlike 057**, and for the reason those
> two give: these record an EVENT that either happened or did not, so NULL is the honest "no
> pre-approval was made" rather than a guess. **No backfill, no data migration** — every analysis
> already `Approved` under the single-step model stays valid with all six NULL. Note the names
> deliberately avoid `ApprovedAmount` / `ApprovedCurrency`, which existed here once (added by 024,
> dropped by 031) and whose reuse would let a stale re-run of 031 drop columns this feature needs.
>
> **059** creates `[dbo].[UserApprovalLimits]` — `(UserId, Currency)` PK, `CanApprove`, nullable
> `MaxAmount`, audit columns, `FK_UserApprovalLimits_Users`, one index. **Deliberately NOT seeded**:
> the absence of a row is the default and means "may only pre-approve", so a tenant that never
> configures it simply routes every analyst decision through a second signature. Reading a missing
> row as *unlimited* would invert the failure mode, so the code reads `null` as a denial. Admins
> populate it through `GET`/`PUT /api/bff/settings/users/{userId}/approval-limits`.
>
> **060** is the one **CHECK-constraint change**, and it is required rather than optional:
> `CK_Analyses_Status` exists (from `001_initial_schema`) and admits only six values, so without
> this every pre-approval fails on the constraint. It drops and recreates it with `N'PreApproved'`
> added, in 043's guarded idiom. `PendingReview` needed no change — it was already legal, and it is
> reused as the return-for-rework destino rather than inventing a second name.
>
> **All three are deploy-blockers.** The six columns are mapped in `AnalysisConfiguration`, and
> `AnalysisRepository.GetByIdAsync` selects the FULL entity with no projection while `UpdateAsync`
> does a full-row `.Update()`, so publishing first fails **the decision endpoint, the analysis
> detail AND the analyses list** with `Invalid column name`; the new table is queried on every
> analyst approval, so its absence fails with `Invalid object name`. **Restart the App Service**
> after deploying so EF's model cache is rebuilt.
>
> **The Portal MUST be deployed first**, as with 040 and unlike 057 — two compounding reasons:
> `PreApproved` is a new `Status` value its renderer has no branch for, and the decision payload now
> **requires** `amount` and `currency` on an approval, so the current Portal's approve call becomes a
> `400` the moment this API is live. The Portal also needs the Admin limits screen and the
> pre-approval record on the detail view. **`061` belongs to this same batch and is applied in the
> same sitting** — see its own note below. **Deploy order: Portal → DDL 058 + 059 + 060 + 061 on
> every tenant DB → publish this API → restart the App Service.** All four are idempotent. Rationale
> and rollback: `docs/db-changes/058_analyses_add_preapproval_columns.md`,
> `docs/db-changes/059_user_approval_limits.md`,
> `docs/db-changes/060_analyses_status_add_preapproved.md` — note 060's rollback **fails by design**
> while any analysis still sits in `PreApproved`.
>
> The same release also carries **code-only** changes with no schema component: `RiskManager` is now
> permitted on `PATCH /api/analyses/{id}/decision` (it was denied), which needed **no** DDL because
> `CK_Users_Role` has admitted `RiskManager` since `001_initial_schema` and
> `SettingsController.ValidRoles` already listed it; and `GET /api/analyses/{id}` now resolves the
> requester, reviewer and pre-approver names in ONE batched lookup instead of a query per actor,
> which changes an unresolvable account's name from `null` to `"Usuario"`, matching chat and the
> credit-line payloads.

> `061_analyses_add_proposed_confirmed_operations.sql` has NOT been executed yet, and is **part of
> the 058 + 059 + 060 change above** — same feature, same sitting, same deploy order. It adds two
> NULLABLE `NVARCHAR(MAX)` columns to `[dbo].[Analyses]`: `ProposedOperationsJson` and
> `ConfirmedOperationsJson`, each a JSON array of the operations behind one side of a decision.
>
> **What it fixes.** 058 gave a decision two amounts and that was only half the record. An analyst
> pre-approving also enters the OPERATIONS the cupo is made up of (type, importe, vigencia, a sola
> firma, contragarantía real), and the API had nowhere to put them — it wrote the amount and dropped
> the rest, so the RiskManager who confirms got a bare figure. These two columns are where they go,
> and **two** of them rather than one for the reason 058 keeps two amounts: a confirmer may change
> the operations, not just the total, and the pair is the only place that override is visible. A
> confirmation never rewrites the proposed blob.
>
> **A JSON column, not a child table.** These are the frozen TERMS of a decision; the live rows keep
> living in `[dbo].[CreditLineOperations]`, created by the credit-line routes once the analysis is
> `Approved`. Stored as a plain blob, this follows `Entities.LufeDataJson` and
> `AnalysisResults.BcraDataJson` — serialised manually at the controller boundary, never joined.
> This is the FIRST JSON column on `[dbo].[Analyses]` (`ReviewNotes` is `NVARCHAR(MAX)` but free
> text). Each element matches `CreateOperationRequest` field for field minus `AnalysisId`, so a
> confirmed element goes straight to `POST /api/credit-lines/{id}/operations` untranslated.
>
> **Both nullable, no backfill**, like 058 and unlike 057: they record an event that either happened
> or did not. An empty array is never stored — the API writes NULL when a decision carries no
> operations. The two are asymmetric on purpose: a ONE-STEP approval fills confirmed and leaves
> proposed NULL (exactly as it leaves `PreApprovedAmount` NULL), and a rejection or return leaves
> confirmed NULL (exactly as it leaves `DecisionAmount` NULL). Read confirmed for what was granted;
> read the pair only when both are present. No CHECK, no `ISJSON`, no FK, no index — rationale in
> the note.
>
> **Deploy-blocker, same as 058.** Both columns are mapped in `AnalysisConfiguration`, and
> `AnalysisRepository.GetByIdAsync` selects the full entity while `UpdateAsync` does a full-row
> `.Update()`, so publishing first fails the decision endpoint, the analysis detail AND the analyses
> list with `Invalid column name`. **Restart the App Service** after deploying.
>
> The Portal ordering is 058's, not this script's: 061's own API change merely ACCEPTS an optional
> `operations` array and RETURNS two new nullable arrays, so a Portal deployed after this API keeps
> working. Sending and rendering them is a separate Portal change that can follow at its own pace.
>
> The same release also carries a **code-only** change with no schema component: the SGR
> credit-line type catalogue moves out of `CreditLinesController` into `Clases/CreditLineTypes.cs`
> so the decision endpoint and the operations endpoint validate a `type` against one list instead of
> two copies. Same eighteen codes, code `0` still accepted, nothing for the DBA to run — exactly as
> the `Currencies` extraction needed nothing. Rationale and rollback:
> `docs/db-changes/061_analyses_add_proposed_confirmed_operations.md`.
