// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-05

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The <see cref="IEntityRepository"/> the analysis chat reads (K5): the detail projection of the evaluated
/// entity (name, trade name, CUIT and its current LUFE snapshot), nothing else. Answers a SYNTHETIC entity for
/// any id by default, so a chat suite that is not about redaction sees a turn go through exactly as before.
/// The members the chat never calls throw, so a new read cannot hide behind this stub.
/// </summary>
public sealed class ChatEntityRepositoryStub : IEntityRepository
{
    /// <summary>A synthetic company name — never a real one.</summary>
    public const string DefaultBusinessName = "Compania Sintetica de Prueba S.A.";

    /// <summary>A synthetic CUIT — never a real one.</summary>
    public const string DefaultCuit = "30-99887766-5";

    /// <summary>What <see cref="GetDetailByIdAsync"/> answers; null reads as "no such entity".</summary>
    public EntityDetailProjection? Entity { get; set; } = new()
    {
        EntityId     = Guid.NewGuid(),
        BusinessName = DefaultBusinessName,
        CUIT         = DefaultCuit
    };

    /// <summary>When set, every read throws it — the lookup failing.</summary>
    public Exception? ThrowOnRead { get; set; }

    /// <summary>The entity ids asked for, in order.</summary>
    public List<Guid> RequestedEntityIds { get; } = [];

    /// <inheritdoc />
    public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        RequestedEntityIds.Add(entityId);
        if (ThrowOnRead is not null) throw ThrowOnRead;
        return Task.FromResult(Entity);
    }

    private static NotSupportedException NotTheChat() => new("The analysis chat does not read this.");

    /// <inheritdoc />
    public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        => throw NotTheChat();

    /// <inheritdoc />
    public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
        => throw NotTheChat();

    /// <inheritdoc />
    public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<Entity> CreateAsync(Entity entity, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
        string tenantId, int skip, int take, CancellationToken ct) => throw NotTheChat();

    /// <inheritdoc />
    public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
        => throw NotTheChat();

    /// <inheritdoc />
    public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
        => throw NotTheChat();

    /// <inheritdoc />
    public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
        => throw NotTheChat();
}
