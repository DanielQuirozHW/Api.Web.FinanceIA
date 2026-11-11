// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Exposes available export template definitions.
/// Route: GET /api/bff/export/templates
/// </summary>
[ApiController]
[Route("api/bff/export")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class ExportTemplatesController : ControllerBase
{
    /// <summary>
    /// Returns all predefined export templates with their section sets.
    /// </summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(IEnumerable<ExportTemplateDto>), 200)]
    public IActionResult GetTemplates()
    {
        var templates = ExportController.Templates.Select(kvp => new ExportTemplateDto
        {
            Key          = kvp.Key,
            Name         = GetDisplayName(kvp.Key),
            Sections     = kvp.Value,
            AnalysisKind = ExportController.TemplateKind(kvp.Key)
        });

        return Ok(templates);
    }

    private static string GetDisplayName(string key) => key switch
    {
        "ejecutivo" => "Ejecutivo",
        "completo"  => "Completo",
        "tecnico"   => "Técnico",
        "persona"   => "Persona humana",
        _           => key
    };
}

/// <summary>
/// DTO representing an export template definition.
/// </summary>
public sealed class ExportTemplateDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The analysis kind the template applies to: <c>Balance</c> or <c>PersonaHumana</c>. The export of an
    /// analysis accepts only its own kind's templates, so a client lists the ones matching the analysis.
    /// </summary>
    public string AnalysisKind { get; init; } = AnalysisKinds.Balance;
}
