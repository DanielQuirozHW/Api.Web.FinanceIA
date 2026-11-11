// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Guards the PDF export's section-key contract, which lives in THREE places that can drift apart:
/// <c>ExportController.ValidSectionKeys</c> (what the API accepts), <c>PdfExportService.AllSections</c>
/// (what the service can actually draw) and <c>ExportController.Templates</c> (the named presets the
/// Portal offers).
/// <para>
/// WHY THIS MATTERS: the two failure modes are silent in opposite directions. A key in
/// <c>AllSections</c> but not in the whitelist 400s a request the service could have served. A key in
/// the whitelist but not in <c>AllSections</c> renders nothing at all and surfaces only as a runtime
/// Warning in the service's logs — the very defect its own log message names. Until now nothing failed
/// a build over either; this fixture does.
/// </para>
/// <para>
/// Both constants are <c>internal</c> for exactly this reason (InternalsVisibleTo:
/// Api.Web.FinanceIA.Tests).
/// </para>
/// </summary>
public sealed class ExportSectionKeyContractTests
{
    // ── The CASFOG section must be reachable end to end ───────────────────────

    [Fact]
    public void CasfogKey_IsAcceptedByTheControllerAndRenderedByTheService()
    {
        Assert.Contains("casfog", ExportController.ValidSectionKeys);
        Assert.Contains("casfog", PdfExportService.AllSections);
    }

    // ── …and only from the full-audit template ────────────────────────────────

    [Fact]
    public void CasfogKey_IsInTheTecnicoTemplateOnly()
    {
        Assert.Contains("casfog", ExportController.Templates["tecnico"]);
        Assert.DoesNotContain("casfog", ExportController.Templates["ejecutivo"]);
        Assert.DoesNotContain("casfog", ExportController.Templates["completo"]);
    }

    // ── The LUFE declared-sales section, likewise ─────────────────────────────

    [Fact]
    public void LufeKey_IsAcceptedByTheControllerAndRenderedByTheService()
    {
        Assert.Contains("lufe", ExportController.ValidSectionKeys);
        Assert.Contains("lufe", PdfExportService.AllSections);
    }

    [Fact]
    public void LufeKey_IsInTheTecnicoTemplateOnly_RightAfterBcra()
    {
        IReadOnlyList<string> tecnico = ExportController.Templates["tecnico"];

        Assert.Equal(tecnico.ToList().IndexOf("bcra") + 1, tecnico.ToList().IndexOf("lufe"));
        Assert.DoesNotContain("lufe", ExportController.Templates["ejecutivo"]);
        Assert.DoesNotContain("lufe", ExportController.Templates["completo"]);
    }

    // ── The persona humana report ─────────────────────────────────────────────

    [Fact]
    public void PersonaTemplate_IsExactlyThePersonaSections_InOrder()
    {
        Assert.Equal(PdfExportService.PersonaSections, ExportController.Templates["persona"]);
        Assert.Equal(
            new[]
            {
                "persona_resumen", "persona_elegibilidad", "persona_filtros", "persona_capacidad", "persona_solvencia",
                "persona_fiadores", "persona_coherencia", "persona_cobertura", "persona_fuentes", "persona_decision"
            },
            PdfExportService.PersonaSections);
    }

    [Fact]
    public void FiadoresKey_IsListedByTheTemplatesEndpoint_UnderThePersonaTemplate_RightAfterSolvencia()
    {
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(new ExportTemplatesController().GetTemplates());
        var persona = Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<ExportTemplateDto>>(result.Value), t => t.Key == ExportController.PersonaTemplate);

        Assert.Equal(Api.Web.FinanceIA.Models.Financia.AnalysisKinds.PersonaHumana, persona.AnalysisKind);
        Assert.Equal(persona.Sections.ToList().IndexOf("persona_solvencia") + 1, persona.Sections.ToList().IndexOf("persona_fiadores"));
        Assert.True(ExportController.IsKeyForKind("persona_fiadores", Api.Web.FinanceIA.Models.Financia.AnalysisKinds.PersonaHumana));
        Assert.False(ExportController.IsKeyForKind("persona_fiadores", Api.Web.FinanceIA.Models.Financia.AnalysisKinds.Balance));
    }

    [Fact]
    public void PersonaAndBalanceSections_AreDisjoint_AndTogetherAreAllSections()
    {
        Assert.Empty(PdfExportService.PersonaSections.Intersect(PdfExportService.BalanceSections));
        Assert.All(PdfExportService.PersonaSections, k => Assert.StartsWith("persona_", k));
        Assert.All(PdfExportService.BalanceSections, k => Assert.False(k.StartsWith("persona_", StringComparison.Ordinal), k));
        Assert.Equal(
            PdfExportService.BalanceSections.Concat(PdfExportService.PersonaSections).OrderBy(k => k),
            PdfExportService.AllSections.OrderBy(k => k));
    }

    [Fact]
    public void EveryTemplate_StaysWithinOneAnalysisKind()
    {
        foreach ((string template, IReadOnlyList<string> keys) in ExportController.Templates)
        {
            string kind = ExportController.TemplateKind(template);
            Assert.All(keys, k => Assert.True(ExportController.IsKeyForKind(k, kind), $"{template}: {k}"));
        }
    }

    // ── General drift guards ──────────────────────────────────────────────────

    [Fact]
    public void EveryRenderableSection_IsAcceptedByTheControllerWhitelist()
    {
        foreach (string key in PdfExportService.AllSections)
        {
            Assert.True(
                ExportController.ValidSectionKeys.Contains(key),
                $"PdfExportService renders '{key}' but ExportController.ValidSectionKeys rejects it, " +
                "so a request asking for it 400s before ever reaching the service.");
        }
    }

    [Fact]
    public void EveryWhitelistedSection_IsRenderable()
    {
        // With EveryRenderableSection_IsAcceptedByTheControllerWhitelist above, this pins the two
        // lists as equal: there is no accepted-but-unrendered key, deprecated or otherwise.
        foreach (string key in ExportController.ValidSectionKeys)
        {
            Assert.True(
                PdfExportService.AllSections.Contains(key),
                $"ExportController accepts '{key}' but PdfExportService has no renderer for it — the " +
                "whitelist and the service section list have drifted, which at runtime shows up only " +
                "as a Warning.");
        }
    }

    [Fact]
    public void EveryTemplateKey_IsWhitelistedAndRenderable()
    {
        foreach ((string template, IReadOnlyList<string> keys) in ExportController.Templates)
        {
            foreach (string key in keys)
            {
                Assert.True(
                    ExportController.ValidSectionKeys.Contains(key),
                    $"Template '{template}' names section '{key}', which the controller whitelist rejects.");

                Assert.True(
                    PdfExportService.AllSections.Contains(key),
                    $"Template '{template}' names section '{key}', which the service cannot render.");
            }
        }
    }
}
