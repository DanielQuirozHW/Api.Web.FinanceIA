# Plantillas de correo (SendGrid)

**La fuente de verdad es SendGrid.** Lo que se envía es la plantilla dinámica publicada en SendGrid;
esta carpeta es una **copia** de referencia. Cada vez que se publique una versión nueva de una
plantilla en SendGrid, hay que actualizar acá el archivo correspondiente en el mismo cambio. Estos
archivos no se compilan, no se publican y la API no los lee en tiempo de ejecución.

## Los tres correos

| Correo | Archivo | App Setting (global) | Override por tenant (`TenantSettings.SettingKey`) | `dynamic_template_data` que envía el código |
|---|---|---|---|---|
| Invitación | `invitation.es-AR.html` *(todavía no está en el repo)* | `SendGrid__InvitationTemplateId` | `email.invitation.template_id` | `inviterName`, `invitationLink` |
| Recuperación de contraseña | `password-reset.es-AR.html` | `SendGrid__PasswordResetTemplateId` | `email.password_reset.template_id` | `productName`, `resetLink`, `expiresInText` |
| Verificación de correo | `verify-email.es-AR.html` *(hoy es sólo un placeholder)* | `SendGrid__EmailVerificationTemplateId` | `email.email_verification.template_id` | `productName`, `verifyLink` |

Las claves salen de `Clases/SendGridEmailService.cs` y se escriben exactamente así, respetando
mayúsculas. La invitación manda **`invitationLink`**, no `inviteUrl`: una plantilla que use
`{{inviteUrl}}` sale con el botón vacío.

### Branding opcional (los tres correos)

| Clave de `TenantSettings` | Campo en la plantilla |
|---|---|
| `branding.company_name` | `companyName` |
| `branding.logo_url` | `logoUrl` |

Los dos campos se agregan **sólo si el tenant los tiene configurados**. Si no, no se mandan (ni
vacíos ni `null`), así que la plantilla tiene que tener un fallback con `{{#if companyName}}…{{else}}Financia{{/if}}`.
`branding.logo_url` tiene que ser una URL https absoluta y pública de un PNG; cualquier otra cosa se
ignora con un Warning. Detalle en `docs/guides/password-reset.md` › *Branding*.

## Qué plantilla se usa

Para cada correo, en este orden:

1. El override del tenant (`email.*.template_id`), si está cargado.
2. El App Setting global (`SendGrid__*TemplateId`).
3. Sólo para la **invitación**: un id fijo en el código (`SendGridEmailService`, `d-5f0dd166…`).
   Recuperación y verificación no tienen fallback: sin plantilla no se envía el correo y el
   resultado queda en el log (`SendFailed` / `TemplateMissing`; ver las guías de cada flujo).

## Guías relacionadas

- `docs/guides/password-reset.md`: enlace de recuperación, `portal.url`, `Cors__AllowedOrigins__N`.
- `docs/guides/email-verification.md`: enlace de verificación (misma regla de `portal.url`).
- `docs/guides/first-admin.md`: alta del primer Admin de un cliente nuevo.
