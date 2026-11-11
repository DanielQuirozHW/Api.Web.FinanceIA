# 059 — How much each analyst may commit before a second signature is needed

## What changed and why

058 gave an analysis room for two signatures. This script adds the table that decides **when the
second one is required**, which is what turns the two-step flow from a blanket rule into a
delegation ladder: an analyst commits directly up to a personal ceiling, and only beyond it does the
decision escalate.

`[dbo].[UserApprovalLimits]`, one row per (user, currency):

| Column | Meaning |
|---|---|
| `TenantId` | tenant slug (carried, filtered on, not part of the key) |
| `UserId` | the user the ceiling applies to |
| `Currency` | ISO 4217 code the ceiling is denominated in |
| `CanApprove` | may this user approve directly in this currency at all? |
| `MaxAmount` | inclusive ceiling; `NULL` with `CanApprove = 1` means **no** ceiling |
| `CreatedAt` / `CreatedBy` / `UpdatedAt` / `UpdatedBy` | audit trail |

### The absence of a row is the default, and it is a denial

A user with **no row** for a currency cannot approve in it directly; their decision becomes a
pre-approval awaiting a RiskManager. This is the single most important property of the table and it
is why **nothing is seeded**: a tenant that never configures it routes every analyst decision
through a second signature, which is the safe reading of "not configured".

The opposite default — a missing row meaning "unlimited" — would be the same table with the failure
mode inverted: forgetting to configure someone would silently grant them unbounded authority. So the
code reads `null` as a denial, never as an absent ceiling, and the tests pin that.

A row with `CanApprove = 0` is therefore **not** redundant with no row at all. Both stop a direct
approval, but the row is a deliberate, auditable statement, and it retains a ceiling an administrator
configured earlier in case they re-enable it. `CanApprove` is the gate; `MaxAmount` only bounds it —
a row that stores a ceiling but denies approval must not approve just because the amount fits under
the ceiling.

### Only analysts are measured against it

RiskManager and Admin have no ceiling and the code never reads this table for them, so they hold no
rows here. An empty result for a RiskManager is not a restriction — it is never consulted.

### Why the key is `(UserId, Currency)` and not `(TenantId, UserId, Currency)`

`UserId` is already globally unique — `[dbo].[Users]` is keyed by it alone — so adding `TenantId` to
the key would permit the same user to hold two different ceilings in one currency with nothing to
choose between them. `TenantId` is still carried and still filtered on in every query, because
`FinanciaDbContext` has no global query filter and the predicate is ours to write; the verification
section includes a query that catches any row whose tenant disagrees with its user's.

`UserId` leads the key rather than `TenantId`, unlike the tenant-first indexes elsewhere in this
schema. The hot read is a point lookup for one user in one currency on the decision path, and the
leading column should be the one the predicate always has.
`IX_UserApprovalLimits_TenantId_UserId` covers the admin screen's "everything for this user in this
tenant" read.

### Constraints: one FK, no CHECKs

`FK_UserApprovalLimits_Users` **is** declared, following 027, 049 and 054. 049 states the test: a
foreign key earns its place when the column is a live *subject* rather than a record of a past
action. A limit belonging to a user row that does not exist can never be read by anything, so it is
exactly that case. No `CASCADE`, per 027 and 049.

`CreatedBy` / `UpdatedBy` carry **no** foreign key — the other half of the same rule, as 030, 040,
041 and 046 argue: they record who performed an action and may outlive the account. Both are
nullable, because the DBA may seed a row with no application user behind it.

No `CHECK` on `Currency`: the accepted codes live in `Clases/Currencies.cs` and are validated in the
API, following 036, 046 and 052 — a value list in the schema turns adding a currency into a
migration on every tenant database. No `CHECK` forbidding a negative `MaxAmount` either; the API
rejects it, and a `DECIMAL` constraint cannot express the rule about `NULL` that actually matters.

## DDL

Script location: `docs/database/059_user_approval_limits.sql`

| Object | Type |
|---|---|
| `[dbo].[UserApprovalLimits]` | table, 9 columns |
| `PK_UserApprovalLimits` | primary key, clustered, `(UserId, Currency)` |
| `DF_UserApprovalLimits_CanApprove` | default constraint, `DEFAULT (0)` |
| `DF_UserApprovalLimits_CreatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `DF_UserApprovalLimits_UpdatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `FK_UserApprovalLimits_Users` | foreign key → `[dbo].[Users]([UserId])`, no cascade |
| `IX_UserApprovalLimits_TenantId_UserId` | nonclustered index |

Three guarded blocks on `sys.objects` / `sys.foreign_keys` / `sys.indexes`, `PRINT` in both
branches, `GO` per batch, no transaction — the conventions in `docs/database/README.md`.

`CanApprove` defaults to `0`, so a hand-written `INSERT` that forgets it denies rather than grants.

## How the application uses it

`UserApprovalLimit` is a mapped entity on `FinanciaDbContext` (composite key via
`HasKey(x => new { x.UserId, x.Currency })`), reached through
`IUserApprovalLimitRepository`, registered scoped.

- **Read, on the decision path**: `GetAsync(userId, currency, tenantId, ct)` — one point lookup per
  analyst approval, served by the clustered PK. `null` means "may only pre-approve".
- **Write, from the Admin screen**: `GET` and `PUT /api/bff/settings/users/{userId}/approval-limits`,
  both `[Authorize(Roles = "Admin")]`. The `PUT` is a **replace**, not a merge: currencies absent
  from the payload are cleared, because removing a currency has to be expressible and the screen
  edits the whole set at once. Both endpoints 404 unless the target user exists **and** belongs to
  the caller's tenant — `GetByUserIdAsync` is keyed by a globally unique id, so without that check
  an Admin could read or rewrite another tenant's limits by guessing a GUID.

Without the write path this table could only be populated by the DBA, which would ship the feature
with every analyst permanently unable to approve directly. That is why the Admin endpoints are part
of this change rather than a follow-up.

## Shipping alongside

- **058** — the pre-approval and decision-amount columns on `[dbo].[Analyses]`.
- **060** — widens `CK_Analyses_Status` to admit `'PreApproved'`.

## Pre-conditions

- Apply **058, 059 and 060 together**, on **every tenant database**, **before** publishing the API.
- `UserApprovalLimit` is a mapped entity and the decision endpoint queries it on every analyst
  approval. Against a database without the table, every approval fails with `Invalid object name`.
- **Restart the App Service** after deploying so EF's model cache is rebuilt.
- Creating the table changes **no** existing behaviour on its own: until the new code is live nothing
  reads it, and once live, an empty table simply means every analyst decision is a pre-approval.
  Populating it is a business decision per tenant — prefer the Admin screen. The script carries a
  commented seed template for a DBA who needs one, deliberately not executed.

**Deploy order: Portal → DDL 058 + 059 + 060 on every tenant DB → publish this API → restart the App Service.**

(The Portal-first requirement comes from 058/060 — the new `PreApproved` status and the now-required
amount on an approval. This script alone would not need it.)

## Rollback

Roll the **application** back first: the deployed code queries this table on every analyst approval,
so dropping it under a running deployment breaks the decision endpoint with `Invalid object name`.
Dropping the table removes its foreign key, index and default constraints with it.

```sql
DROP TABLE [dbo].[UserApprovalLimits];
GO
```

Export first if the configuration matters:

```sql
SELECT [TenantId], [UserId], [Currency], [CanApprove], [MaxAmount]
FROM   [dbo].[UserApprovalLimits];
```

What is lost is every configured ceiling. **No analysis is affected** — the decisions themselves live
on `[dbo].[Analyses]` and are untouched — but after a rollback every analyst is back to the default
of "may only pre-approve" until an Admin reconfigures them. That is a degraded-but-safe state, not a
broken one, which is the point of choosing that default.

## Verification

The script carries six commented checks: the nine columns and their types, that the primary key is
the composite in the right order, the foreign key and index, that the table starts **empty** (which
is correct), that a configured row round-trips, and a cross-tenant integrity query that must return
nothing.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
