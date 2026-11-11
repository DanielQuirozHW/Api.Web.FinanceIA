# MFA backup-code login — Portal contract

The BFF half of the 2FA recovery flow. The Portal half is not built yet; this describes what the
API now exposes and what the Portal has to do with it.

Repo: `PortalesHWA/Portal-FinanceIA`, branch from `develop`, PR against `develop`.

---

## Why the flow looks like this

Worth reading before implementing, because the obvious approaches do not work.

When a TOTP-enrolled user calls `signInWithEmailAndPassword`, Firebase throws
`auth/multi-factor-auth-required`. The only way the Web SDK can finish that sign-in is
`resolver.resolveSignIn(assertion)` with a TOTP or SMS assertion — **a backup code cannot satisfy
it**, and there is no Identity Platform API that accepts one.

Two things the Portal might reasonably try, and why neither works:

- **Send the pending credential to the BFF.** `MultiFactorResolver` exposes only `hints`,
  `session` and `resolveSignIn()`, and `MultiFactorSession` is an empty interface. The value is
  reachable at `error.customData._serverResponse.mfaPendingCredential`, but it is an underscore-
  prefixed SDK internal — and, more decisively, **nothing can verify it server-side**. The Admin
  SDK has no multi-factor surface at all, and the REST API only ever consumes the credential
  (`mfaSignIn:finalize`, which needs the lost TOTP code). The BFF would have no way to tell a real
  credential from an invented one.
- **Send a first-factor ID token.** There isn't one. Identity Platform returns
  `mfaPendingCredential` + `mfaInfo` and **no `idToken`** — the token is only issued after the
  second factor passes. The MFA-pending state is pre-token by design.

So the BFF re-runs the password check itself against Identity Platform. An `mfaPendingCredential`
coming back from *that* call is Google's own attestation that the password is correct and a second
factor is enrolled — a proof the BFF produced rather than one the client asserted.

---

## The endpoint

```
POST /api/mfa/backup-codes/redeem
POST /api/bff/mfa/backup-codes/redeem      (same handler, both prefixes)
```

No `Authorization` header. `X-Tenant-ID` **is** required — it selects the Firebase project.

### Request

```jsonc
{
  "email":          "analyst@example.com",
  "password":       "the account password",
  "code":           "ABCD-2345",   // case- and hyphen-insensitive
  "recaptchaToken": "..."          // reCAPTCHA V3, required
}
```

### Responses

| Status | Meaning | What the Portal should do |
|--------|---------|---------------------------|
| `200` | All checks passed | Redeem the token (below) |
| `400` | A required field is missing | Fix the form |
| `401` | Credentials or backup code rejected | Show one generic error |
| `422` | reCAPTCHA failed | Ask them to retry |
| `429` | Rate limited (10/min per IP) | Show a "try again shortly" message; honour `Retry-After` |
| `502` | Identity Platform unreachable | Transient — invite a retry, do **not** say the credentials were wrong |
| `503` | Tenant has no Web API key configured | Operator problem; surface a support message |

`200` body:

```jsonc
{
  "customToken": "eyJhbGciOi...",  // treat as a credential
  "remaining":   7                 // unspent codes left AFTER this one was spent
}
```

**`401` is deliberately uniform.** A wrong password, an unknown account, an account with no second
factor, and a wrong or already-spent code all return the same status and the same message
(`"Credenciales o código de recuperación inválidos."`). Do not try to tell them apart or infer a
reason — the endpoint is anonymous, and distinguishing them would make it an account-enumeration
and password oracle. Show one message.

Note the contrast with `POST /api/mfa/backup-codes/validate`, which answers `200 {"valid": false}`
for a wrong code. That endpoint is authenticated; this one is not, and here a `200` must mean a
token was minted.

---

## Portal flow

```ts
try {
  await signInWithEmailAndPassword(auth, email, password);
} catch (err) {
  if (err.code !== 'auth/multi-factor-auth-required') throw err;

  // Offer "I lost my authenticator — use a backup code" alongside the TOTP prompt.
  const recaptchaToken = await executeRecaptcha('mfa_backup_code_login');

  const res = await fetch('/api/bff/mfa/backup-codes/redeem', {
    method:  'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tenant-ID': tenantId },
    body:    JSON.stringify({ email, password, code, recaptchaToken }),
  });

  if (!res.ok) { /* one generic message for 401; see the table above */ return; }

  const { customToken, remaining } = await res.json();

  // Firebase genuinely completes the sign-in here — signInWithCustomToken is not gated
  // by the second factor.
  const cred = await signInWithCustomToken(auth, customToken);

  // From here it is the ordinary session exchange.
  const idToken = await cred.user.getIdToken();
  await fetch('/api/bff/auth/token', {
    method:  'POST',
    headers: { 'Content-Type': 'application/json',
               'X-Tenant-ID': tenantId,
               Authorization: `Bearer ${idToken}` },
    credentials: 'include',
  });

  if (remaining <= 2) { /* prompt them to regenerate their codes */ }
}
```

Do not store `customToken` anywhere — redeem it immediately and discard it. Same rule as the ID
token: never `localStorage`, never `sessionStorage`.

After a successful recovery, push the user toward re-enrolling TOTP and regenerating their codes
(`POST /api/mfa/backup-codes/generate`, which replaces the unspent set). `remaining` is there to
make "you have 2 codes left" possible before they are locked out for good.

---

## ⚠ Unverified premise — confirm before building against this

`signInWithCustomToken` is assumed **not** to be gated by the MFA second factor. That assumption
is load-bearing for the whole flow and **Google does not document it either way**:

- In favour: `firebase-js-sdk`'s `custom_token.ts` has no MFA handling at all.
- Against: the Firebase MFA docs say every provider supports MFA *except* phone auth, anonymous
  auth and Apple Game Center — custom auth is not on that exclusion list.

One API call settles it: mint a custom token for a TOTP-enrolled uid with the Admin SDK, `POST` it
to `/v1/accounts:signInWithCustomToken`, and see whether the response carries `idToken` or
`mfaPendingCredential`.

If it turns out custom tokens **are** gated, the BFF switches to minting the Internal JWT directly
and setting the `bff_session` cookie — every other step above is unchanged, but the Portal then
skips `signInWithCustomToken` and `/api/bff/auth/token` entirely and ends up signed in to FinancIA
with no Firebase session. Confirm before building the Portal side.

---

## Operator prerequisites

- `docs/database/054_mfa_backup_codes.sql` run on **every tenant business DB**
  (`FinanciaDefault`) — it creates `MfaBackupCodes` beside `Users` — and
  `docs/database/default-db/002_tenantfirebaseconfig_add_webapikey.sql` run once on the **Default**
  DB. Independent of each other; both before the API deploy.
  (`default-db/001_mfa_backup_codes.sql` was withdrawn and deleted — `054` replaces it.)
- `TenantFirebaseConfig.WebApiKey` populated for the tenant — the same Firebase Web API key the
  Portal already ships as `NEXT_PUBLIC_FIREBASE_API_KEY`. Until then the endpoint answers `503`.
- The user must already hold backup codes (`POST /api/mfa/backup-codes/generate`, issued at TOTP
  enrolment). Enrolment-time issuance is Portal work and is not built yet.
