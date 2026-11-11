-- ============================================================================
-- Script  : diagnostics/Documents_LufeFiscalYear_Audit_20260803.sql
-- Purpose : READ-ONLY audit. Lists the Lufe-imported documents whose stored
--           [dbo].[Documents].[FiscalYear] disagrees with the fiscal year parsed
--           from the document's OWN periodo inside its entity's LufeDataJson.
--           Output is per-document (entity name + document name) so a human can
--           act on each row — it identifies WHICH documents are wrong, not just
--           how many.
-- Date    : 2026-08-03
-- Author  : Claude (assisted)
-- Type    : DIAGNOSTIC — not a migration. Deliberately NOT numbered into the
--           NNN_ DDL sequence: it is re-run on demand (after deploy to size the
--           problem, again to verify any correction, and later if anyone questions
--           an old analysis's ejercicio), never executed once in order by the DBA.
--
-- READ-ONLY: this script performs SELECTs only. It contains NO INSERT/UPDATE/
--            DELETE and NO DDL. It is safe to run on production and changes nothing.
--            Any correction of the mislabelled rows is a SEPARATE, deliberate action
--            (see the tracking issue linked from PR #89), not part of this file.
--
-- Background (the bug this measures):
--   Before PR #89, the Lufe single-document import derived a document's FiscalYear
--   from a two-level cascade that preferred the ENTITY-WIDE indicadores.periodo
--   (the entity's most-recent statements) over the document's own period. That
--   stamped every imported document with the latest year, mislabelling any
--   historical balance. The int.TryParse fallback also rejected the YYYYMM / YYYY00
--   / date-like periodo shapes, leaving FiscalYear NULL for those.
--   PR #89 (merged to DEVELOP) fixed the import to parse the document's own
--   periodo (LufePeriodo.ParseYear). But the fix is forward-only: rows imported
--   BEFORE the deploy do NOT self-correct — this script finds them.
--
-- Scope   : Run against each per-tenant business database (connection string
--           "FinanciaDefault"). Tables: [dbo].[Documents], [dbo].[Entities].
--           LufeDataJson root props are PascalCase ($.Documentos, $.Indicadores)
--           because they are written with JsonSerializer.Serialize(lufeData) and no
--           naming policy; nombre/periodo are lowercase (locked by [JsonPropertyName]).
--
-- Caveats (stated, not hidden):
--   1. The JSON documento is matched to the imported row by the SAME name transform
--      the import used: OriginalFileName = GetFileNameWithoutExtension(nombre)+'.pdf'.
--      Assumes nombre has no directory separators and names are unique within an
--      entity; duplicate names fan out — eyeball DocPeriodoRaw for those.
--   2. ExpectedFiscalYear approximates LufePeriodo.ParseYear by taking the first
--      4-digit run (PATINDEX), range-checked to [1990, UtcNow.Year+1]. Faithful for
--      every realistic periodo shape ("2024","202400","202412","2024-12-31",
--      "20241231"). A contrived multi-run string whose first run is out of range
--      would differ — but such a row still SURFACES below (Expected NULL vs a
--      non-null stored value), so nothing is silently passed.
--   3. Only documents still present in the entity's CURRENT LufeDataJson snapshot
--      are checked (INNER JOIN). A document whose Lufe data was later refreshed and
--      no longer lists it cannot be verified from the JSON and will not appear.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @MaxYear int = YEAR(SYSUTCDATETIME()) + 1;   -- mirrors DeriveFiscalYear's DateTime.UtcNow.Year + 1
DECLARE @MinYear int = 1990;

;WITH LufeDocs AS (
    -- Every documento inside every entity's LufeDataJson snapshot.
    SELECT
        e.EntityId,
        e.TenantId,
        e.BusinessName,
        jd.[key]                            AS DocIndex,        -- 0-based ordinal in the JSON array
        JSON_VALUE(jd.value, '$.nombre')    AS Nombre,
        JSON_VALUE(jd.value, '$.periodo')   AS Periodo,
        -- indicadores.periodo (decimal, e.g. 202412) -> the year the OLD code stamped: floor(/100)
        CAST(TRY_CONVERT(decimal(18,4),
             JSON_VALUE(e.LufeDataJson, '$.Indicadores.periodo')) / 100.0 AS int) AS IndicadoresYear
    FROM dbo.Entities e
    CROSS APPLY OPENJSON(e.LufeDataJson, '$.Documentos') jd
    WHERE e.LufeDataJson IS NOT NULL
),
Parsed AS (
    -- ExpectedFiscalYear = year parsed from the document's OWN periodo (the correct value).
    SELECT ld.*,
           CASE
               WHEN ld.Periodo IS NOT NULL
                    AND PATINDEX('%[0-9][0-9][0-9][0-9]%', ld.Periodo) > 0
                    AND TRY_CONVERT(int, SUBSTRING(ld.Periodo,
                            PATINDEX('%[0-9][0-9][0-9][0-9]%', ld.Periodo), 4))
                        BETWEEN @MinYear AND @MaxYear
               THEN TRY_CONVERT(int, SUBSTRING(ld.Periodo,
                            PATINDEX('%[0-9][0-9][0-9][0-9]%', ld.Periodo), 4))
           END AS ExpectedFiscalYear
    FROM LufeDocs ld
)
SELECT
    d.TenantId,
    p.BusinessName            AS EntityName,          -- so a human can act on the row
    d.EntityId,
    d.DocumentId,
    d.OriginalFileName        AS DocumentName,
    d.DocumentType,
    d.FiscalYear              AS StoredFiscalYear,     -- the value under audit
    p.ExpectedFiscalYear,                             -- from the document's OWN periodo (correct)
    p.IndicadoresYear,                                -- entity-wide latest; the OLD wrong source (context)
    p.Periodo                 AS DocPeriodoRaw,
    d.UploadedAt
FROM dbo.Documents d
JOIN Parsed p
      ON  p.EntityId = d.EntityId
      AND p.TenantId = d.TenantId
      AND d.OriginalFileName =
          CASE
              WHEN p.Nombre IS NULL OR LTRIM(RTRIM(p.Nombre)) = '' THEN 'documento.pdf'
              WHEN CHARINDEX('.', REVERSE(p.Nombre)) > 0
                  THEN LEFT(p.Nombre, LEN(p.Nombre) - CHARINDEX('.', REVERSE(p.Nombre))) + '.pdf'
              ELSE p.Nombre + '.pdf'
          END
WHERE d.Source = 'Lufe'
  AND d.IsDeleted = 0
  AND (   -- stored value disagrees with the document's own parsed period
          (d.FiscalYear IS NULL     AND p.ExpectedFiscalYear IS NOT NULL)   -- old NULLs on YYYYMM shapes
       OR (d.FiscalYear IS NOT NULL AND p.ExpectedFiscalYear IS NULL)       -- unparseable / needs review
       OR (d.FiscalYear <> p.ExpectedFiscalYear)                            -- indicadores-stamped, etc.
      )
ORDER BY d.TenantId, p.BusinessName, d.OriginalFileName;
