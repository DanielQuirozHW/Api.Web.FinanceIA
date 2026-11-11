-- DATA FIX, no schema change
-- ============================================================================
-- Script  : 084_entities_businessname_strip_wrapping_quotes.sql
-- Purpose : Strip the quote characters that WRAP a stored [dbo].[Entities].
--           [BusinessName] — e.g. '"silo Chico S.A."' -> 'silo Chico S.A.' —
--           so rows created before BusinessNameNormalizer read the way new
--           rows are now written.
-- Date    : 2026-11-06
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS IS A DATA SCRIPT (DML), NOT A SCHEMA SCRIPT — like 066, 068
--           and 082. It changes no object, so the API does NOT depend on it and
--           the --verify-schema gate neither needs nor sees it. It is OPTIONAL
--           and SAFE TO RUN BEFORE OR AFTER THE DEPLOY: the new code normalizes
--           a name on create / update and reads any stored name fine. NO App
--           Service restart is needed.
--
--           RUN IT ONCE IN EACH Financia (tenant business) DATABASE. It covers
--           every TenantId found in the database it runs in (the TenantId is in
--           every result set so the DBA sees whose rows they are).
--
--           TWO STEPS, ONE FILE. With @Apply = 0 (the default) it only READS:
--             1. Result set 1 — the rows it WOULD fix: TenantId, EntityId, the
--                current BusinessName and the ProposedName.
--             2. Result set 2 — rows that need MANUAL REVIEW and are never
--                updated (see below).
--           Review them, then set @Apply = 1 and run again: the same rows are
--           recomputed and updated in ONE transaction (XACT_ABORT ON), and the
--           number of rows written is printed.
--
--           WHAT COUNTS AS WRAPPED. The trimmed name starts AND ends with a
--           quote of the same family —
--             double: " “ ” „ ‟ « »    (U+0022 U+201C U+201D U+201E U+201F U+00AB U+00BB)
--             single: ' ‘ ’ ‚ ‛ ‹ ›    (U+0027 U+2018 U+2019 U+201A U+201B U+2039 U+203A)
--           — and the text between them contains NO other quote of that family.
--           Layers are peeled one at a time ('«"Silo"»' -> 'Silo'). Characters
--           are compared by code point (UNICODE / a BIN2 collation), so the
--           result never depends on the database collation.
--
--           MANUAL REVIEW. A name that is wrapped but still has a same-family
--           quote INSIDE ('"Sociedad "La Esperanza" S.A."', or '"Uno" y "Dos"')
--           is listed in result set 2 and left alone: deciding whether the outer
--           pair really wraps needs the pairing rule the application applies
--           (BusinessNameNormalizer), which this script does not reimplement.
--           Fix those by hand, or by editing the PyME in the Portal — a PUT now
--           normalizes the name.
--
--           WHAT IT DOES NOT TOUCH.
--             - Letter case, whitespace inside the name, any other column.
--             - [UpdatedAt]: left as is, so list ordering does not jump.
--             - Vendor payloads (FiscalDataJson, LufeDataJson, …).
--             - Copies of the name elsewhere: PersonLegajos.FullName (persona
--               humana legajos, copied at creation), historical Notifications
--               titles/bodies, AuditLogs. They are listed in the PR; check
--               PersonLegajos.FullName by hand if a fixed row is a persona humana.
--           A fix that would leave an EMPTY name is never written.
--
--           ⚠ It BYPASSES the application audit trail (no AuditLogs row).
--           Record the run (database, date, who, rows) in README.md's table.
--
--           IDEMPOTENT: a second run finds nothing wrapped and writes nothing.
--           CONCURRENCY: the UPDATE matches the BusinessName it previewed, so a
--           name edited in the Portal meanwhile is skipped, never overwritten.
--
--           Rationale: docs/db-changes/084_entities_businessname_strip_wrapping_quotes.md
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 0 = preview only (default). 1 = write the fixes listed in result set 1.
DECLARE @Apply BIT = 0;

DECLARE @DoubleQuotes NVARCHAR(20) =
      NCHAR(34) + NCHAR(8220) + NCHAR(8221) + NCHAR(8222) + NCHAR(8223) + NCHAR(171) + NCHAR(187);
DECLARE @SingleQuotes NVARCHAR(20) =
      NCHAR(39) + NCHAR(8216) + NCHAR(8217) + NCHAR(8218) + NCHAR(8219) + NCHAR(8249) + NCHAR(8250);

-- LIKE patterns '%[<family>]%' compared under a BIN2 collation: exact code points.
DECLARE @DoublePattern NVARCHAR(30) = N'%[' + @DoubleQuotes + N']%';
DECLARE @SinglePattern NVARCHAR(30) = N'%[' + @SingleQuotes + N']%';

IF OBJECT_ID(N'tempdb..#Fix') IS NOT NULL DROP TABLE #Fix;

CREATE TABLE #Fix
(
    TenantId      NVARCHAR(100)    NOT NULL,
    EntityId      UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    CurrentName   NVARCHAR(300)    NOT NULL,
    ProposedName  NVARCHAR(300)    NOT NULL,
    Layers        INT              NOT NULL,
    NeedsReview   BIT              NOT NULL
);

-- Candidates: the trimmed name starts and ends with a quote of the SAME family.
INSERT INTO #Fix (TenantId, EntityId, CurrentName, ProposedName, Layers, NeedsReview)
SELECT e.TenantId, e.EntityId, e.BusinessName, t.Trimmed, 0, 0
FROM   dbo.Entities e
CROSS APPLY (SELECT LTRIM(RTRIM(e.BusinessName)) AS Trimmed) t
WHERE  LEN(t.Trimmed) >= 2
  AND (
        (    CHARINDEX(LEFT(t.Trimmed, 1)  COLLATE Latin1_General_BIN2, @DoubleQuotes COLLATE Latin1_General_BIN2) > 0
         AND CHARINDEX(RIGHT(t.Trimmed, 1) COLLATE Latin1_General_BIN2, @DoubleQuotes COLLATE Latin1_General_BIN2) > 0)
     OR (    CHARINDEX(LEFT(t.Trimmed, 1)  COLLATE Latin1_General_BIN2, @SingleQuotes COLLATE Latin1_General_BIN2) > 0
         AND CHARINDEX(RIGHT(t.Trimmed, 1) COLLATE Latin1_General_BIN2, @SingleQuotes COLLATE Latin1_General_BIN2) > 0)
      );

-- Peel one layer per pass while the name is wrapped AND nothing of that family is left inside.
DECLARE @Peeled INT = 1;
WHILE @Peeled > 0
BEGIN
    UPDATE f
    SET    f.ProposedName = LTRIM(RTRIM(x.InnerName)),
           f.Layers       = f.Layers + 1
    FROM   #Fix f
    CROSS APPLY (
        SELECT
            SUBSTRING(f.ProposedName, 2, LEN(f.ProposedName) - 2) AS InnerName,
            CASE
                WHEN CHARINDEX(LEFT(f.ProposedName, 1)  COLLATE Latin1_General_BIN2, @DoubleQuotes COLLATE Latin1_General_BIN2) > 0
                 AND CHARINDEX(RIGHT(f.ProposedName, 1) COLLATE Latin1_General_BIN2, @DoubleQuotes COLLATE Latin1_General_BIN2) > 0
                    THEN @DoublePattern
                WHEN CHARINDEX(LEFT(f.ProposedName, 1)  COLLATE Latin1_General_BIN2, @SingleQuotes COLLATE Latin1_General_BIN2) > 0
                 AND CHARINDEX(RIGHT(f.ProposedName, 1) COLLATE Latin1_General_BIN2, @SingleQuotes COLLATE Latin1_General_BIN2) > 0
                    THEN @SinglePattern
            END AS FamilyPattern
        WHERE LEN(f.ProposedName) >= 2
    ) x
    WHERE  x.FamilyPattern IS NOT NULL
      AND  x.InnerName COLLATE Latin1_General_BIN2 NOT LIKE x.FamilyPattern COLLATE Latin1_General_BIN2;

    SET @Peeled = @@ROWCOUNT;
END;

-- Still wrapped after peeling → the inner text holds a same-family quote: manual review.
UPDATE f
SET    f.NeedsReview = 1
FROM   #Fix f
WHERE  LEN(f.ProposedName) >= 2
  AND (
        (    CHARINDEX(LEFT(f.ProposedName, 1)  COLLATE Latin1_General_BIN2, @DoubleQuotes COLLATE Latin1_General_BIN2) > 0
         AND CHARINDEX(RIGHT(f.ProposedName, 1) COLLATE Latin1_General_BIN2, @DoubleQuotes COLLATE Latin1_General_BIN2) > 0)
     OR (    CHARINDEX(LEFT(f.ProposedName, 1)  COLLATE Latin1_General_BIN2, @SingleQuotes COLLATE Latin1_General_BIN2) > 0
         AND CHARINDEX(RIGHT(f.ProposedName, 1) COLLATE Latin1_General_BIN2, @SingleQuotes COLLATE Latin1_General_BIN2) > 0)
      );

-- ── Result set 1: rows this script fixes ────────────────────────────────────
SELECT f.TenantId, f.EntityId, f.CurrentName AS BusinessName, f.ProposedName, f.Layers
FROM   #Fix f
WHERE  f.Layers > 0 AND f.NeedsReview = 0 AND LEN(f.ProposedName) > 0
ORDER  BY f.TenantId, f.CurrentName;

-- ── Result set 2: wrapped, but NOT fixed — review by hand ───────────────────
SELECT f.TenantId, f.EntityId, f.CurrentName AS BusinessName,
       CASE WHEN LEN(f.ProposedName) = 0 THEN N'(would be empty)' ELSE N'(inner quotes)' END AS Reason
FROM   #Fix f
WHERE  f.NeedsReview = 1 OR LEN(f.ProposedName) = 0
ORDER  BY f.TenantId, f.CurrentName;

IF @Apply = 1
BEGIN
    BEGIN TRANSACTION;

    UPDATE e
    SET    e.BusinessName = f.ProposedName
    FROM   dbo.Entities e
    JOIN   #Fix f
      ON   f.EntityId     = e.EntityId
     AND   f.TenantId     = e.TenantId
     AND   f.CurrentName  = e.BusinessName COLLATE Latin1_General_BIN2   -- unchanged since previewed
    WHERE  f.Layers > 0
      AND  f.NeedsReview = 0
      AND  LEN(f.ProposedName) > 0;

    DECLARE @Updated INT = @@ROWCOUNT;

    COMMIT TRANSACTION;

    PRINT CONCAT(N'084: ', @Updated, N' Entities.BusinessName value(s) fixed.');
END
ELSE
BEGIN
    PRINT N'084: preview only (@Apply = 0) — nothing was written. Review the two result sets, then set @Apply = 1 and run again.';
END;

DROP TABLE #Fix;
