-- ============================================================
-- Project  : Api.Web.FinanceIA
-- Script   : 002_tenantfirebaseconfig_add_webapikey
-- DATABASE : "Default" (SHARED) — the one AppDbContext binds.
--            NOT a tenant business database. Run it ONCE, on the
--            shared DB, not once per tenant.
-- Purpose  : Add [WebApiKey] to [dbo].[TenantFirebaseConfig] —
--            the Firebase Web API key of each tenant's project.
--            Needed by POST /api/mfa/backup-codes/redeem, which
--            re-proves a user's first factor by calling Identity
--            Platform's accounts:signInWithPassword. That is the
--            only way a backend can authenticate a user stuck at
--            auth/multi-factor-auth-required: Firebase issues no
--            ID token until the second factor passes, and the
--            opaque mfaPendingCredential it does return cannot be
--            verified server-side by any API.
-- Notes    : NULLABLE and NOT ENCRYPTED, unlike [CredentialJson]
--            and [RecaptchaSecretKey] in the same table.
--            A Firebase Web API key is public by design — it ships
--            inside every browser bundle (the Portal exposes it as
--            NEXT_PUBLIC_FIREBASE_API_KEY) and identifies the
--            project rather than authorising anything by itself.
--            Encrypting it would imply a secrecy it does not have
--            and cost a decrypt on every tenant resolution.
--            Nullable so existing rows stay valid: a tenant with no
--            key simply cannot use the backup-code login, which
--            answers 503, and nothing else changes behaviour.
-- Author   : DBA / Dev Team
-- Date     : 2026-09-03
-- ============================================================
-- Run BEFORE deploying the matching application build. EF Core maps
-- this column unconditionally, so publishing first breaks EVERY read
-- of TenantFirebaseConfig with "Invalid column name 'WebApiKey'" —
-- and that table is read during tenant resolution, so the failure
-- would take down every authenticated endpoint, not just the new one.
--
-- AFTER running this, populate the column per tenant:
--     UPDATE [dbo].[TenantFirebaseConfig]
--        SET [WebApiKey] = N'<the project''s Web API key>'
--      WHERE [TenantId]  = N'<tenant slug>';
-- The value is the same one the Portal already uses for that tenant
-- (Firebase console > Project settings > General > Web API Key).
-- Until it is set the backup-code login answers 503 for that tenant;
-- every other endpoint is unaffected.
--
-- Restart the App Service afterwards so EF's model cache is built
-- against the column that now exists.
-- ============================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[TenantFirebaseConfig]')
      AND name      = N'WebApiKey'
)
BEGIN
    ALTER TABLE [dbo].[TenantFirebaseConfig]
        ADD [WebApiKey] NVARCHAR(200) NULL;

    PRINT 'Column TenantFirebaseConfig.WebApiKey added.';
END
ELSE
BEGIN
    PRINT 'Column TenantFirebaseConfig.WebApiKey already exists — skipped.';
END
GO

-- Documentation for the next person reading the schema, so the
-- plaintext storage is not later "fixed" into encryption.
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[TenantFirebaseConfig]')
      AND name      = N'WebApiKey'
)
AND NOT EXISTS (
    SELECT 1
    FROM sys.extended_properties
    WHERE major_id = OBJECT_ID(N'[dbo].[TenantFirebaseConfig]')
      AND minor_id = COLUMNPROPERTY(
                        OBJECT_ID(N'[dbo].[TenantFirebaseConfig]'),
                        N'WebApiKey',
                        'ColumnId')
      AND name     = N'MS_Description'
)
BEGIN
    EXEC sp_addextendedproperty
         @name       = N'MS_Description',
         @value      = N'Firebase Web API key for this tenant''s project. Stored in PLAINTEXT on purpose: a Web API key is public by design and ships in the Portal''s browser bundle. Used only to call Identity Platform accounts:signInWithPassword for the MFA backup-code login. NULL means that tenant cannot use backup-code login (endpoint answers 503).',
         @level0type = N'SCHEMA', @level0name = N'dbo',
         @level1type = N'TABLE',  @level1name = N'TenantFirebaseConfig',
         @level2type = N'COLUMN', @level2name = N'WebApiKey';

    PRINT 'Extended property added on TenantFirebaseConfig.WebApiKey.';
END
GO

PRINT 'Script 002_tenantfirebaseconfig_add_webapikey completed.';
GO
