// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunListWorld;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// <see cref="DocumentRepository.FindRecentByHashAsync"/>, the idempotency read of the company upload: tenant,
/// entity, type, hash and window scoped; deleted and legajo rows never match; newest first.
/// </summary>
public sealed class DocumentRepositoryFindRecentByHashTests : IDisposable
{
    private const string Hash = "0a1b2c";

    private readonly AnalysisRunListWorld _world = new();
    private readonly DocumentRepository   _sut;

    public DocumentRepositoryFindRecentByHashTests()
    {
        _sut = new DocumentRepository(_world.Db, NullLogger<DocumentRepository>.Instance);
    }

    public void Dispose() => _world.Dispose();

    private Task<Document?> FindAsync(Entity entity, string tenantId = TenantA, string? hash = Hash,
        string type = DocumentTypes.Balance, DateTime? since = null)
        => _sut.FindRecentByHashAsync(entity.EntityId, tenantId, hash, type, since ?? Now.AddMinutes(-2), CancellationToken.None);

    [Fact]
    public async Task ReturnsTheNewestMatchWithinTheWindow()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: Now.AddSeconds(-90), fileHash: Hash);
        var newest = _world.AddDocument(TenantA, entity, "Pending", uploadedAt: Now.AddSeconds(-10), fileHash: Hash);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: Now.AddMinutes(-10), fileHash: Hash);

        Assert.Equal(newest.DocumentId, (await FindAsync(entity))?.DocumentId);
    }

    [Fact]
    public async Task OutsideTheWindow_ReturnsNull()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: Now.AddMinutes(-3), fileHash: Hash);

        Assert.Null(await FindAsync(entity));
    }

    [Fact]
    public async Task OtherTenant_OtherEntity_OtherType_DeletedAndLegajoRows_NeverMatch()
    {
        var entity  = _world.AddEntity(TenantA);
        var sibling = _world.AddEntity(TenantA);
        var foreign = _world.AddEntity(TenantB);
        var recent  = Now.AddSeconds(-5);

        _world.AddDocument(TenantB, foreign, "Pending", uploadedAt: recent, fileHash: Hash);
        _world.AddDocument(TenantA, sibling, "Pending", uploadedAt: recent, fileHash: Hash);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: recent, fileHash: Hash, documentType: DocumentTypes.Complemento);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: recent, fileHash: Hash, isDeleted: true);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: recent, fileHash: Hash,
            personLegajoId: Guid.NewGuid(), legajoStatus: LegajoStatuses.Uploaded);

        Assert.Null(await FindAsync(entity));
        Assert.Null(await FindAsync(foreign, tenantId: TenantA));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BlankHash_NeverMatches(string? hash)
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Pending", uploadedAt: Now.AddSeconds(-5), fileHash: "");

        Assert.Null(await FindAsync(entity, hash: hash));
    }
}
