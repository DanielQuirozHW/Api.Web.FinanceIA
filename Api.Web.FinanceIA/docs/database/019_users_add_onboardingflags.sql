-- ============================================================================
-- Script  : 019_users_add_onboardingflags.sql
-- Purpose : Add nullable OnboardingFlagsJson column to Users
-- Date    : 2026-06-08
-- Author  : Claude (assisted)
-- Notes   : Stores per-user onboarding/coach-mark flags as a JSON blob.
--           Existing rows keep OnboardingFlagsJson = NULL (no flags seen,
--           portal shows tours). New flags are merged on write.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Users]')
      AND  name      = N'OnboardingFlagsJson'
)
BEGIN
    ALTER TABLE [dbo].[Users]
        ADD [OnboardingFlagsJson] NVARCHAR(MAX) NULL;

    PRINT 'Column [OnboardingFlagsJson] added to [dbo].[Users].';
END
ELSE
BEGIN
    PRINT 'Column [OnboardingFlagsJson] already exists on [dbo].[Users]. Skipped.';
END
GO
