// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The solicitante's pre-filter counts before and after a re-evaluation.</summary>
public sealed record PersonRescoreFilterCounts(PersonFilterCounts Previous, PersonFilterCounts New);
