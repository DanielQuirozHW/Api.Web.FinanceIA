// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>What <c>AlertRepository.UnresolveAsync</c> did. The controller maps it to 204 / 404 / 409.</summary>
public enum AlertUnresolveOutcome
{
    /// <summary>Reopened, or already open (a no-op, idempotent like resolve).</summary>
    Reopened,

    /// <summary>No such alert in this tenant.</summary>
    NotFound,

    /// <summary>The system resolved it (ResolvedBy null): the situation cleared, there is nothing to reopen.</summary>
    AutoResolved,

    /// <summary>Another OPEN alert exists for the same situation — the system already recreated it.</summary>
    OpenDuplicate
}
