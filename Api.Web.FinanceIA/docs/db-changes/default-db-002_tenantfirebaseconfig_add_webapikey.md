# default-db 002 — `TenantFirebaseConfig.WebApiKey`

**Script:** `docs/database/default-db/002_tenantfirebaseconfig_add_webapikey.sql`
**Database:** `Default` (shared) — run ONCE, not per tenant
**Status:** pending

## What it does

Adds one nullable column, `[WebApiKey] NVARCHAR(200) NULL`, to
`[dbo].[TenantFirebaseConfig]`, plus a column description. No data is modified and no
existing column is touched.

## Why

`POST /api/mfa/backup-codes/redeem` lets a user who enrolled TOTP and then lost their
authenticator sign in with a backup code. To do that safely the BFF has to establish that
the caller knows the account password — and it cannot get that from the client.

When a TOTP-enrolled user signs in, Identity Platform returns an opaque
`mfaPendingCredential` and **no ID token**; the ID token is only issued by
`mfaSignIn:finalize`, which requires the very TOTP code the user has lost. That pending
credential cannot be checked server-side either: the Firebase Admin SDK has no
multi-factor surface at all, and the REST API only ever *consumes* the credential, never
validates it. So there is nothing verifiable the client can hand the backend.

The only remaining option is for the BFF to re-run the password check itself against
Identity Platform's `accounts:signInWithPassword`. An `mfaPendingCredential` coming back
from *that* call is Google's own attestation that the password is correct and a second
factor is enrolled — a proof the BFF produced rather than one the caller asserted.

That call needs the project's **Web API key**, and this project runs **one Firebase project
per tenant**, so the key varies by tenant and has to live next to the rest of that tenant's
Firebase configuration. Nothing in the schema held it before.

## Why it is not encrypted

`CredentialJson` and `RecaptchaSecretKey` in the same table are AES-256 encrypted at rest.
`WebApiKey` deliberately is not:

- A Firebase Web API key is **public by design**. It ships inside every browser bundle —
  the Portal already exposes it as `NEXT_PUBLIC_FIREBASE_API_KEY`.
- It identifies a project; it does not authorise anything on its own. Access is governed by
  Firebase Auth rules and per-project quotas, not by possession of the key.
- Encrypting it would imply a secrecy it does not have, and cost a decrypt on every tenant
  resolution — which is on the hot path for every request.

This reasoning is repeated in the column's extended property, in
`Models/TenantFirebaseConfig.cs` and in `TenantContext.FirebaseWebApiKey`, specifically so
that a future reviewer does not "fix" the inconsistency by encrypting it.

## Why nullable

The column is added to a table that already has a row per live tenant. A `NOT NULL` column
would need a backfill in the same script, and the correct value differs per tenant and must
be read out of each project's Firebase console — not something a DDL script can supply.

Nullable makes the feature fail closed instead: a tenant with no key gets `503` from the
redeem endpoint alone, and every other endpoint is untouched. `TenantContext.FirebaseWebApiKey`
is likewise `string?` and **not** `required`, so tenant resolution keeps working for every
tenant that has not been populated yet.

## Deploy ordering — this one is not optional

Run **before** the matching application build is published.

`TenantFirebaseConfig` is read during **tenant resolution, on every request**. EF Core maps
the new column unconditionally, so publishing the code first makes every read of that table
fail with `Invalid column name 'WebApiKey'` — which takes down the entire API, not just the
new endpoint. This is a strictly larger blast radius than `054_mfa_backup_codes.sql`, which
only creates a new table and can therefore only break the new feature.

Order:

1. Run `054_mfa_backup_codes.sql` on **every tenant business DB** (still pending). It replaces
   the withdrawn `default-db/001_mfa_backup_codes.sql`; the two steps are independent, so this
   one may equally run after step 2.
2. Run `default-db/002_tenantfirebaseconfig_add_webapikey.sql`.
3. Deploy the API.
4. Restart the App Service so EF's model cache is rebuilt.
5. Populate `WebApiKey` per tenant:
   ```sql
   UPDATE [dbo].[TenantFirebaseConfig]
      SET [WebApiKey] = N'<the project''s Web API key>'
    WHERE [TenantId]  = N'<tenant slug>';
   ```
   Steps 3–4 do not depend on step 5: until a tenant is populated its backup-code login
   answers 503 and nothing else regresses.

## Rollback

`ALTER TABLE [dbo].[TenantFirebaseConfig] DROP COLUMN [WebApiKey];` — but only after
rolling the application back, for the same reason the forward order matters. Dropping the
column under a running build that maps it produces the same API-wide failure.
