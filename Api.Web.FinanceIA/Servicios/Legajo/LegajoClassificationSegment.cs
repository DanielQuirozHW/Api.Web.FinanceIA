// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>One part of a composite PDF, as the classification read it.</summary>
/// <param name="DocumentType">One of the legajo types, or "Desconocido".</param>
/// <param name="FromPage">1-based first page.</param>
/// <param name="ToPage">1-based last page (inclusive).</param>
public sealed record LegajoClassificationSegment(
    [property: JsonPropertyName("documentType")] string DocumentType,
    [property: JsonPropertyName("fromPage")] int FromPage,
    [property: JsonPropertyName("toPage")] int ToPage);
