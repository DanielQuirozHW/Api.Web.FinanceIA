# Password reset via SendGrid

`POST /api/bff/auth/password-reset` replaces the Portal's direct call to Firebase
`sendPasswordResetEmail` (RecoverView step 1). The BFF generates the reset link with the **tenant's**
Firebase Admin app and sends it through SendGrid with the tenant's template and sender. Steps 2–3
(`verifyPasswordResetCode` / `confirmPasswordReset`) stay in the Portal.

**No DDL.** The audit row uses a new `AuditLogs.Action` value (no CHECK constraint on that column),
and the per-tenant settings are new `TenantSettings` keys (free-form key/value, defaults in code).

## Contract

```
POST /api/bff/auth/password-reset
X-Tenant-ID: <tenant slug>          (required — TenantResolutionMiddleware answers 400 without it)
Content-Type: application/json

{ "email": "user@example.com", "recaptchaToken": "<reCAPTCHA v3 token>" }
```

| Status | When | Body |
|---|---|---|
| **202** | Every well-formed request that passes reCAPTCHA and the limits — **whether or not the address belongs to an active user** | `{ "message": "Si el correo corresponde a una cuenta activa, vas a recibir un enlace para restablecer tu contraseña." }` |
| 400 | Missing/malformed email (max 256) or missing reCAPTCHA token | ProblemDetails |
| 403 | reCAPTCHA failed or score < `PasswordReset:RecaptchaMinScore` (0.5) | ProblemDetails, `errorCode: recaptcha_failed` |
| 429 | Per-IP (`RateLimiting:PerIP:PasswordReset`, 5/min) or per-email (`PasswordReset:PerEmailLimit` per `PerEmailWindowMinutes`, 3 per 15 min) | ProblemDetails, `errorCode: rate_limit_exceeded_ip` / `rate_limit_exceeded_email`, `Retry-After` header |
| 502 | Google reCAPTCHA unreachable | ProblemDetails, `errorCode: recaptcha_error` |

Note the 403: the other anonymous endpoints answer a failed reCAPTCHA with 422; this one follows the
security spec (`multi-tenant-bff.md`) instead. The Portal should treat both as "verification failed".

### No account enumeration

- The 202 body is constant, and it returns **before** anything about the address is looked up: the
  controller only checks reCAPTCHA and the limits, then queues the request. The user lookup, Firebase
  and SendGrid run in `PasswordResetWorker`, so a known and an unknown address also answer in the
  same time.
- The per-email limit counts every address, known or not.
- Firebase and SendGrid failures never reach the caller — they are logged at Error.
- Logs carry `TenantId` and an email **tag** (`EmailHasher`: 16 hex chars of SHA-256), never the
  address, and never the link.

## The emailed link

Firebase's own link opens its hosted action page, which would reset the password there and skip the
Portal. So the BFF generates the link with `ActionCodeSettings.Url = {portal}/recover`, takes the
`oobCode` out, and emails:

```
{portal}/recover?mode=resetPassword&oobCode=<code>&lang=es-AR
```

The query names are the ones Firebase uses for a custom action handler. The link expires after
**1 hour** (Firebase's fixed lifetime for reset codes) and works once.

`{portal}` is the tenant's `portal.url` setting when it is https (http only on localhost) and its
origin is in `Cors:AllowedOrigins`; otherwise `AppSettings:FrontendUrl`.

## What happens to a request

| Outcome (logged) | Meaning | Email | Audit |
|---|---|---|---|
| `Sent` | Active user of the tenant, link generated, SendGrid accepted | yes | `User.PasswordResetRequested` (UserId, IP, trace id — no email) |
| `UnknownEmail` | No user with that address in this tenant | no | no |
| `InactiveUser` | `Users.IsActive = 0` | no | no |
| `FirebaseUserMissing` | Users row exists, tenant's Firebase project has no such account | no | no |
| `LinkFailed` | Config, DB or Firebase error before a link existed (Error log) | no | no |
| `SendFailed` | SendGrid error, incl. no template configured (Error log) | no | no |

## Deploy prerequisites

1. **SendGrid template.** Create a dynamic template from
   `Api.Web.FinanceIA/docs/email-templates/password-reset.es-AR.html` (subject:
   `Restablecé tu contraseña de {{productName}}`) and set `SendGrid__PasswordResetTemplateId` in the App
   Service settings. Until it is set, requests answer 202 and log `SendFailed` — no email goes out.
2. **Firebase authorized domain.** In **each tenant's** Firebase project, add the Portal domain
   (the host of `{portal}`) under Authentication → Settings → Authorized domains. Without it Firebase
   rejects the continue URL (`UnauthorizedContinueUrl`) and every request logs `LinkFailed`.
3. **Portal.** RecoverView step 1 must call this endpoint (see below) — until then nothing calls it.

No DDL, so no ordering constraint against the DBA.

## Per-tenant overrides (optional, DBA)

All four keys are optional; a tenant with no row uses the global configuration.

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description]) VALUES
 (N'<tenant-slug>', N'portal.url',                       N'https://portal.example.com', N'Base URL del Portal para enlaces de recuperación.'),
 (N'<tenant-slug>', N'email.from.address',               N'no-reply@example.com',       N'Remitente verificado en SendGrid.'),
 (N'<tenant-slug>', N'email.from.name',                  N'Financia',                   N'Nombre visible del remitente.'),
 (N'<tenant-slug>', N'email.password_reset.template_id', N'd-xxxxxxxxxxxxxxxx',         N'Plantilla SendGrid de recuperación de contraseña.');
```

A `portal.url` whose origin is not in `Cors:AllowedOrigins` is ignored with a Warning — the link
carries a live reset code and must never point at a host the API does not serve.

## Orígenes permitidos por App Service (`Cors__AllowedOrigins__N`)

`Cors:AllowedOrigins` decide **dos** cosas a la vez: qué orígenes acepta CORS (con credenciales,
para la cookie httpOnly) y qué `portal.url` de un tenant es válido para armar los enlaces de
recuperación de contraseña y de verificación de correo. Desde que `appsettings.json` lista sólo
`http://localhost:3000` (para desarrollo local), **ambas cosas dependen únicamente de los App Settings
de cada App Service**:

- Cada App Service tiene que declarar el origen de su Portal: `Cors__AllowedOrigins__0` =
  `https://{slug}.financia.click` (por ejemplo, `https://sgr-portales.financia.click`). Si el cliente
  usa más de un Portal, se agregan `Cors__AllowedOrigins__1`, `__2`, etc.
- El valor es un **origen**: esquema + host (+ puerto si no es el por defecto). Sin ruta y **sin barra
  final**: `https://x.financia.click/` nunca coincide con el encabezado `Origin` del navegador. Al
  arrancar, la API registra un Warning con la cantidad y los índices de los valores que no son un
  origen limpio (nunca los valores).
- Un `portal.url` de tenant cuyo origen no esté en esa lista se ignora (Warning) y el enlace usa
  `AppSettings:FrontendUrl`. Por eso `AppSettings__FrontendUrl` también tiene que apuntar al Portal de
  ese App Service.
- `Cors__AllowedOrigins__0` en Azure reemplaza sólo el índice 0 de `appsettings.json`; los demás
  índices que no se configuren no existen.

## Branding (optional, DBA)

Two more keys, shared by every transactional email (invitation, password reset, email verification).
Neither has a default in code.

| Key | Template field | Example |
|---|---|---|
| `branding.company_name` | `companyName` | `Movil SGR` |
| `branding.logo_url` | `logoUrl` | `https://cdn.example.com/logo.png` |

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description]) VALUES
 (N'<tenant-slug>', N'branding.company_name', N'Movil SGR',                          N'Nombre de la empresa en los correos y en /auth/me.'),
 (N'<tenant-slug>', N'branding.logo_url',     N'https://cdn.example.com/logo.png',   N'Logo PNG público (https) para los correos.');
```

The reset email's `dynamic_template_data` is `productName`, `resetLink`, `expiresInText`, then
`companyName` and `logoUrl` **only when configured**. An unset key is omitted, never sent as `""` or
`null`, so a tenant with no rows sends exactly the payload it always did, and the template can fall
back with `{{#if}}`:

```handlebars
{{#if logoUrl}}<img src="{{logoUrl}}" alt="{{#if companyName}}{{companyName}}{{else}}Financia{{/if}}" height="40">{{/if}}
<p>Equipo de {{#if companyName}}{{companyName}}{{else}}Financia{{/if}}</p>
```

`branding.logo_url` must be an **absolute https URL of a PNG at a public address** — Gmail and
Outlook do not render SVG, and a private URL shows a broken image. Anything that is not an absolute
https URL is ignored with a Warning (the key is logged, never the value) and the email goes without
`logoUrl`.

`branding.company_name` is also returned to the Portal as `tenantDisplayName` on `GET /api/bff/auth/me`
(`null` when unset).

## Portal follow-up (PortalesHWA/Portal-FinanceIA, branch from `develop`)

RecoverView step 1: replace `sendPasswordResetEmail(auth, email)` with
`POST /api/bff/auth/password-reset` (`X-Tenant-ID` header, `{ email, recaptchaToken }`), and show the
202 `message` for every 202. Map 403 to "verificación fallida" and 429 to "demasiados intentos"
(use `Retry-After`). Steps 2–3 keep reading `oobCode` (and `mode`) from the `/recover` query string.
