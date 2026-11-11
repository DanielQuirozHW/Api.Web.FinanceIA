// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.NosisReport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// The NOSIS Manager "Informe Individual" import (DDL 087): an analyst uploads the PDF on a PyME's NOSIS
/// tab, the API reads it IN MEMORY with <see cref="INosisManagerReportParser"/> (deterministic, no model
/// call), stores only company-level data as JSON on the entity, and discards the file. Nothing here
/// reaches a prompt, a score or an alert.
/// </summary>
public sealed partial class EntitiesController
{
    internal const string NosisReportImportedAuditAction = "Entity.NosisReportImported";
    internal const string NosisReportReplacedAuditAction = "Entity.NosisReportReplaced";
    internal const string NosisReportDeletedAuditAction  = "Entity.NosisReportDeleted";

    internal const string NosisReportNotRecognizedCode       = "nosis_report_not_recognized";
    internal const string NosisReportCuitMismatchCode        = "nosis_report_cuit_mismatch";
    internal const string NosisReportPersonTypeUnsupportedCode = "nosis_report_person_type_unsupported";

    private const string PdfContentType = "application/pdf";

    private static readonly System.Globalization.CultureInfo EsArCulture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Imports a NOSIS Manager "Informe Individual" for a company. Multipart field <c>file</c>: a PDF of
    /// at most <see cref="PdfSizeLimits.MaxPdfLabel"/> and <see cref="NosisManagerReportParser.MaxPages"/>
    /// pages. The same file again (same SHA-256 and parser version as the stored report) writes nothing;
    /// another file, or the same one after a parser upgrade, replaces the stored report.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="file">The report PDF.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="EntityNosisResponse"/>; 400 for a bad file or NOSIS not enabled for the
    /// tenant; 404 for an unknown entity; 422 <c>nosis_report_not_recognized</c>,
    /// <c>nosis_report_cuit_mismatch</c> or <c>nosis_report_person_type_unsupported</c>.
    /// </returns>
    [HttpPost("{id:guid}/nosis/report")]
    [Authorize(Roles = Roles.Operators)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PdfSizeLimits.MaxUploadRequestBytes)]
    [ProducesResponseType(typeof(EntityNosisResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> ImportNosisReportAsync(
        Guid id,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.ImportNosisReportAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        // ── The file: the checks every PDF upload makes ──────────────────────────────

        if (file is null || file.Length == 0)
            return ValidationProblem("file", "Se requiere un archivo no vacío.");

        if (file.Length > PdfSizeLimits.MaxPdfBytes)
            return ValidationProblem("file", $"El archivo supera el tamaño máximo permitido de {PdfSizeLimits.MaxPdfLabel}.");

        if (!string.Equals(file.ContentType, PdfContentType, StringComparison.OrdinalIgnoreCase))
            return ValidationProblem("file", "Solo se aceptan archivos application/pdf.");

        await using (var header = file.OpenReadStream())
        {
            if (!await FileSignatureValidator.MatchesAsync(header, FileSignatureType.Pdf, cancellationToken).ConfigureAwait(false))
                return ValidationProblem("file", "El archivo no es un PDF válido.");
        }

        // ── The entity, the person type, the entitlement ─────────────────────────────

        var subject = await _nosisReportRepository.GetSubjectAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (subject is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        if (subject.PersonType == PersonTypes.Humana)
        {
            return CodedProblem(422, "Unprocessable Entity",
                "El informe de NOSIS Manager solo se importa para empresas (personas jurídicas).",
                NosisReportPersonTypeUnsupportedCode);
        }

        // The tenant's entitlement only: the report is a file the analyst brings, so the NOSIS web
        // service credentials refresh-nosis needs (IsNosisConfigured) play no part here.
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (!profile.NosisEnabled)
        {
            _logger.LogInformation(
                "NOSIS report import: requested for entity {EntityId} tenant {TenantId} while the tenant profile disables NOSIS.",
                id, tenantId);
            return Problem(statusCode: 400, title: "Bad Request", detail: "NOSIS no está habilitado para este tenant.");
        }

        var userId = await CurrentUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        // ── Read the file in memory; it is never stored ──────────────────────────────

        byte[] bytes;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using (await _nosisReportLock.AcquireAsync($"nosis-report|{tenantId}|{id:N}", cancellationToken).ConfigureAwait(false))
        {
            var (storedSha, storedVersion) = await _nosisReportRepository
                .GetStoredFingerprintAsync(id, tenantId, cancellationToken).ConfigureAwait(false);

            // The same file, read by this same parser: nothing to do. The same file read by an older
            // parser is read again, so a parser fix reaches a report by re-uploading it.
            if (string.Equals(storedSha, sha256, StringComparison.Ordinal)
                && string.Equals(storedVersion, NosisManagerReportParser.ParserVersion, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "NOSIS report import: entity {EntityId} tenant {TenantId} already holds this file; nothing written.",
                    id, tenantId);
                return Ok(await BuildNosisResponseAsync(id, tenantId, cancellationToken).ConfigureAwait(false));
            }

            var stopwatch = Stopwatch.StartNew();
            var parsed = _nosisReportParser.Parse(bytes, cancellationToken);
            stopwatch.Stop();

            if (parsed.Outcome == NosisReportParseOutcome.TooManyPages)
            {
                _logger.LogWarning(
                    "NOSIS report import refused for entity {EntityId} tenant {TenantId}: {PageCount} pages.",
                    id, tenantId, parsed.PageCount);
                return ValidationProblem("file", $"El informe supera las {NosisManagerReportParser.MaxPages.ToString("N0", EsArCulture)} páginas.");
            }

            if (parsed.Outcome != NosisReportParseOutcome.Parsed || parsed.Report is null || parsed.ReportDate is null)
            {
                _logger.LogWarning(
                    "NOSIS report import: file not recognized for entity {EntityId} tenant {TenantId}: reason={Reason} pages={PageCount} elapsedMs={ElapsedMs}",
                    id, tenantId, parsed.Reason, parsed.PageCount, stopwatch.ElapsedMilliseconds);
                return CodedProblem(422, "Unprocessable Entity",
                    "El archivo no es un informe individual de NOSIS Manager o no se pudo leer.",
                    NosisReportNotRecognizedCode);
            }

            // Neither CUIT is logged or echoed: the comparison is the only use of either.
            if (!string.Equals(LegajoCuit.Digits(subject.CUIT), parsed.SubjectCuitDigits, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "NOSIS report import refused for entity {EntityId} tenant {TenantId}: the report is about another CUIT.",
                    id, tenantId);
                return CodedProblem(422, "Unprocessable Entity",
                    "El informe corresponde a otro CUIT que el de esta empresa.",
                    NosisReportCuitMismatchCode);
            }

            var previous = storedSha is null
                ? null
                : await _nosisReportRepository.GetAsync(id, tenantId, cancellationToken).ConfigureAwait(false);

            var importedAt = DateTime.UtcNow;
            var report = parsed.Report with { FileSha256 = sha256, ImportedAt = importedAt };
            var json = JsonSerializer.Serialize(report, NosisManagerReport.JsonOptions);

            try
            {
                if (!await _nosisReportRepository.SaveAsync(id, tenantId, json, parsed.ReportDate.Value, importedAt, CancellationToken.None).ConfigureAwait(false))
                    return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "NOSIS report import: failed to store the report for entity {EntityId} tenant {TenantId}.",
                    id, tenantId);
                return Problem(statusCode: 500, title: "Internal Server Error", detail: "No se pudo guardar el informe.");
            }

            var statuses = string.Join(',', report.Sections.Select(s => $"{s.Key}={s.Value}"));
            _logger.LogInformation(
                "NOSIS report imported for entity {EntityId} tenant {TenantId}: pages={PageCount} elapsedMs={ElapsedMs} sections={SectionStatuses} droppedStrings={DroppedStrings} replaced={Replaced}",
                id, tenantId, parsed.PageCount, stopwatch.ElapsedMilliseconds, statuses, parsed.DroppedStrings, storedSha is not null);

            _ = _auditService.LogAsync(
                tenantId:     tenantId,
                userId:       userId,
                action:       storedSha is null ? NosisReportImportedAuditAction : NosisReportReplacedAuditAction,
                severity:     AuditSeverities.Info,
                entityType:   "Entity",
                entityId:     id.ToString(),
                ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
                requestId:    HttpContext.TraceIdentifier,
                oldValueJson: storedSha is null ? null : JsonSerializer.Serialize(new
                {
                    fileSha256 = storedSha,
                    reportDate = ParseNosisReport(previous?.Json, id)?.ReportDate,
                }),
                newValueJson: JsonSerializer.Serialize(new
                {
                    fileSha256    = sha256,
                    reportDate    = report.ReportDate,
                    parserVersion = report.ParserVersion,
                    sections      = report.Sections,
                }),
                ct: CancellationToken.None);

            return Ok(await BuildNosisResponseAsync(id, tenantId, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Removes the imported NOSIS Manager report of an entity. Idempotent: an entity with no report
    /// answers the same 200 and writes nothing.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="EntityNosisResponse"/> (report null), or 404.</returns>
    [HttpDelete("{id:guid}/nosis/report")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityNosisResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> DeleteNosisReportAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.DeleteNosisReportAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var userId = await CurrentUserIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        await using (await _nosisReportLock.AcquireAsync($"nosis-report|{tenantId}|{id:N}", cancellationToken).ConfigureAwait(false))
        {
            var stored = await _nosisReportRepository.GetAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
                return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

            if (stored.Json is not null || stored.ReportDate is not null || stored.ImportedAt is not null)
            {
                var previous = ParseNosisReport(stored.Json, id);

                bool cleared;
                try
                {
                    cleared = await _nosisReportRepository.ClearAsync(id, tenantId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "NOSIS report delete: failed to clear the report of entity {EntityId} tenant {TenantId}.",
                        id, tenantId);
                    return Problem(statusCode: 500, title: "Internal Server Error", detail: "No se pudo eliminar el informe.");
                }

                if (cleared)
                {
                    _logger.LogInformation("NOSIS report deleted for entity {EntityId} tenant {TenantId}", id, tenantId);

                    _ = _auditService.LogAsync(
                        tenantId:     tenantId,
                        userId:       userId,
                        action:       NosisReportDeletedAuditAction,
                        severity:     AuditSeverities.Info,
                        entityType:   "Entity",
                        entityId:     id.ToString(),
                        ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
                        requestId:    HttpContext.TraceIdentifier,
                        oldValueJson: JsonSerializer.Serialize(new
                        {
                            fileSha256 = previous?.FileSha256,
                            reportDate = previous?.ReportDate,
                        }),
                        ct: CancellationToken.None);
                }
            }

            return Ok(await BuildNosisResponseAsync(id, tenantId, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The NOSIS tab's payload: the WS.01 snapshot and the imported report, both as stored.</summary>
    private async Task<EntityNosisResponse> BuildNosisResponseAsync(Guid id, string tenantId, CancellationToken ct)
    {
        var snapshot = await _entityRepository.GetNosisSnapshotAsync(id, tenantId, ct).ConfigureAwait(false);
        var report   = await _nosisReportRepository.GetAsync(id, tenantId, ct).ConfigureAwait(false);

        return new EntityNosisResponse
        {
            Nosis              = ParseSnapshot<NosisData>(snapshot?.Json, id, "NosisDataJson"),
            NosisDataUpdatedAt = snapshot?.UpdatedAt,
            Report             = ParseNosisReport(report?.Json, id),
        };
    }

    /// <summary>The stored report, or null (one Warning, never the content) when it does not deserialize.</summary>
    private NosisManagerReport? ParseNosisReport(string? json, Guid entityId)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<NosisManagerReport>(json, NosisManagerReport.JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not parse NosisReportJson for entity {EntityId} ({ExceptionType}). The report will be null.",
                entityId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The caller's user id, or null when the principal does not resolve to a user.</summary>
    private async Task<Guid?> CurrentUserIdAsync(CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return null;

        var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid, ct).ConfigureAwait(false);
        return user?.UserId;
    }
}
