// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using System.Collections.Concurrent;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// The readers that branch on AnalysisKind (DDL 072), on the REAL repositories over EF InMemory: the
/// entity's analysis list reaches a document-less persona analysis without changing what it shows for
/// balance analyses, and the PDF export serves a persona analysis its own report — and only its own sections.
/// </summary>
public sealed class PersonAnalysisReadersTests
{
    private const string Tenant = "tenant-readers";

    private readonly FinanciaDbContext _db = new(new DbContextOptionsBuilder<FinanciaDbContext>()
        .UseInMemoryDatabase($"person-readers-{Guid.NewGuid()}").Options);

    // ── GET /api/analyses?entityId= ────────────────────────────────────────────────

    [Fact]
    public async Task EntityList_IncludesThePersonaAnalysis_AndStillHidesAnalysesOfDeletedBalances()
    {
        var entityId = Guid.NewGuid();
        var live     = AddBalance(entityId, deleted: false);
        var gone     = AddBalance(entityId, deleted: true);
        var persona  = AddPersona(entityId);
        var other    = AddPersona(Guid.NewGuid());   // another entity
        Save();

        var repo = new AnalysisResultRepository(_db, NullLogger<AnalysisResultRepository>.Instance);
        var (items, total) = await repo.GetListPageByEntityAsync(entityId, Tenant, 0, 20, CancellationToken.None);

        Assert.Equal(2, total);
        Assert.Equal(
            new[] { live, persona }.OrderBy(x => x),
            items.Select(i => i.AnalysisId).OrderBy(x => x));
        Assert.DoesNotContain(gone, items.Select(i => i.AnalysisId));
        Assert.DoesNotContain(other, items.Select(i => i.AnalysisId));
    }

    [Fact]
    public async Task EntityList_IsTenantScoped_ForPersonaAnalysesToo()
    {
        var entityId = Guid.NewGuid();
        AddPersona(entityId);
        Save();

        var repo = new AnalysisResultRepository(_db, NullLogger<AnalysisResultRepository>.Instance);
        var (_, total) = await repo.GetListPageByEntityAsync(entityId, "other-tenant", 0, 20, CancellationToken.None);

        Assert.Equal(0, total);
    }

    // ── PDF export ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PdfExport_OfACompany_WhenTheComparisonAndTheSettingsThrow_StillReturnsThePdf()
    {
        // The first page's previous-analysis comparison and the document's Author are fault-isolated: a reader
        // that throws leaves its item out, never a failed export.
        var entityId   = AddCompanyEntity();
        var analysisId = AddBalance(entityId, deleted: false);
        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var sut = BuildExportController(
            Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService(),
            new CapturingExportLogRepository(),
            new Api.Web.FinanceIA.Tests.Controllers.ExportTenantSettingsStub(throws: true),
            new Api.Web.FinanceIA.Tests.Controllers.ExportComparisonStub(throws: true));

        var result = await sut.ExportPdfAsync(resultId, "", "", cancellationToken: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("%PDF"u8.ToArray(), file.FileContents.AsSpan(0, 4).ToArray());

        string text = Api.Web.FinanceIA.Tests.Servicios.PersonPdfFixtures.Text(file.FileContents);
        Assert.Contains("Metalúrgica \"Ejemplo\" S.A.", text);                // razón social from Entity
        Assert.Contains("30-71234567-8", text);
        Assert.DoesNotContain("Comparación", text);

        // Quotes dropped, accents stripped, hyphens between words; the UTF-8 name keeps the accent.
        string disposition = sut.HttpContext.Response.Headers.ContentDisposition.ToString();
        Assert.Contains("filename=Informe-crediticio_Metalurgica-Ejemplo-S-A_Ejercicio-0_", disposition);
        Assert.Contains("filename*=UTF-8''Informe%20crediticio_Metal%C3%BArgica%20Ejemplo%20S.A._Ejercicio%200_", disposition);
    }

    [Fact]
    public async Task PdfExport_OfAPersonaAnalysis_ReturnsThePersonaPdf_AndLogsThePersonaTemplate()
    {
        var entityId   = AddPersonEntity();
        var analysisId = AddPersona(entityId);
        AddSolicitante(analysisId);
        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;
        var exportLogs = new CapturingExportLogRepository();

        var sut = BuildExportController(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService(), exportLogs);

        var result = await sut.ExportPdfAsync(resultId, "", "", cancellationToken: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        // The download name travels in Content-Disposition: ASCII filename + RFC 5987 filename*.
        string disposition = sut.HttpContext.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("attachment", disposition);
        Assert.Contains("filename=Evaluacion-crediticia_Juana-Persona_", disposition);
        Assert.Contains("filename*=UTF-8''Evaluaci%C3%B3n%20crediticia_Juana%20Persona_", disposition);
        Assert.Equal("%PDF"u8.ToArray(), file.FileContents.AsSpan(0, 4).ToArray());

        string text = Api.Web.FinanceIA.Tests.Servicios.PersonPdfFixtures.Text(file.FileContents);
        Assert.Contains("Informe de análisis · Persona humana", text);
        Assert.Contains("Juana Persona", text);                    // the identity comes from Entity
        Assert.Contains("27-11222333-4", text);
        Assert.Contains("Pendiente de decisión", text);

        var log = Assert.Single(exportLogs.Logs);
        Assert.Equal(ExportController.PersonaTemplate, log.Template);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(ExportController.Templates["persona"]), log.SectionsIncluded);
    }

    /// <summary>
    /// M10 Phase 5a-2: the report reads the fiadores' aggregate coverage through the same reader as the detail
    /// and the decision, and prints it in persona_decision.
    /// </summary>
    [Fact]
    public async Task PdfExport_OfAPersonaAnalysisWithFiadores_ReadsAndPrintsTheSameCoverage()
    {
        var entityId   = AddPersonEntity();
        var analysisId = AddPersona(entityId);
        AddSolicitante(analysisId);
        AddUser();
        var fiadorRow = PersonFiadorFixtures.FiadorRow(analysisId, Tenant, 1_000_000m, 1_200_000m);
        _db.AnalysisParticipants.Add(fiadorRow);
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;
        var capturing = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());

        var result = await BuildExportController(capturing, new CapturingExportLogRepository())
            .ExportPdfAsync(resultId, "", "", cancellationToken: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var repository = new PersonAnalysisRepository(_db, NullLogger<PersonAnalysisRepository>.Instance);
        var (rules, solicitante, fiadores) = await Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson
            .ResolveSolicitanteAsync(repository, analysisId, Tenant, CancellationToken.None);
        var detail = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.ToDetailResponse(rules, solicitante, fiadores)!;

        var printed = capturing.Report!.Evaluation!;
        var options = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Options;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(detail.CounterGuaranteeCoverage, options),
                     System.Text.Json.JsonSerializer.Serialize(printed.CounterGuaranteeCoverage, options));
        Assert.Same(printed.CounterGuaranteeCoverage, printed.DecisionRules!.CounterGuaranteeCoverage);
        Assert.Single(printed.Fiadores!);

        string text = Api.Web.FinanceIA.Tests.Servicios.PersonPdfFixtures.Text(file.FileContents);
        Assert.Contains("Cobertura de contragarantías", text);
        Assert.Contains("60 % del monto solicitado (mínimo 100 %, no cumple)", text);
        Assert.Contains("Motivo obligatorio", text);
    }

    /// <summary>
    /// The fiador section prints the CURRENT rows, as the detail reads them: a re-evaluation overwrites the
    /// fiador row in place and the next export shows the new figures, never the old ones. The CUIT comes from
    /// the Fiadores row, the same one the detail now returns.
    /// </summary>
    [Fact]
    public async Task PdfExport_OfAReevaluatedAnalysis_PrintsTheNewFiadorEvaluation_AndTheFiadorsCuit()
    {
        var entityId   = AddPersonEntity();
        var analysisId = AddPersona(entityId);
        AddSolicitante(analysisId);
        AddUser();
        var fiadorRow = PersonFiadorFixtures.FiadorRow(analysisId, Tenant, 1_000_000m, 1_200_000m);
        _db.AnalysisParticipants.Add(fiadorRow);
        _db.Fiadores.Add(new Fiador
        {
            FiadorId     = fiadorRow.FiadorId!.Value,
            TenantId     = Tenant,
            EntityId     = entityId,
            CUIT         = "20999888777",
            Denominacion = "Roberto Garante",
            Origen       = "Manual",
            Status       = "Active",
            CreatedAt    = DateTime.UtcNow
        });
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var before = Assert.IsType<FileContentResult>(await BuildExportController(
                Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService(), new CapturingExportLogRepository())
            .ExportPdfAsync(resultId, "persona_fiadores", "", cancellationToken: CancellationToken.None));
        string first = Api.Web.FinanceIA.Tests.Servicios.PersonPdfFixtures.Text(before.FileContents);
        Assert.Contains("Roberto Garante", first);
        Assert.Contains("20-99988877-7", first);
        Assert.Contains("$ 1.200.000", first);                     // the inmueble as first evaluated

        // "Reanalizar": the same row, overwritten in place with a new evaluation.
        var reevaluated = PersonFiadorFixtures.FiadorRow(analysisId, Tenant, 1_000_000m, 3_400_000m);
        var row = _db.AnalysisParticipants.Single(p => p.ParticipantId == fiadorRow.ParticipantId);
        row.FiltersJson    = reevaluated.FiltersJson;
        row.IndicatorsJson = reevaluated.IndicatorsJson;
        row.CoverageJson   = reevaluated.CoverageJson;
        var result = _db.AnalysisResults.Single(r => r.ResultId == resultId);
        result.RescoreCount = 1;
        result.RescoredAt   = DateTime.UtcNow;
        Save();

        var capturing = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());
        var after = Assert.IsType<FileContentResult>(await BuildExportController(capturing, new CapturingExportLogRepository())
            .ExportPdfAsync(resultId, "persona_fiadores", "", cancellationToken: CancellationToken.None));
        string second = Api.Web.FinanceIA.Tests.Servicios.PersonPdfFixtures.Text(after.FileContents);
        Assert.Contains("$ 3.400.000", second);
        Assert.DoesNotContain("$ 1.200.000", second);

        // Exactly the detail's fiador, CUIT included.
        var repository = new PersonAnalysisRepository(_db, NullLogger<PersonAnalysisRepository>.Instance);
        var (rules, solicitante, fiadores) = await Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson
            .ResolveSolicitanteAsync(repository, analysisId, Tenant, CancellationToken.None);
        var detail  = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.ToDetailResponse(rules, solicitante, fiadores)!;
        var options = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Options;
        Assert.Equal("20999888777", Assert.Single(detail.Fiadores!).Cuit);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(detail.Fiadores, options),
                     System.Text.Json.JsonSerializer.Serialize(capturing.Report!.Evaluation!.Fiadores, options));
    }

    [Theory]
    [InlineData("balance", "")]
    [InlineData("persona_resumen,risk", "")]
    [InlineData("", "ejecutivo")]
    public async Task PdfExport_OfAPersonaAnalysis_WithBalanceSections_Is400(string sections, string template)
    {
        var analysisId = AddPersona(Guid.NewGuid());
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var result = await BuildExportController(null!, null!).ExportPdfAsync(resultId, sections, template, cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("section_not_valid_for_analysis_kind", problem.Extensions["errorCode"]);
    }

    [Theory]
    [InlineData("persona_resumen", "")]
    [InlineData("persona_fiadores", "")]
    [InlineData("balance,persona_decision", "")]
    [InlineData("", "persona")]
    public async Task PdfExport_OfABalanceAnalysis_WithPersonaSections_Is400(string sections, string template)
    {
        var analysisId = AddBalance(Guid.NewGuid(), deleted: false);
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var result = await BuildExportController(null!, null!).ExportPdfAsync(resultId, sections, template, cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("section_not_valid_for_analysis_kind", problem.Extensions["errorCode"]);
    }

    [Fact]
    public void ExportTemplates_ListThePersonaTemplate_WithItsAnalysisKind()
    {
        var ok = Assert.IsType<OkObjectResult>(new ExportTemplatesController().GetTemplates());
        var templates = Assert.IsAssignableFrom<IEnumerable<ExportTemplateDto>>(ok.Value).ToList();

        var persona = Assert.Single(templates, t => t.Key == "persona");
        Assert.Equal("Persona humana", persona.Name);
        Assert.Equal(AnalysisKinds.PersonaHumana, persona.AnalysisKind);
        Assert.Equal(Api.Web.FinanceIA.Servicios.PdfExportService.PersonaSections, persona.Sections);
        Assert.All(templates.Where(t => t.Key != "persona"), t => Assert.Equal(AnalysisKinds.Balance, t.AnalysisKind));
    }

    [Theory]
    [InlineData(false, "DARK", Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Dark)]
    [InlineData(true, " dark ", Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Dark)]
    [InlineData(false, "", Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData(true, "Light", Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    public async Task PdfExport_PassesTheTheme_ToEitherReport_AndKeepsTheFileName(
        bool persona, string theme, Api.Web.FinanceIA.Servicios.Pdf.PdfTheme expected)
    {
        Guid analysisId;
        if (persona)
        {
            analysisId = AddPersona(AddPersonEntity());
            AddSolicitante(analysisId);
        }
        else
        {
            analysisId = AddBalance(AddCompanyEntity(), deleted: false);
        }

        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;
        var exportLogs = new CapturingExportLogRepository();
        var pdf = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());

        var sut = BuildExportController(pdf, exportLogs);
        var result = await sut.ExportPdfAsync(resultId, "", "", theme, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(expected, pdf.Theme);

        // The theme never reaches the file name, and ExportLogs has no column for it.
        string disposition = sut.HttpContext.Response.Headers.ContentDisposition.ToString();
        Assert.DoesNotContain("dark", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(persona ? "filename=Evaluacion-crediticia_" : "filename=Informe-crediticio_", disposition);
        Assert.Single(exportLogs.Logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PdfExport_ResolvesBrandingOnce_AndHandsItToEitherReport(bool persona)
    {
        Guid analysisId = persona ? AddPersona(AddPersonEntity()) : AddBalance(AddCompanyEntity(), deleted: false);
        if (persona)
            AddSolicitante(analysisId);

        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var branding = Api.Web.FinanceIA.Tests.Servicios.PdfBranding.BrandingFixtures.CoBranded("SGR Ejemplo");
        var provider = new Api.Web.FinanceIA.Tests.Controllers.ExportBrandingStub(branding);
        var logger   = new Api.Web.FinanceIA.Tests.Servicios.PdfBranding.RecordingLogger<ExportController>();
        var pdf      = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());

        var result = await BuildExportController(pdf, new CapturingExportLogRepository(), branding: provider, logger: logger)
            .ExportPdfAsync(resultId, "", "", "dark", CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Dark, provider.Theme);
        Assert.Same(branding, persona ? pdf.Report!.Branding : pdf.CompanyReport!.Branding);
        Assert.Contains(logger.Entries, e => e.Message.Contains("brand=client", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PdfExport_BrandingThatThrows_StillExports_WithTheFinanciaHeader(bool persona)
    {
        Guid analysisId = persona ? AddPersona(AddPersonEntity()) : AddBalance(AddCompanyEntity(), deleted: false);
        if (persona)
            AddSolicitante(analysisId);

        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var logger = new Api.Web.FinanceIA.Tests.Servicios.PdfBranding.RecordingLogger<ExportController>();
        var pdf    = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());

        var result = await BuildExportController(
                pdf, new CapturingExportLogRepository(),
                branding: new Api.Web.FinanceIA.Tests.Controllers.ExportBrandingStub(throws: true), logger: logger)
            .ExportPdfAsync(resultId, "", "", cancellationToken: CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Same(Api.Web.FinanceIA.Servicios.Pdf.TenantPdfBranding.None,
            persona ? pdf.Report!.Branding : pdf.CompanyReport!.Branding);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                             && e.Message.Contains("branding unavailable", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("brand=financia", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompanyPdfExport_ReadsTheAlertCatalogueOnce_AndHandsThisTenantsNamesToTheReport()
    {
        Guid analysisId = AddBalance(AddCompanyEntity(), deleted: false);
        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var catalogue = new Api.Web.FinanceIA.Tests.Controllers.ExportAlertCatalogueStub(
        [
            new AlertRuleDefinition { TenantId = Tenant, Code = "ENDEUDAMIENTO_ELEVADO", DisplayNameEs = "Endeudamiento alto (nombre del cliente)", Enabled = false },
            new AlertRuleDefinition { TenantId = Tenant, Code = "SIN_NOMBRE", DisplayNameEs = "  " },
            new AlertRuleDefinition { TenantId = "otro-tenant", Code = "FLUJO_NO_CONCILIA", DisplayNameEs = "Nombre de otro tenant" },
        ]);
        var pdf = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());

        var result = await BuildExportController(pdf, new CapturingExportLogRepository(), alertCatalogue: catalogue)
            .ExportPdfAsync(resultId, "", "", cancellationToken: CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(1, catalogue.Calls);
        IReadOnlyDictionary<string, string> names = pdf.CompanyReport!.AlertDisplayNames;
        Assert.Equal("Endeudamiento alto (nombre del cliente)", names["endeudamiento_elevado"]);   // a disabled rule still names its code
        Assert.False(names.ContainsKey("SIN_NOMBRE"));
        Assert.False(names.ContainsKey("FLUJO_NO_CONCILIA"));
    }

    [Fact]
    public async Task CompanyPdfExport_ACatalogueThatThrows_StillExports_WithNoCatalogueNames()
    {
        Guid analysisId = AddBalance(AddCompanyEntity(), deleted: false);
        AddUser();
        Save();
        var resultId = _db.AnalysisResults.Single(r => r.AnalysisId == analysisId).ResultId;

        var logger = new Api.Web.FinanceIA.Tests.Servicios.PdfBranding.RecordingLogger<ExportController>();
        var pdf    = new CapturingPdfService(Api.Web.FinanceIA.Tests.Servicios.PdfExportServiceRenderTests.NewService());

        var result = await BuildExportController(
                pdf, new CapturingExportLogRepository(), logger: logger,
                alertCatalogue: new Api.Web.FinanceIA.Tests.Controllers.ExportAlertCatalogueStub(throws: true))
            .ExportPdfAsync(resultId, "", "", cancellationToken: CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Empty(pdf.CompanyReport!.AlertDisplayNames);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                             && e.Message.Contains("alert_catalogue", StringComparison.Ordinal));
    }

    private const string FirebaseUid = "uid-analyst";

    private ExportController BuildExportController(
        Api.Web.FinanceIA.Servicios.IPdfExportService pdfExportService, IExportLogRepository exportLogRepository,
        ITenantSettingsService? tenantSettings = null,
        Api.Web.FinanceIA.Servicios.AnalysisComparison.IAnalysisComparisonService? comparison = null,
        Api.Web.FinanceIA.Servicios.Pdf.ITenantPdfBrandingProvider? branding = null,
        Microsoft.Extensions.Logging.ILogger<ExportController>? logger = null,
        IAlertRuleCatalogueRepository? alertCatalogue = null)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("uid", FirebaseUid)], "Internal"))
        };

        return new ExportController(
            new Api.Web.FinanceIA.Tests.Legajo.StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId = Tenant, FirebaseProjectId = "proj", FirebaseCredentialJson = "{}", RecaptchaSecretKey = "secret"
                }
            },
            new AnalysisResultRepository(_db, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(_db, NullLogger<AnalysisRepository>.Instance),
            new UserRepository(_db, NullLogger<UserRepository>.Instance),
            pdfExportService,
            exportLogRepository,
            new PersonAnalysisRepository(_db, NullLogger<PersonAnalysisRepository>.Instance),
            new EntityRepository(_db, NullLogger<EntityRepository>.Instance),
            new CreditLineRepository(_db, NullLogger<CreditLineRepository>.Instance),
            tenantSettings ?? new Api.Web.FinanceIA.Tests.Controllers.ExportTenantSettingsStub(),
            comparison ?? new Api.Web.FinanceIA.Tests.Controllers.ExportComparisonStub(),
            branding ?? new Api.Web.FinanceIA.Tests.Controllers.ExportBrandingStub(),
            alertCatalogue ?? new AlertRuleCatalogueRepository(_db, NullLogger<AlertRuleCatalogueRepository>.Instance),
            logger ?? NullLogger<ExportController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    /// <summary>The real renderer, with the report it was handed kept for the test.</summary>
    private sealed class CapturingPdfService(Api.Web.FinanceIA.Servicios.IPdfExportService inner) : Api.Web.FinanceIA.Servicios.IPdfExportService
    {
        public PersonPdfReport? Report { get; private set; }

        public CompanyPdfReport? CompanyReport { get; private set; }

        public Api.Web.FinanceIA.Servicios.Pdf.PdfTheme? Theme { get; private set; }

        public Task<byte[]> GenerateAsync(AnalysisResult result, Analysis analysis, string tenantId, IReadOnlyList<string> sections, CancellationToken ct, CompanyPdfReport? report = null, Api.Web.FinanceIA.Servicios.Pdf.PdfTheme theme = Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)
        {
            Theme         = theme;
            CompanyReport = report;
            return inner.GenerateAsync(result, analysis, tenantId, sections, ct, report, theme);
        }

        public Task<byte[]> GeneratePersonAsync(PersonPdfReport report, IReadOnlyList<string> sections, CancellationToken ct, Api.Web.FinanceIA.Servicios.Pdf.PdfTheme theme = Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)
        {
            Report = report;
            Theme  = theme;
            return inner.GeneratePersonAsync(report, sections, ct, theme);
        }
    }

    private sealed class CapturingExportLogRepository : IExportLogRepository
    {
        public ConcurrentQueue<ExportLog> Logs { get; } = new();

        public Task<ExportLog> CreateAsync(ExportLog log, CancellationToken ct)
        {
            Logs.Enqueue(log);
            return Task.FromResult(log);
        }
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────

    private Guid AddBalance(Guid entityId, bool deleted)
    {
        var document = new Document
        {
            DocumentId       = Guid.NewGuid(),
            TenantId         = Tenant,
            EntityId         = entityId,
            DocumentType     = DocumentTypes.Balance,
            StoragePath      = "p",
            FileHashSHA256   = Guid.NewGuid().ToString("N"),
            OriginalFileName = "balance.pdf",
            IsDeleted        = deleted,
            UploadedAt       = DateTime.UtcNow
        };
        _db.Documents.Add(document);
        return AddAnalysis(entityId, document.DocumentId, AnalysisKinds.Balance);
    }

    private Guid AddPersona(Guid entityId) => AddAnalysis(entityId, null, AnalysisKinds.PersonaHumana);

    private Guid AddCompanyEntity()
    {
        var entity = new Entity
        {
            EntityId     = Guid.NewGuid(),
            TenantId     = Tenant,
            CUIT         = "30712345678",
            PersonType   = PersonTypes.Juridica,
            BusinessName = "Metalúrgica \"Ejemplo\" S.A.",
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow
        };
        _db.Entities.Add(entity);
        return entity.EntityId;
    }

    private Guid AddPersonEntity()
    {
        var entity = new Entity
        {
            EntityId     = Guid.NewGuid(),
            TenantId     = Tenant,
            CUIT         = "27112223334",
            PersonType   = PersonTypes.Humana,
            BusinessName = "Juana Persona",
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow
        };
        _db.Entities.Add(entity);
        return entity.EntityId;
    }

    private void AddSolicitante(Guid analysisId)
    {
        var input  = PersonEvaluationFixtures.Empty();
        var result = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationService.Evaluate(input);
        _db.AnalysisParticipants.Add(new AnalysisParticipant
        {
            ParticipantId        = Guid.NewGuid(),
            TenantId             = Tenant,
            AnalysisId           = analysisId,
            PersonLegajoId       = PersonEvaluationFixtures.LegajoId,
            Role                 = ParticipantRoles.Solicitante,
            AmountAtCharge       = input.RequestedAmount,
            Currency             = input.Currency,
            FiltersJson          = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Serialize(result.Filters),
            IndicatorsJson       = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Serialize(result.Indicators),
            CoverageJson         = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Serialize(result.Coverage),
            EvaluationVersion    = result.Version,
            CreatedAt            = input.AsOfUtc
        });
    }

    private void AddUser() => _db.Users.Add(new User
    {
        UserId      = Guid.NewGuid(),
        TenantId    = Tenant,
        FirebaseUid = FirebaseUid,
        Email       = "analyst@example.test",
        DisplayName = "Analista",
        Role        = "Analyst",
        IsActive    = true
    });

    private Guid AddAnalysis(Guid entityId, Guid? documentId, string kind)
    {
        var analysis = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = Tenant,
            EntityId      = entityId,
            DocumentId    = documentId,
            AnalysisKind  = kind,
            Status        = AnalysisStatuses.PendingReview,
            AiModel       = "none",
            PromptVersion = "pf-0.1",
            CreatedAt     = DateTime.UtcNow
        };
        _db.Analyses.Add(analysis);
        _db.AnalysisResults.Add(new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = analysis.AnalysisId,
            TenantId         = Tenant,
            AiInputHash      = "in",
            AiOutputHash     = "out",
            ValidationStatus = "Passed",
            EnrichmentStatus = "Complete",
            GeneratedAt      = DateTime.UtcNow
        });
        return analysis.AnalysisId;
    }

    private void Save()
    {
        _db.ChangeTracker.DetectChanges();
        _db.SaveChanges();
    }
}
