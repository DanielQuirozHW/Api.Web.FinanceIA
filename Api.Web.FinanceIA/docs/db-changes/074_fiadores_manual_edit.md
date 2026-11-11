# 074 — Fiadores: manual add and edit

## What changed and why

LUFE's socios/autoridades list (`LufeData.Autoridades[]`) is often wrong or incomplete. Until now an
analyst could add as a fiador only a CUIT that LUFE reported. The name, cargo and percentage were
copied from LUFE, the request values were ignored, and nothing could correct them afterwards.

This release adds three things:

- **Manual add.** `POST /api/entities/{entityId}/fiadores` with `origen: "Manual"` stores the
  analyst's own CUIT, name, cargo and percentage. The CUIT may or may not be in LUFE.
- **Edit.** `PATCH /api/entities/{entityId}/fiadores/{fiadorId}` corrects `denominacion`, `cargo` and
  `porcAccionista` of any ACTIVE fiador, whatever its origen. The CUIT is never editable.
- **ARCA prefill.** `GET /api/entities/{entityId}/fiadores/arca-lookup?cuit=…` returns the name ARCA
  knows for a CUIT, so the manual form can be prefilled. It creates nothing.

| Change | Where |
|---|---|
| When the latest edit happened | `[dbo].[Fiadores].[UpdatedAt]` DATETIME2 NULL (UTC; NULL = never edited) |
| Who made it | `[dbo].[Fiadores].[UpdatedByUserId]` UNIQUEIDENTIFIER NULL (Users.UserId, no FK, like `CreatedByUserId`) |

Nothing else changes in the schema. The existing columns already fit a manual fiador:
`Denominacion` NVARCHAR(300), `Cargo` NVARCHAR(200), `PorcAccionista` DECIMAL(5,2), and `Origen`
NVARCHAR(20). Script 046 reserved the value `'Manual'` for exactly this case and deliberately added no
CHECK constraint. `'Lufe' | 'Manual'` is still validated in the API.

## What the API derives from the new columns

`FiadorResponse` gains the following fields:

- `editado`: true when `UpdatedAt` is set, or when a `Lufe` fiador's stored fields differ from the
  entity's CURRENT LUFE entry for that CUIT.
- `lufeOriginal`: LUFE's current name, cargo and percentage for a `Lufe` fiador while LUFE still
  reports the CUIT. Null otherwise.
- `updatedAt`.
- `updatedByName`.

None of these is stored. They are computed per read from the entity's persisted LUFE snapshot. This
means a LUFE refresh that changes a socio's data can turn `editado` true without anyone editing the
fiador. That is intended: the analyst sees that the fiador and LUFE disagree.

## Audit and PII

- `Fiador.Added` now carries `{"Origen":"Lufe"|"Manual"}` and no names.
- `Fiador.Edited` carries `{"ChangedFields":[…]}`, which is the field NAMES only. The values identify a
  natural person and are never written to the audit trail.
- Logs carry FiadorId, EntityId and TenantId only.

## Deploy order

1. The DBA runs `074_fiadores_manual_edit.sql` on Financia-db, and on any other business database the
   DDL procedure lists. Running it first is safe for the code already deployed: both columns are
   nullable with no default, and the old INSERT names neither.
2. Run the deploy workflow with `ddl_aplicada`. `--verify-schema` refuses the publish if either column
   is missing.
3. Restart the App Service.
4. Deploy the Portal. The API change is additive: the old Portal never sends `origen`, so its adds keep
   taking the LUFE path unchanged, and it simply ignores the new response fields.

Publishing the API before the script breaks every read and write of `[Fiadores]` with
`Invalid column name`: the list, add, remove and refresh-nosis.

## Rollback

Roll the API back to a release that does not map the columns, then:

```sql
ALTER TABLE [dbo].[Fiadores] DROP COLUMN [UpdatedByUserId], [UpdatedAt];
```

Rows added with `Origen = 'Manual'` stay valid under the old code. It only ever wrote `'Lufe'`, but
it reads `Origen` as a plain string.
