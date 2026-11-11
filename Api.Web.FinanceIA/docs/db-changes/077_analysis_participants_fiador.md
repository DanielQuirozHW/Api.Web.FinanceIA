# 077 — AnalysisParticipants: the fiador columns

## What changed and why

M10 Phase 5 evaluates a FIADOR (guarantor) who is a natural person but not necessarily a client. 072
created `[AnalysisParticipants]` with `Role IN ('Solicitante', 'Fiador')` and left the fiador
columns of the design to Phase 5. This script adds them:

| Column | Type | Meaning |
|---|---|---|
| `FiadorId` | UNIQUEIDENTIFIER NULL, FK → `[Fiadores]` (NO ACTION) | the fiador row a Fiador participant stands for |
| `GuaranteeType` | NVARCHAR(30) NULL | the kind of guarantee; no value CHECK yet (its vocabulary is 5a-2's) |
| `SpouseConsent` | BIT NULL | asentimiento conyugal obtained |
| `HousingInopposable` | BIT NULL | true: the housing protection (CCyC art. 244/456) was declared inopposable to this creditor by deed, so the home IS executable for this guarantee |
| `GuaranteesGivenAmount` | DECIMAL(18,2) NULL | guarantees the fiador already gives elsewhere |

Two CHECKs, both created WITH CHECK (trusted):

- `CK_AnalysisParticipants_FiadorId_Role`:
  `([Role] <> N'Fiador' OR [FiadorId] IS NOT NULL) AND ([Role] <> N'Solicitante' OR [FiadorId] IS NULL)`.
  A Fiador row names its fiador; a Solicitante row names none.
- `CK_AnalysisParticipants_GuaranteesGivenAmount`: `IS NULL OR >= 0`.

No index on `FiadorId`. Nothing reads by it yet, and a fiador is never deleted (removal is the soft
`Status = 'Removed'`), so the FK is never probed by a delete. The FK is not modelled in EF, like the
other two FKs of the table.

**This release writes none of the five columns.** It maps them so that the fiador evaluation (5a-2)
can, and the persona analysis keeps inserting Solicitante rows with `FiadorId` NULL.

**Since 5a-2 (no further DDL)** `POST /api/person-analyses` writes them: one `Role = 'Fiador'` row per
fiador, with `FiadorId`, `GuaranteeType` (`Fianza` | `Conyuge`, validated in code by `GuaranteeTypes`),
`SpouseConsent`, `HousingInopposable` and `GuaranteesGivenAmount`, frozen with the fiador's own evaluation.
The script's header still reads "the fiador's home is protected (vivienda inoponible)"; the meaning in
force is the one in the table above — an executed script is not edited.

## What ships with it (no further DDL)

- `POST /api/entities/{entityId}/fiadores/{fiadorId}/legajo` is the first writer of
  `[Fiadores].[PersonLegajoId]`, which has existed since DDL 069. It creates the tenant's legajo for
  the fiador's CUIT, or links the existing one. EF now models `FK_Fiadores_PersonLegajos`, also DDL
  069, so a new legajo is inserted before the fiador row that points at it.
- `POST /api/documents/upload` accepts the legajo of an ACTIVE fiador of the entity. The document keeps
  the PyME's `EntityId` (`[Documents].[EntityId]` is NOT NULL since 001) and carries the fiador's
  `PersonLegajoId`.
- `POST /api/person-legajos/{legajoId}/refresh-bcra` and `/refresh-arca` refresh a legajo's snapshots.

## Audit and PII

- `Fiador.LegajoLinked` carries `{"PersonLegajoId": …, "Created": true|false}` and nothing else.
- `SpouseConsent` implies marital status, and `GuaranteesGivenAmount` is a natural person's financial
  figure. Neither is logged nor sent to a prompt.
- Logs carry FiadorId, EntityId, PersonLegajoId and TenantId only.

## Deploy order

1. The DBA runs `077_analysis_participants_fiador.sql` in **every** Financia database (one per client,
   `.github/clients.json`). It is not for the Default database. Running it first is safe for the code
   already deployed: every column is nullable with no default, the old INSERT names none of them, and
   its Solicitante rows with `FiadorId` NULL satisfy both CHECKs. Section 0 stops the script, altering
   nothing, if a participant row with another role already exists.
2. Run the deploy workflow from DEVELOP with `ddl_aplicada` ticked and `client = all`.
   `--verify-schema` refuses a client whose database lacks a column.
3. The App Service restart is part of the publish (`restart: true`).
4. Deploy the Portal. Every API change is additive: `FiadorResponse.personLegajoId` and three new
   routes.

Publishing the API before the script breaks every read and write of `[AnalysisParticipants]`, i.e.
the persona analysis, with `Invalid column name`.

## Rollback

Roll the API back to a release that does not map the columns, then run the commented rollback at the
end of the script. That means exporting first any row that uses the columns (expect none), then
dropping the two CHECKs, the FK and the five columns. Fiadores linked to a legajo stay valid under the
old code, which maps `PersonLegajoId` and never reads it.
