// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One [AnalysisEdits] row as the correction history reads it (no tenant, no analysis id).</summary>
public sealed record AnalysisEditHistoryItem(
    long     AnalysisEditId,
    string   FieldPath,
    string?  OldValue,
    string?  NewValue,
    string?  EditReason,
    Guid     EditedBy,
    DateTime EditedAt);
