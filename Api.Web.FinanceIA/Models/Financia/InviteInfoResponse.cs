// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-05-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response body for the GET /api/bff/auth/invite/{token} endpoint.
/// Contains only the information needed to pre-fill the registration form.
/// Never reveals whether a token is expired, used or non-existent.
/// </summary>
public sealed record InviteInfoResponse(
    string Email,
    string Role,
    string TenantCode
);
