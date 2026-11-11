// Project : Api.Web.FinanceIA
// Created : 2026-04-27
// Modified: 2026-04-27

using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Resolves user's Dynamics 365 contactid/
/// 
/// 
/// id from Firestore.
/// Used by SearchController to enforce row-level security.
/// Queries the <c>usuarios</c> collection for a document matching the Firebase UID.
/// </summary>
public sealed class FirestoreUserContextResolver : IUserContextResolver
{
    private readonly ILogger<FirestoreUserContextResolver> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FirestoreUserContextResolver"/>.
    /// </summary>
    public FirestoreUserContextResolver(ILogger<FirestoreUserContextResolver> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserContext?> ResolveAsync(
        string uid,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(tenantContext.FirebaseCredentialJson));
        var credential = GoogleCredential.FromStream(stream);

        using var jsonDoc = JsonDocument.Parse(tenantContext.FirebaseCredentialJson);
        var projectId = jsonDoc.RootElement.GetProperty("project_id").GetString();

        var firestoreDb = new FirestoreDbBuilder
        {
            ProjectId = projectId,
            Credential = credential
        }.Build();

        _logger.LogDebug(
            "Resolving user context for tenant {TenantId}",
            tenantContext.TenantId);

        var query = firestoreDb
            .Collection("usuarios")
            .WhereEqualTo("uid", uid)
            .Limit(1);

        var snapshot = await query.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot.Count == 0)
        {
            _logger.LogWarning(
                "User not found in Firestore for tenant {TenantId}",
                tenantContext.TenantId);

            return null;
        }

        var doc = snapshot.Documents[0];

        var data = doc.ToDictionary();

        var contactId  = data.TryGetValue("contactid",     out var cVal) ? cVal?.ToString() : null;
        var accountId  = data.TryGetValue("accountid",     out var aVal) ? aVal?.ToString() : null;
        var employeeId = data.TryGetValue("new_empleadoid", out var eVal) ? eVal?.ToString() : null;
        var email      = data.TryGetValue("email",         out var mVal) ? mVal?.ToString() : null;

        if (string.IsNullOrWhiteSpace(contactId) &&
            string.IsNullOrWhiteSpace(accountId) &&
            string.IsNullOrWhiteSpace(employeeId))
        {
            _logger.LogWarning(
                "User {Uid} in tenant {TenantId} has no identity IDs",
                uid, tenantContext.TenantId);

            return null;
        }

        return new UserContext
        {
            ContactId  = contactId  ?? string.Empty,
            AccountId  = accountId  ?? string.Empty,
            EmployeeId = employeeId ?? string.Empty,
            Email      = email
        };
    }
}
