// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One item of GET /api/analyses/{id}/balance-corrections: one corrected figure, read from [AnalysisEdits].
/// <see cref="EditedBy"/> is a UserId string, like every other actor id on the analysis payloads;
/// <see cref="EditedByName"/> is "Usuario" when the id no longer resolves. <see cref="OldValue"/> is the
/// value right before THIS correction, in pesos; null when the figure was not extracted.
/// <see cref="Origin"/> is the <c>origin</c> of the matching <c>manual_corrections</c> entry
/// (<see cref="BalanceCorrectionOrigins"/>), "manual" when there is none.
/// </summary>
public sealed record BalanceCorrectionHistoryResponse(
    long     EditId,
    string   FieldPath,
    double?  OldValue,
    double?  NewValue,
    string?  Reason,
    DateTime EditedAt,
    string   EditedBy,
    string?  EditedByName,
    string   Origin = BalanceCorrectionOrigins.Manual);
