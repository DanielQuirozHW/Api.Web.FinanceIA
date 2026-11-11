# First administrator of a new client (bootstrap)

A new client starts with an empty Financia database. Users only join by invitation, and only an Admin
can invite. `POST /api/bff/setup/first-admin` breaks that loop once. It issues an **Admin**
invitation with no inviter, through the same invitation service, token, 7-day expiry and SendGrid
email as the normal invite.

The endpoint is **off** unless an operator turns it on with an App Setting. It works only for a
tenant that has **no active Admin**. Turn it off again as soon as the admin has registered.

## Prerequisite (once per database)

DDL `docs/database/075_user_invitations_inviter_nullable.sql` must have run in the client's Financia
database **before** this API version is published. `--verify-schema` does not check nullability, so
confirm it by hand:

```sql
SELECT is_nullable FROM sys.columns
WHERE object_id = OBJECT_ID(N'[dbo].[UserInvitations]') AND name = N'InvitedByUserId';  -- expect 1
```

Without it, the call answers 500, and the API logs an Error that names script 075.

## Steps

1. **Turn it on.** In the client's App Service, add the Application Setting `Setup__BootstrapKey`
   with a random value of **at least 32 characters**, then save, which restarts the app. For
   example:

   ```bash
   openssl rand -base64 48
   ```

   If the value is shorter than 32 characters, or missing, the endpoint answers **404**, exactly like
   a route that does not exist.

2. **Call it** with the tenant slug and the key:

   ```bash
   curl -sS -X POST "https://<api-host>/api/bff/setup/first-admin" \
     -H "Content-Type: application/json" \
     -H "X-Tenant-ID: <tenant-slug>" \
     -H "X-Setup-Key: <the Setup__BootstrapKey value>" \
     -d '{ "email": "admin@client.com" }'
   ```

   `201 Created` answer:

   ```json
   {
     "invitationId": "…",
     "email": "admin@client.com",
     "expiresAt": "2026-11-01T12:00:00Z",
     "inviteUrl": "https://<portal>/register/invite?token=…",
     "emailSent": true
   }
   ```

   `inviteUrl` is exactly the link in the email, built from `AppSettings:FrontendUrl`. If
   `emailSent` is `false` (for example, SendGrid is not configured yet), the invitation still exists.
   Hand `inviteUrl` to the admin over a secure channel. **Treat it as a password**: it is single-use
   but grants Admin until it is used, revoked or expires (7 days).

3. **The admin opens the link** and registers. Use the same email; the Portal creates the Firebase
   account. They end up as `Admin` and can invite everyone else from Configuración → Usuarios.

4. **Turn it off.** Delete the `Setup__BootstrapKey` setting and save, which restarts the app. The
   endpoint goes back to 404.

## Answers

| Status | Meaning |
|---|---|
| 201 | Invitation created. Check `emailSent`. |
| 400 | No `X-Tenant-ID` header, or an invalid email. |
| 401 | `X-Setup-Key` missing or wrong. No further detail is given. |
| 404 | The endpoint is off (key unset or shorter than 32 chars), or the tenant is unknown. |
| 409 `admin_already_exists` | The tenant already has an **active** Admin, who should invite from the Portal. Inactive Admins and other roles do not block. |
| 409 `user_already_exists` | That email already has a user in the tenant. |
| 409 `invitation_pending` | An Admin already sent a live invitation to that email; resend or revoke it from the Portal. |
| 429 | More than 5 calls per minute from your IP (`RateLimiting:PerIP:FirstAdminSetup`). |

**Calling again for the same email** (for example, the link was lost) revokes the previous
bootstrap invitation and issues a new token and email. Bootstrap invitations show in the
invitations list with `invitedByName: "Configuración inicial"`.

## The invitation email per tenant (optional, DBA)

The first-admin email is the ordinary invitation email, so it follows the same per-tenant settings as
every invite and resend. All of them are optional `TenantSettings` rows with no default in code; a
tenant with none sends exactly what it always did.

| Key | Falls back to |
|---|---|
| `email.invitation.template_id` | `SendGrid:InvitationTemplateId`, then the built-in invitation template |
| `email.from.address` | `SendGrid:FromEmail`, then `noreply@financia.app` |
| `email.from.name` | `SendGrid:FromName`, then `FinanceIA` |
| `branding.company_name` | omitted from the template data |
| `branding.logo_url` | omitted from the template data |

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description]) VALUES
 (N'<tenant-slug>', N'email.invitation.template_id', N'd-xxxxxxxxxxxxxxxx',               N'Plantilla SendGrid de invitación.'),
 (N'<tenant-slug>', N'email.from.address',           N'no-reply@example.com',             N'Remitente verificado en SendGrid.'),
 (N'<tenant-slug>', N'email.from.name',              N'Movil SGR',                        N'Nombre visible del remitente.'),
 (N'<tenant-slug>', N'branding.company_name',        N'Movil SGR',                        N'Nombre de la empresa en los correos y en /auth/me.'),
 (N'<tenant-slug>', N'branding.logo_url',            N'https://cdn.example.com/logo.png', N'Logo PNG público (https) para los correos.');
```

`email.from.*` and `branding.*` are shared with the password reset and the email verification
(`password-reset.md`); seed them once per tenant. The sender must be verified in SendGrid. The
invitation link itself still comes from `AppSettings:FrontendUrl`; `portal.url` does not apply to it.

The invitation's `dynamic_template_data` is `inviterName` (`"Un administrador"` for a first-admin
invitation, which has no inviter), `invitationLink`, then `companyName` and `logoUrl` **only when
configured** — an unset key is omitted, never `""` or `null`:

```handlebars
{{#if logoUrl}}<img src="{{logoUrl}}" alt="{{#if companyName}}{{companyName}}{{else}}Financia{{/if}}" height="40">{{/if}}
<p>Equipo de {{#if companyName}}{{companyName}}{{else}}Financia{{/if}}</p>
```

`branding.logo_url` must be an **absolute https URL of a PNG at a public address** — Gmail and
Outlook do not render SVG, and a private URL shows a broken image. Anything that is not an absolute
https URL is ignored with a Warning (the key is logged, never the value) and the email goes without
`logoUrl`.

A failed `TenantSettings` read never costs the email: it is logged as a Warning and the global
configuration is used.

## What is recorded

- Audit row `Setup.FirstAdminInvited`, with the UserInvitation id and no user.
- Logs carry the tenant and invitation id only.
- The key, the token and the link are never logged or audited.
