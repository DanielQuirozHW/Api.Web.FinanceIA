// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Font;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging;
using ITextDocument = iText.Layout.Document;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// "Hallazgos del balance" in the risk section, in the Portal's order and words (<c>AlertsPanelSection</c> in
/// <c>AnalysisDetailView.tsx</c>): the deterministic catalogue findings first, the model's own observations after them
/// and visibly subordinate. The two channels are never merged or de-duplicated against each other — a finding may
/// legitimately appear in both, as on screen.
/// </summary>
public sealed partial class PdfExportService
{
    private const string FindingsHeading = "Hallazgos del balance";

    private const string CatalogueHeading = "Hallazgos de catálogo";
    private const string CatalogueNote    = "Reglas deterministas: el mismo balance produce siempre los mismos hallazgos.";

    // The Portal's ChannelNotice for an empty catalogue. The wire cannot tell "no rule fired" from "the analysis
    // predates the catalogue" (nothing is stored either way), so the copy claims neither.
    private const string CatalogueEmptyTitle  = "Sin hallazgos de catálogo";
    private const string CatalogueEmptyDetail = "Ninguna regla del catálogo se activó en este análisis, o el análisis es anterior al catálogo.";

    private const string ModelHeading = "Observaciones del modelo";
    private const string ModelNote    = "Lectura del modelo, no garantizada: un nuevo análisis del mismo balance puede redactarlas o codificarlas distinto.";

    /// <summary>
    /// Reads the deterministic channel once per export. An unreadable payload is logged here, once, with ids and
    /// counts only (never the payload: it holds the company's figures); the export goes on without that block.
    /// </summary>
    private CatalogueAlertChannel.Result ReadCatalogueAlerts(AnalysisResult result)
    {
        CatalogueAlertChannel.Result catalogue = CatalogueAlertChannel.Parse(result.CatalogueAlerts);

        if (catalogue.State == AlertChannelState.Unreadable)
        {
            _logger.LogWarning(
                "PDF export: the catalogue alerts of result {ResultId} analysis {AnalysisId} tenant {TenantId} could not be read " +
                "({Received} element(s) received); the catalogue findings are left out of the report.",
                result.ResultId, result.AnalysisId, result.TenantId, catalogue.Received);
        }
        else if (catalogue.Dropped > 0)
        {
            _logger.LogWarning(
                "PDF export: {Dropped} of {Received} catalogue alerts of result {ResultId} tenant {TenantId} had no description and were left out.",
                catalogue.Dropped, catalogue.Received, result.ResultId, result.TenantId);
        }

        return catalogue;
    }

    private void AddFindings(
        ITextDocument doc, Fonts fonts, AnalysisResult result, CatalogueAlertChannel.Result catalogue,
        IReadOnlyDictionary<string, string> alertDisplayNames)
    {
        List<AlertItem> observations = ReadObservations(result);
        bool catalogueBlock = catalogue.State != AlertChannelState.Unreadable;

        if (!catalogueBlock && observations.Count == 0)
            return;

        // iText's keep-with-next does not chain, so each block's headings travel with its first row as ONE
        // keep-together unit: a heading is never left alone at the foot of a page.
        Div lead = new Div().SetKeepTogether(true).Add(SubHeadingParagraph(fonts, FindingsHeading));

        // ── The deterministic channel ──────────────────────────────────────────────
        if (catalogueBlock)
        {
            AddChannelHeading(lead, fonts, CatalogueHeading, CatalogueNote, primary: true);

            // Most severe first; a stable sort keeps the engine's own order within one severity.
            List<Table> rows = catalogue.Alerts.Count == 0
                ? [CatalogueEmptyNotice(fonts)]
                : catalogue.Alerts
                    .OrderByDescending(a => PdfLabels.AlertSeverity(a.Level).Rank)
                    .Select(alert => FindingRow(fonts, alert.Level, CatalogueTitle(alert), alert.Description, CatalogueMetricFormat.MetricLine(alert), primary: true))
                    .ToList();

            AddBlock(doc, lead, rows);
            lead = new Div().SetKeepTogether(true);
        }

        // ── The model channel: subordinate, and only when the model reported something ─────────────
        if (observations.Count == 0)
            return;

        if (catalogueBlock)
        {
            lead.Add(new Div()
                .SetMargin(0)
                .SetMarginTop(6)
                .SetMarginBottom(4)
                .SetBorderTop(new SolidBorder(SurfaceBorder, 0.5f)));
        }

        AddChannelHeading(lead, fonts, ModelHeading, ModelNote, primary: false);

        AddBlock(doc, lead, observations
            .OrderByDescending(a => PdfLabels.AlertSeverity(a.Level).Rank)
            .Select(alert => FindingRow(fonts, alert.Level, AlertTitles.Resolve(alert.Code, alert.Title, alertDisplayNames), alert.Description, metricLine: null, primary: false))
            .ToList());
    }

    // The lead (headings) with the first row as one unit, then the remaining rows on their own.
    private static void AddBlock(ITextDocument doc, Div lead, IReadOnlyList<Table> rows)
    {
        if (rows.Count > 0)
            lead.Add(rows[0]);
        doc.Add(lead);

        foreach (Table row in rows.Skip(1))
            doc.Add(row);
    }

    // The catalogue's own es-AR title, as stored; only a blank one falls back to the code.
    private static string? CatalogueTitle(CatalogueAlertChannel.Item alert) =>
        !string.IsNullOrWhiteSpace(alert.Title) ? alert.Title
        : alert.Code.Length == 0 ? null
        : AlertTitles.Humanize(alert.Code);

    /// <summary>
    /// The model channel (<c>RiskAlerts</c>), with today's export-time percentage reconciliation (not persisted; it
    /// mirrors the executive-summary block and AnalysisController.GetById's read-time repair). Unreadable → empty.
    /// </summary>
    private List<AlertItem> ReadObservations(AnalysisResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RiskAlerts))
            return [];

        List<AlertItem>? alerts;
        try { alerts = JsonSerializer.Deserialize<List<AlertItem>>(result.RiskAlerts, JsonOpts); }
        catch { alerts = null; }

        if (alerts is null || alerts.Count == 0)
            return [];

        try
        {
            // InPrintedUnits: the prose quotes amounts as printed, BalanceJson persists pesos.
            var accounts = string.IsNullOrWhiteSpace(result.BalanceJson)
                ? null
                : JsonSerializer.Deserialize<BalanceAccounts>(result.BalanceJson, JsonOpts)?.InPrintedUnits();

            if (accounts is not null)
            {
                foreach (var alert in alerts)
                {
                    if (alert is null || string.IsNullOrWhiteSpace(alert.Description)) continue;

                    var rec = _alertRatioReconciliationService.Reconcile(alert.Description, accounts);
                    if (!rec.Corrected) continue;

                    alert.Description = rec.Description;
                    _logger.LogWarning(
                        "PDF export: alert percentage reconciled [{Code}] result {ResultId} analysis {AnalysisId} " +
                        "tenant {TenantId}: clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                        alert.Code, result.ResultId, result.AnalysisId, result.TenantId,
                        rec.Clause, rec.StatedPercent, rec.ExpectedPercent, rec.Denominator);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PDF export: alert percentage reconciliation failed for result {ResultId}; rendering original alert text.",
                result.ResultId);
        }

        return alerts
            .Where(a => a is not null && (!string.IsNullOrWhiteSpace(a.Description) || !string.IsNullOrWhiteSpace(a.Title)))
            .ToList();
    }

    // The channel label and its one-line note, added to the lead unit that carries the first row. The model channel's pair is smaller and
    // muted, as the Portal's subordinate ChannelHeading.
    private static void AddChannelHeading(Div lead, Fonts fonts, string label, string note, bool primary)
    {
        lead.Add(new Paragraph(label.ToUpperInvariant())
            .SetFont(fonts.BodyMedium)
            .SetFontSize(primary ? 8f : 7.5f)
            .SetCharacterSpacing(0.4f)
            .SetFontColor(primary ? ColorBodyText : ColorMuted)
            .SetMargin(0)
            .SetMarginTop(primary ? 2 : 0));

        lead.Add(new Paragraph(note)
            .SetFont(fonts.Body)
            .SetFontSize(primary ? 8f : 7.5f)
            .SetFontColor(ColorMuted)
            .SetMargin(0)
            .SetMarginBottom(4));
    }

    private static Table CatalogueEmptyNotice(Fonts fonts)
    {
        Cell cell = new Cell()
            .SetBackgroundColor(Surface)
            .SetBorder(Border.NO_BORDER)
            .SetBorderLeft(new SolidBorder(SurfaceBorder, 2.5f))
            .SetPaddingLeft(8)
            .SetPaddingTop(6)
            .SetPaddingBottom(6)
            .Add(new Paragraph(CatalogueEmptyTitle).SetFont(fonts.BodyMedium).SetFontSize(9).SetFontColor(ColorBodyText).SetMargin(0).SetMarginBottom(2))
            .Add(new Paragraph(CatalogueEmptyDetail).SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(ColorMuted).SetMargin(0));

        return new Table(UnitValue.CreatePercentArray([1f]))
            .UseAllAvailableWidth()
            .SetMarginBottom(4)
            .SetKeepTogether(true)
            .AddCell(cell);
    }

    /// <summary>
    /// One finding. Primary (catalogue): tinted severity chip, 2.5 pt severity border, 9 pt type, and the measured
    /// value against its threshold when the rule reports them. Subordinate (model): the same severity colour on a
    /// thinner border — subordinate must not mean unreadable — with an untinted, muted chip and 8.5 pt type.
    /// </summary>
    private static Table FindingRow(Fonts fonts, string? level, string? title, string? description, string? metricLine, bool primary)
    {
        PdfFont regular = fonts.Body, med = fonts.BodyMedium;
        (string severity, _, PdfTone tone) = PdfLabels.AlertSeverity(level);
        float size = primary ? 9f : 8.5f;

        Text chip = new Text(" " + severity + " ").SetFont(med).SetFontSize(7.5f);
        if (primary)
            chip.SetFontColor(ToneColor(tone)).SetBackgroundColor(ToneSoft(tone) == Surface ? Theme.Paper : ToneSoft(tone));
        else
            chip.SetFontColor(ColorMuted);

        Paragraph head = new Paragraph().SetMargin(0).SetMarginBottom(2).Add(chip);
        if (!string.IsNullOrWhiteSpace(title))
            head.Add(new Text("  " + title.Trim()).SetFont(med).SetFontSize(size).SetFontColor(ColorBodyText));

        Cell cell = new Cell()
            .SetBackgroundColor(primary ? Surface : Theme.Paper)
            .SetBorder(Border.NO_BORDER)
            .SetBorderLeft(new SolidBorder(ToneColor(tone), primary ? 2.5f : 1.5f))
            .SetPaddingLeft(8)
            .SetPaddingTop(primary ? 6 : 4)
            .SetPaddingBottom(primary ? 6 : 4)
            .Add(head);

        if (!string.IsNullOrWhiteSpace(description))
        {
            cell.Add(new Paragraph(description)
                .SetFont(regular)
                .SetFontSize(size)
                .SetFontColor(ColorBodyText)
                .SetMargin(0));
        }

        if (metricLine is not null)
        {
            cell.Add(new Paragraph(metricLine)
                .SetFont(med)
                .SetFontSize(8)
                .SetFontColor(ColorMuted)
                .SetMargin(0)
                .SetMarginTop(2));
        }

        return new Table(UnitValue.CreatePercentArray([1f]))
            .UseAllAvailableWidth()
            .SetMarginBottom(primary ? 4 : 3)
            .SetKeepTogether(true)
            .AddCell(cell);
    }
}
