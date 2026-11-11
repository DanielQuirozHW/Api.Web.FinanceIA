// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>A legajo integrity check that did not run because an operand was not extracted.</summary>
/// <param name="Name">The check's identifier.</param>
/// <param name="Subject">The group it would have been about, or null.</param>
public sealed record LegajoIntegritySkip(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("subject"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject = null);
