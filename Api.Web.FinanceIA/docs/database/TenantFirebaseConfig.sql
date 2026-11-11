-- ============================================================
-- Project : Api.Web.ServiciosFinancieros
-- Script  : TenantFirebaseConfig
-- Purpose : Create TenantFirebaseConfig table to store
--           Firebase project configuration per tenant.
--           CredentialJson and RecaptchaSecretKey are
--           stored encrypted with AES-256.
-- Author  : DBA / Dev Team
-- Date    : 2025-01-01
-- ============================================================

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.objects
        WHERE object_id = OBJECT_ID(N'[dbo].[TenantFirebaseConfig]')
        AND type = N'U'
    )
    BEGIN
        CREATE TABLE [dbo].[TenantFirebaseConfig]
        (
            [Id]                 INT           NOT NULL IDENTITY(1,1),
            [Cliente]            NVARCHAR(100) NOT NULL,
            [ProjectId]          NVARCHAR(200) NOT NULL,
            [CredentialJson]     NVARCHAR(MAX) NOT NULL,
            [RecaptchaSecretKey] NVARCHAR(500) NOT NULL,
            [IsActive]           BIT           NOT NULL
                CONSTRAINT [DF_TenantFirebaseConfig_IsActive]
                DEFAULT (1),
            [CreatedAt]          DATETIME2(7)  NOT NULL
                CONSTRAINT [DF_TenantFirebaseConfig_CreatedAt]
                DEFAULT (GETUTCDATE()),
            [UpdatedAt]          DATETIME2(7)  NOT NULL
                CONSTRAINT [DF_TenantFirebaseConfig_UpdatedAt]
                DEFAULT (GETUTCDATE()),

            CONSTRAINT [PK_TenantFirebaseConfig]
                PRIMARY KEY CLUSTERED ([Id] ASC),

            CONSTRAINT [UQ_TenantFirebaseConfig_Cliente]
                UNIQUE NONCLUSTERED ([Cliente] ASC),

            CONSTRAINT [UQ_TenantFirebaseConfig_ProjectId]
                UNIQUE NONCLUSTERED ([ProjectId] ASC),

            CONSTRAINT [CK_TenantFirebaseConfig_Cliente_Format]
                CHECK (
                    [Cliente] NOT LIKE '%[^a-zA-Z0-9-]%'
                    AND LEN([Cliente]) >= 3
                ),

            CONSTRAINT [CK_TenantFirebaseConfig_ProjectId_NotEmpty]
                CHECK (LEN(LTRIM(RTRIM([ProjectId]))) > 0),

            CONSTRAINT [CK_TenantFirebaseConfig_CredentialJson_NotEmpty]
                CHECK (LEN(LTRIM(RTRIM([CredentialJson]))) > 0),

            CONSTRAINT [CK_TenantFirebaseConfig_RecaptchaSecretKey_NotEmpty]
                CHECK (LEN(LTRIM(RTRIM([RecaptchaSecretKey]))) > 0)
        );

        -- Frequent lookup by Cliente (tenant identifier)
        CREATE NONCLUSTERED INDEX [IX_TenantFirebaseConfig_Cliente]
            ON [dbo].[TenantFirebaseConfig] ([Cliente] ASC)
            INCLUDE ([ProjectId], [IsActive]);

        -- Active records lookup
        CREATE NONCLUSTERED INDEX [IX_TenantFirebaseConfig_IsActive]
            ON [dbo].[TenantFirebaseConfig] ([IsActive] ASC)
            INCLUDE ([Cliente], [ProjectId]);

        -- Column descriptions
        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'Tenant identifier. Maps to Credenciales.cliente.',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'TenantFirebaseConfig',
            @level2type = N'COLUMN', @level2name = N'Cliente';

        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'Firebase project identifier.',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'TenantFirebaseConfig',
            @level2type = N'COLUMN', @level2name = N'ProjectId';

        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'Firebase service account JSON. AES-256 encrypted.',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'TenantFirebaseConfig',
            @level2type = N'COLUMN', @level2name = N'CredentialJson';

        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'reCAPTCHA V3 secret key. AES-256 encrypted.',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'TenantFirebaseConfig',
            @level2type = N'COLUMN', @level2name = N'RecaptchaSecretKey';

        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = N'Soft delete. 0 = inactive, 1 = active.',
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE',  @level1name = N'TenantFirebaseConfig',
            @level2type = N'COLUMN', @level2name = N'IsActive';

        PRINT 'TenantFirebaseConfig table created successfully.';
    END
    ELSE
    BEGIN
        PRINT 'TenantFirebaseConfig already exists. Skipping.';
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    ROLLBACK TRANSACTION;

    DECLARE @errorMessage NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @errorLine    INT            = ERROR_LINE();

    PRINT 'Error creating TenantFirebaseConfig at line ' +
          CAST(@errorLine AS NVARCHAR(10)) +
          ': ' + @errorMessage;

    THROW;
END CATCH;