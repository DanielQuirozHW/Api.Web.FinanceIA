// Project : Api.Web.FinanceIA
// Created : 2026-08-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Single source of truth for <see cref="TenantSettings"/> keys and their in-code
/// defaults. Defaults deliberately live here, not in the database: a tenant with no
/// row must resolve to a sensible default and must never fail, which lets us add
/// settings later without inserting a row for every existing tenant.
/// </summary>
public static class TenantSettingKeys
{
    // ── creditline.mode ─────────────────────────────────────────────────────────

    /// <summary>
    /// Controls how the frontend renders a credit line (cupo).
    /// Presentation only — it never changes server-side validation.
    /// </summary>
    public const string CreditLineMode = "creditline.mode";

    /// <summary>SGR mode: a cupo (tope) with typed operations beneath it. This is the default.</summary>
    public const string CreditLineModeSgr = "sgr";

    /// <summary>Simple mode: the granted amount IS the cupo, with a single untyped operation beneath it.</summary>
    public const string CreditLineModeSimple = "simple";

    /// <summary>Default credit-line mode when the tenant has no <see cref="CreditLineMode"/> row.</summary>
    public const string CreditLineModeDefault = CreditLineModeSgr;

    // ── Tenant profile (§7.0, Layer 1 of the scoring redesign) ──────────────────
    //
    // The tenant profile is what kind of entity the tenant is plus which OPTIONAL
    // integrations it may use. It is resolved by ITenantProfileService, NOT read key by
    // key: the three integration keys have no fixed default of their own — their default
    // is whatever the preset for the tenant's entity type says (TenantProfilePresets), and
    // an explicit row for one of them overrides that preset value for that key only.
    //
    // ARCA and BCRA have NO key here on purpose. They are base integrations, always on,
    // never parametrised; the absence of a key is the statement of that rule.

    /// <summary>
    /// What kind of entity the tenant is: <see cref="EntityTypeSgr"/> or
    /// <see cref="EntityTypeNonSgr"/>. Selects the preset that supplies the default for
    /// every <c>integration.*.enabled</c> key the tenant has not overridden. Extensible:
    /// an unknown stored value resolves to <see cref="EntityTypeDefault"/>.
    /// </summary>
    public const string EntityType = "entity.type";

    /// <summary>An SGR (Sociedad de Garantía Recíproca). This is the default.</summary>
    public const string EntityTypeSgr = "sgr";

    /// <summary>A lender that is not an SGR (bank, fintech, mutual, cooperative).</summary>
    public const string EntityTypeNonSgr = "non_sgr";

    /// <summary>Default entity type when the tenant has no <see cref="EntityType"/> row.</summary>
    public const string EntityTypeDefault = EntityTypeSgr;

    /// <summary>Whether the LUFE (company/partner registry) integration is enabled for the tenant.</summary>
    public const string IntegrationLufeEnabled = "integration.lufe.enabled";

    /// <summary>Whether the CASFOG (guarantee-fund standing) integration is enabled for the tenant.</summary>
    public const string IntegrationCasfogEnabled = "integration.casfog.enabled";

    /// <summary>Whether the NOSIS (credit bureau) integration is enabled for the tenant.</summary>
    public const string IntegrationNosisEnabled = "integration.nosis.enabled";

    // ── Portal and email identity (invitation, password reset, email verification) ──
    //
    // Read by UserInvitationService, PasswordResetService and EmailVerificationService (portal.url by
    // the last two only — the invitation link stays on AppSettings:FrontendUrl). Every one of them is
    // OPTIONAL and has no in-code default of its own: a tenant with no row falls back to the global
    // configuration (AppSettings:FrontendUrl, SendGrid:FromEmail / FromName / InvitationTemplateId /
    // PasswordResetTemplateId / EmailVerificationTemplateId). Seeded by the DBA only — no endpoint
    // writes them. See docs/guides/first-admin.md, docs/guides/password-reset.md and
    // docs/guides/email-verification.md.

    /// <summary>
    /// Base URL of the tenant's Portal, e.g. <c>https://portal.example.com</c>. Reset links point
    /// at <c>{portal.url}/recover</c>, verification links at <c>{portal.url}/verify-email</c>. Accepted only when it is https (http only for localhost) and
    /// its origin is listed in <c>Cors:AllowedOrigins</c>, because the link carries a live oobCode;
    /// anything else is ignored with a Warning and the global FrontendUrl is used.
    /// </summary>
    public const string PortalUrl = "portal.url";

    /// <summary>Sender address for the tenant's transactional email. Must be a verified SendGrid sender.</summary>
    public const string EmailFromAddress = "email.from.address";

    /// <summary>Sender display name for the tenant's transactional email.</summary>
    public const string EmailFromName = "email.from.name";

    /// <summary>SendGrid dynamic template id (<c>d-…</c>) for the tenant's password-reset email.</summary>
    public const string EmailPasswordResetTemplateId = "email.password_reset.template_id";

    /// <summary>
    /// SendGrid dynamic template id (<c>d-…</c>) for the tenant's email-verification email. Falls back
    /// to <c>SendGrid:EmailVerificationTemplateId</c>; with neither set nothing is sent.
    /// </summary>
    public const string EmailEmailVerificationTemplateId = "email.email_verification.template_id";

    /// <summary>
    /// SendGrid dynamic template id (<c>d-…</c>) for the tenant's invitation email, first-admin
    /// bootstrap included. Falls back to <c>SendGrid:InvitationTemplateId</c>, then to the
    /// historical built-in invitation template.
    /// </summary>
    public const string EmailInvitationTemplateId = "email.invitation.template_id";

    // ── Branding (every transactional email, /auth/me and the PDF reports) ─────
    //
    // Read by the three email senders through TenantEmailSettings, branding.company_name also by
    // ITenantSettingsService.GetTenantDisplayNameAsync for /auth/me and the PDF Author, and all three by
    // ITenantPdfBrandingProvider for the co-branded PDF header (docs/guides/pdf-branding.md). NO in-code
    // default: absent, the email omits the template field (so a template can say
    // {{#if companyName}}…{{else}}Financia{{/if}}), /auth/me returns tenantDisplayName = null and the PDF keeps
    // the Financia header. The PDF header switches to the client ONLY with a usable logo: a company name or an
    // accent alone changes nothing there. Seeded by the DBA only.

    /// <summary>Display name of the client company, e.g. <c>Movil SGR</c>. Sent to templates as <c>companyName</c>.</summary>
    public const string BrandingCompanyName = "branding.company_name";

    /// <summary>
    /// Public absolute https URL of a PNG logo for emails. Sent to templates as <c>logoUrl</c>.
    /// Anything that is not an absolute https URL is ignored with a Warning. PNG, not SVG: Gmail and
    /// Outlook do not render SVG. The PDF reports also fetch it server-side (BrandingLogoFetcher): there it
    /// must be on port 443, resolve to a public address, answer without a redirect, weigh at most 1 MB, be a
    /// PNG or JPEG by its content and measure at most 4000×4000 px; anything else is ignored with a Warning
    /// (host and reason only) and the PDF keeps the Financia header. Cached 6 h per URL (a refusal 15 min).
    /// </summary>
    public const string BrandingLogoUrl = "branding.logo_url";

    /// <summary>
    /// Optional <c>#RRGGBB</c> accent for the co-branded PDF: it replaces the Financia accent (the header rule,
    /// accent labels, chart accents) and its derived tint, in the light and dark themes alike. Applied ONLY
    /// together with a usable <see cref="BrandingLogoUrl"/>, and only when it reaches 4.5:1 against the export
    /// theme's paper and surface — checked per theme, so one colour may apply to light exports and not to dark
    /// ones. Anything else is ignored with a Warning. Not used by the emails.
    /// </summary>
    public const string BrandingAccentColor = "branding.accent_color";

    // ── SGR Fondo de Riesgo (portfolio dashboard, sgr section) ─────────────────
    //
    // Read and written by ISgrSettingsService (GET/PUT /api/bff/settings/sgr, Admin only); read by
    // the portfolio dashboard's sgr section. NO in-code default: an absent row means "not configured",
    // and the dashboard says so rather than inventing a fund. Amounts are stored as invariant-culture
    // decimals, dates as yyyy-MM-dd (Argentine calendar dates).

    /// <summary>Fondo de Riesgo disponible, in ARS. Positive.</summary>
    public const string SgrFondoRiesgoDisponible = "sgr.fondo_riesgo_disponible";

    /// <summary>The date <see cref="SgrFondoRiesgoDisponible"/> is stated at. Never in the future.</summary>
    public const string SgrFondoRiesgoFecha = "sgr.fondo_riesgo_fecha";

    /// <summary>Optional ARS per USD rate the sgr section converts USD guarantees with. Positive.</summary>
    public const string SgrTipoCambioUsd = "sgr.tipo_cambio_usd";

    /// <summary>The date <see cref="SgrTipoCambioUsd"/> is stated at. Present exactly when the rate is.</summary>
    public const string SgrTipoCambioFecha = "sgr.tipo_cambio_fecha";

    // ── Persona humana: expected documents per fiscal regime ────────────────────
    //
    // Read and written by IPersonExpectedDocumentsService (GET/PUT
    // /api/bff/settings/person-expected-documents). The value is JSON:
    //   { "regimes": { "Monotributo": [line…], "ResponsableInscripto": [line…], "ExentoOrDesconocido": [line…] } }
    //   line = { "documentType", "requirement": "required"|"optional", "alternatives": [], "maxAgeMonths": int|null }
    // The in-code default is LegajoExpectedDocuments.Defaults, applied PER REGIME: a regime missing from the
    // JSON uses its default, and a row that fails to parse or validate uses the defaults for every regime.

    /// <summary>The tenant's expected-documents matrix for the persona humana legajo (JSON).</summary>
    public const string PersonExpectedDocuments = "person.expected_documents";

    /// <summary>
    /// Stored form of a boolean setting value. The reader is more lenient (trimmed,
    /// case-insensitive <c>bool.TryParse</c>), but a writer and the DBA snippets in
    /// docs/guides/tenant-profile.md should store exactly these literals.
    /// </summary>
    public const string BoolTrue = "true";

    /// <inheritdoc cref="BoolTrue"/>
    public const string BoolFalse = "false";
}
