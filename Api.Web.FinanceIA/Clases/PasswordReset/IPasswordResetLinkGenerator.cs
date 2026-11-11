// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Generates a Firebase password-reset action link with the TENANT's Firebase Admin app. A seam
/// over the sealed <c>FirebaseAuth</c>, so the password-reset flow can be tested without Firebase.
/// </summary>
public interface IPasswordResetLinkGenerator
{
    /// <summary>
    /// Returns the action link Firebase generated for <paramref name="email"/>, with
    /// <paramref name="continueUrl"/> as its <c>ActionCodeSettings.Url</c>, or null when the
    /// tenant's Firebase project has no account for that address. Any other Firebase failure throws
    /// <see cref="Exceptions.FirebaseServiceException"/>. The returned link is a credential: never log it.
    /// </summary>
    Task<string?> GenerateAsync(
        string email,
        string continueUrl,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
