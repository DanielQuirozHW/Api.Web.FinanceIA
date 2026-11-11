// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-05-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for the POST /api/bff/auth/register-invited endpoint.
/// The Firebase ID token must be provided in the Authorization: Bearer header.
/// The role assigned to the new user is taken from the invitation record —
/// it is never accepted from the client.
/// </summary>
public sealed record RegisterInvitedRequest(
    string InviteToken,
    string DisplayName
);
