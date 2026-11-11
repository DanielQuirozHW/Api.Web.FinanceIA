# Email verification via SendGrid

Two paths make a user's Firebase `emailVerified` true:

1. **Invited users: automatic.** `POST /api/bff/auth/register-invited` sets `EmailVerified = true`
   on the new user's Firebase account with the **tenant's** Firebase Admin app, right after the
   Users row is created. Holding the emailed invite token is the proof of mailbox control, so they
   never need a verification mail.
2. **Everyone else: on request.** `POST /api/bff/auth/email-verification` generates the verification
   link with the tenant's Firebase app and sends it through SendGrid with the tenant's template and
   sender. This replaces the Portal's direct call to Firebase `sendEmailVerification`, whose link opens
   Firebase's hosted page. Applying the code (`applyActionCode`) stays in the Portal.

**No DDL.** `EmailVerified` lives in Firebase, not SQL. The audit row uses a new `AuditLogs.Action`
value (no CHECK constraint on that column), and the per-tenant template is a new `TenantSettings`
key (free-form key/value, no in-code default).

## Invited users (register-invited)

The Firebase call is **fault-isolated**. If it fails, the endpoint logs a Warning carrying
`TenantId`, `UserId` and the exception type only, and the registration still answers 200. The
response tells the Portal which happened:

```json
{ "userId": "…", "tenantCode": "…", "role": "…", "creditLineMode": "sgr", "emailVerified": true }
```

| `emailVerified` | Meaning | Portal does |
|---|---|---|
| `true` | Firebase now marks the address verified | `await user.reload()`; send nothing |
| `false` | The Firebase call failed | Show the unverified banner; its resend button calls the endpoint below |

The field is present on register-invited only; `GET /api/bff/auth/me` does not read Firebase and
omits it.

## Contract

```
POST /api/bff/auth/email-verification
X-Tenant-ID: <tenant slug>          (required — TenantResolutionMiddleware answers 400 without it)
Cookie: bff_session=<internal JWT>  (Internal scheme; the CURRENT session user only)
(no body)
```

| Status | When | Body |
|---|---|---|
| **200** | Firebase already marks the address verified. Nothing is sent. | `{ "alreadyVerified": true }` |
| **202** | Email queued; the link and SendGrid run in the background | `{ "alreadyVerified": false }` |
| 401 | No session, or the session carries no `userId` | ProblemDetails |
| 403 | `Users.IsActive = 0` | ProblemDetails |
| 404 | The session's user is not a user of the resolved tenant | ProblemDetails |
| 429 | More than `EmailVerification:PerUserLimit` requests per `PerUserWindowMinutes` (3 per 15 min) | ProblemDetails, `errorCode: too_many_requests`, `retryAfter`, `Retry-After` header |
| 502 | The tenant's Firebase project cannot be reached | ProblemDetails, `errorCode: firebase_service_error` |

- The address is read from the Users row, never from the request.
- The per-user budget counts **every** request, including those answered 200. It therefore also
  caps how often one session can make the BFF call Firebase.
- A 202 means "queued", not "delivered". Firebase and SendGrid failures after the response are
  logged; they never reach the caller.

## The emailed link

The BFF generates the link with `ActionCodeSettings.Url = {portal}`, takes the `oobCode` out and
emails:

```
{portal}/verify-email?mode=verifyEmail&oobCode=<code>
```

`{portal}` is resolved by the same rule as the password reset, because both links carry a live code:

- the tenant's `portal.url` setting, when it is https (http only on localhost) and its origin is in
  `Cors:AllowedOrigins`;
- otherwise `AppSettings:FrontendUrl`.

Qué tiene que contener `Cors__AllowedOrigins__N` en cada App Service (el origen
`https://{slug}.financia.click` de su Portal, sin barra final): ver
[password-reset.md › Orígenes permitidos por App Service](password-reset.md#orígenes-permitidos-por-app-service-cors__allowedorigins__n).

## What happens to a request

Every request logs **one** line, `Email verification {Outcome} for tenant {TenantId} user {UserId}`.
It never carries the address or the link.

| Outcome | Logged by | Meaning | Email | Audit |
|---|---|---|---|---|
| `AlreadyVerified` | controller (200) | Firebase already marks the address verified | no | no |
| `Sent` | worker | Link generated, SendGrid accepted | yes | `User.EmailVerificationRequested` (UserId, IP, trace id; no email) |
| `TemplateMissing` | worker | No template in the tenant setting or `SendGrid:EmailVerificationTemplateId`. Checked **before** a link is generated | no | no |
| `LinkFailed` | worker | No usable Portal URL, Firebase failed or has no such account, or the link had no `oobCode` (Error log) | no | no |
| `SendFailed` | worker | SendGrid rejected the email or is unreachable (Error log) | no | no |

## Configuration

| Value | Tenant setting (`TenantSettings`) | Global fallback | Last resort |
|---|---|---|---|
| Template | `email.email_verification.template_id` | `SendGrid:EmailVerificationTemplateId` | **none**: `TemplateMissing` |
| From address | `email.from.address` | `SendGrid:FromEmail` | `noreply@financia.app` |
| From name | `email.from.name` | `SendGrid:FromName` | `Financia` |
| Portal URL | `portal.url` (allow-listed) | `AppSettings:FrontendUrl` | `LinkFailed` |

There is deliberately no hardcoded template id: sending an unrelated template would deliver the
wrong email.

Template dynamic data:
- `productName`: `"Financia"` (`EmailVerification:ProductName`)
- `verifyLink`: the link above

Other settings live in the `EmailVerification` section:
- `PerUserLimit` (3)
- `PerUserWindowMinutes` (15)
- `QueueCapacity` (100). Past it, a request still answers 202 but is dropped with a Warning.
- `ProductName`
- `VerifyPath` (`/verify-email`)

## Deploy prerequisites

1. **SendGrid template.** Create a dynamic template from
   `Api.Web.FinanceIA/docs/email-templates/verify-email.es-AR.html` (subject:
   `Confirmá tu correo en {{productName}}`), then set `SendGrid__EmailVerificationTemplateId` in the
   App Service settings. Until it is set, requests answer 202 and log `TemplateMissing`; no email goes
   out.
2. **Firebase authorized domain.** Add the Portal domain under Authentication → Settings →
   Authorized domains in each tenant's Firebase project. The password reset already requires this.
   Without it, every request logs `LinkFailed` (`UnauthorizedContinueUrl`).
3. **Portal.** It needs a `/verify-email` route (see below); until then the emailed link lands on a
   404. Ship the Portal before, or together with, the template id.

No DDL, so there is no ordering constraint against the DBA.

## Per-tenant override (optional, DBA)

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description]) VALUES
 (N'<tenant-slug>', N'email.email_verification.template_id', N'd-xxxxxxxxxxxxxxxx', N'Plantilla SendGrid de verificación de correo.');
```

`portal.url`, `email.from.address` and `email.from.name` are shared with the password reset; see
`password-reset.md`.

## Branding (optional, DBA)

`branding.company_name` and `branding.logo_url` are shared with the password reset and the
invitation (details in `password-reset.md`). Neither has a default in code.

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description]) VALUES
 (N'<tenant-slug>', N'branding.company_name', N'Movil SGR',                          N'Nombre de la empresa en los correos y en /auth/me.'),
 (N'<tenant-slug>', N'branding.logo_url',     N'https://cdn.example.com/logo.png',   N'Logo PNG público (https) para los correos.');
```

The verification email's `dynamic_template_data` is `productName`, `verifyLink`, then `companyName`
and `logoUrl` **only when configured** — an unset key is omitted, never `""` or `null`, so a tenant
with no rows sends the same payload as before:

```handlebars
{{#if logoUrl}}<img src="{{logoUrl}}" alt="{{#if companyName}}{{companyName}}{{else}}Financia{{/if}}" height="40">{{/if}}
<p>Equipo de {{#if companyName}}{{companyName}}{{else}}Financia{{/if}}</p>
```

`branding.logo_url` must be an **absolute https URL of a PNG at a public address** — Gmail and
Outlook do not render SVG, and a private URL shows a broken image. Anything that is not an absolute
https URL is ignored with a Warning (the key is logged, never the value) and the email goes without
`logoUrl`.

## Portal follow-up (PortalesHWA/Portal-FinanceIA, branch from `develop`)

- **New `/verify-email` route.** Read `oobCode` from the query string, then call
  `applyActionCode(getFirebaseAuth(), oobCode)` followed by `reloadEmailVerified()`. Map
  `auth/expired-action-code` and `auth/invalid-action-code` to Spanish messages, the way
  `recovery/steps/Step3Key.tsx` does for the reset.
- **`register/invite/page.tsx`.** Read `emailVerified` from the register-invited response. When it
  is `true`, call `user.reload()` and skip
  `emailVerificationService.sendVerificationEmail(cred.user)`. When it is `false`, the banner
  covers it.
- **Resend button (`EmailVerificationActions.tsx`).** Replace the browser `sendEmailVerification`
  with `POST /api/bff/auth/email-verification`:
  - 200 `alreadyVerified` → reload;
  - 202 → "te enviamos un correo";
  - 429 → "demasiados intentos" (use `Retry-After`).
