// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>A balance correction whose REQUEST passed validation: trimmed reason and parsed corrections.</summary>
/// <param name="Reason">The trimmed reason, 10..1000 characters.</param>
/// <param name="Corrections">1..30 corrections, each to a different path.</param>
/// <param name="Origin">One of <see cref="BalanceCorrectionOrigins"/>; manual unless the request said otherwise.</param>
public sealed record BalanceCorrectionCommand(
    string Reason, IReadOnlyList<BalanceCorrectionEntry> Corrections, string Origin = BalanceCorrectionOrigins.Manual);

/// <summary>One validated correction: a whitelisted path and a finite value in pesos, or null.</summary>
public sealed record BalanceCorrectionEntry(BalanceCorrectionPath Path, double? Value);

/// <summary>One correction as applied: the value right before it and the value written.</summary>
public sealed record AppliedBalanceCorrection(string Path, double? OldValue, double? NewValue);
