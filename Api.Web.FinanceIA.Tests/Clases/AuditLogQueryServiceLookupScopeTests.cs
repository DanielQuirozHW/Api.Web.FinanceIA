// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers how <see cref="AuditLogQueryService.GetLogsAsync"/> resolves <c>EntityName</c>.
///
/// <para>
/// GET /api/audit resolved the display name of each audited entity by calling
/// <c>IEntityRepository.GetByIdAsync</c> inside the row loop. A dictionary de-duped repeats, so it
/// was never a plain N+1 — but <c>GetByIdAsync</c> is a FULL-ROW load, so every DISTINCT entity on
/// the page dragged all five <c>nvarchar(max)</c> source snapshots (fiscal / lufe / bcra / casfog /
/// nosis) off disk purely to read <c>BusinessName</c>. At <c>PageSize</c> 100 that is up to a
/// hundred blob-laden round-trips to spell a hundred names. Same fix as #142.
/// </para>
///
/// <para>
/// The load-bearing tests here are <see cref="GetLogs_NeverReadsAFullEntityRow"/> and
/// <see cref="GetLogs_AsksOnlyForTheEntityIdsThePageActuallyNames"/>. The rest pin the output,
/// because the whole point of the change is that the response did not move: same DTO, same fields,
/// same nulls. Same counter idiom as AlertsControllerLookupScopeTests — a call count asserted to be
/// zero, rather than a throwing stub, so a regression reads as a number and not as a
/// NotImplementedException from somewhere in the stack.
/// </para>
///
/// <para>
/// Document resolution is deliberately NOT batched and is asserted to stay per-id: Document has no
/// <c>nvarchar(max)</c> column, so a full-row document read is cheap. Hand-rolled private sealed
/// stubs (no mock library).
/// </para>
/// </summary>
public sealed class AuditLogQueryServiceLookupScopeTests
{
    private const string Tenant = "tenant-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubAuditLogRepository : IAuditLogRepository
    {
        public IReadOnlyList<AuditLog> ItemsToReturn { get; set; } = Array.Empty<AuditLog>();
        public int TotalToReturn { get; set; }

        public Task<(IReadOnlyList<AuditLog> Items, int Total)> GetPagedAsync(
            string tenantId, AuditLogFilter filter, CancellationToken ct)
            => Task.FromResult((ItemsToReturn, TotalToReturn));

        public Task<IReadOnlyList<string>> GetDistinctActionsAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public IReadOnlyList<Entity> EntitiesToReturn { get; set; } = Array.Empty<Entity>();

        /// <summary>Set to make the batch lookup fail, exercising the degraded path.</summary>
        public bool LookupThrows { get; set; }

        /// <summary>
        /// How often the service asked for a WHOLE entity row. Must stay 0: that read carries all
        /// five nvarchar(max) source snapshots, and the service only ever wanted BusinessName.
        /// </summary>
        public int GetByIdCallCount { get; private set; }

        /// <summary>How often the service read the entire tenant. Must stay 0.</summary>
        public int GetAllCallCount { get; private set; }

        /// <summary>How many batch lookups were issued — one page, one query.</summary>
        public int LookupCallCount { get; private set; }

        public List<Guid> LastLookupIds { get; private set; } = [];

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            GetByIdCallCount++;
            return Task.FromResult(EntitiesToReturn.FirstOrDefault(e => e.EntityId == entityId));
        }

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
        {
            GetAllCallCount++;
            return Task.FromResult(EntitiesToReturn);
        }

        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
        {
            LookupCallCount++;
            LastLookupIds = entityIds.ToList();

            if (LookupThrows) throw new InvalidOperationException("lookup unavailable");

            var wanted = LastLookupIds.ToHashSet();

            IReadOnlyDictionary<Guid, EntityNameProjection> map = EntitiesToReturn
                .Where(e => wanted.Contains(e.EntityId))
                .ToDictionary(
                    e => e.EntityId,
                    e => new EntityNameProjection
                    {
                        EntityId     = e.EntityId,
                        BusinessName = e.BusinessName,
                        CUIT         = e.CUIT
                    });

            return Task.FromResult(map);
        }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public IReadOnlyList<Document> DocumentsToReturn { get; set; } = Array.Empty<Document>();

        /// <summary>Document id whose read blows up, exercising per-document fault isolation.</summary>
        public Guid? ThrowingDocumentId { get; set; }

        public int GetByIdCallCount { get; private set; }

        public Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
        {
            GetByIdCallCount++;
            if (ThrowingDocumentId == documentId)
                throw new InvalidOperationException("document unavailable");

            return Task.FromResult(DocumentsToReturn.FirstOrDefault(d => d.DocumentId == documentId));
        }

        public Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Document>> GetByEntityIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Document> CreateAsync(Document document, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(
            Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SoftDeleteAsync(
            Guid documentId, string tenantId, DateTime deletedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Document?> FindRecentByHashAsync(Guid entityId, string tenantId, string? fileHashSha256, string documentType, DateTime uploadedSinceUtc, CancellationToken ct)
            => Task.FromResult<Document?>(null);

        public Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<int> SumExtractionAttemptsByHashAsync(
            string? fileHashSha256, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task RecordExtractionFailureAsync(
            Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public IReadOnlyList<User> UsersToReturn { get; set; } = Array.Empty<User>();

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            var wanted = userIds.ToHashSet();
            IReadOnlyList<User> found = UsersToReturn.Where(u => wanted.Contains(u.UserId)).ToList();
            return Task.FromResult(found);
        }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static AuditLogQueryService BuildSut(
        StubAuditLogRepository auditRepo,
        StubEntityRepository entityRepo,
        StubDocumentRepository documentRepo,
        StubUserRepository? userRepo = null)
        => new(
            auditRepo,
            userRepo ?? new StubUserRepository(),
            entityRepo,
            documentRepo,
            NullLogger<AuditLogQueryService>.Instance);

    private static AuditLog Log(string? entityType, string? entityId, long id = 1) => new()
    {
        AuditLogId = id,
        TenantId   = Tenant,
        Action     = "UPDATE",
        EntityType = entityType,
        EntityId   = entityId,
        Severity   = "Info",
        Timestamp  = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// An entity fixture with all five snapshots populated. Nothing here should ever reach the
    /// service: the lookup projects three columns, and none of them is a snapshot.
    /// </summary>
    private static Entity Ent(Guid entityId, string businessName) => new()
    {
        EntityId       = entityId,
        TenantId       = Tenant,
        BusinessName   = businessName,
        CUIT           = "30-12345678-9",
        FiscalDataJson = """{"a":1}""",
        LufeDataJson   = """{"b":2}""",
        BcraDataJson   = """{"c":3}""",
        CasfogDataJson = """{"d":4}""",
        NosisDataJson  = """{"e":5}"""
    };

    private static Document Doc(Guid documentId, Guid ownerEntityId) => new()
    {
        DocumentId = documentId,
        TenantId   = Tenant,
        EntityId   = ownerEntityId
    };

    private static readonly AuditLogFilter Filter = new() { Page = 1, PageSize = 20 };

    // ── The change ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. Neither resolution path may load a whole entity row — that read
    /// carries all five nvarchar(max) source snapshots, and it ran once per distinct entity on the
    /// page. One batch query replaces the lot.
    /// </summary>
    [Fact]
    public async Task GetLogs_NeverReadsAFullEntityRow()
    {
        var entityId = Guid.NewGuid();
        var docId    = Guid.NewGuid();
        var docOwner = Guid.NewGuid();

        var auditRepo = new StubAuditLogRepository
        {
            ItemsToReturn =
            [
                Log("Entity", entityId.ToString(), 1),
                Log("Document", docId.ToString(), 2)
            ],
            TotalToReturn = 2
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A."), Ent(docOwner, "Textil Andina S.R.L.")]
        };
        var docRepo = new StubDocumentRepository { DocumentsToReturn = [Doc(docId, docOwner)] };

        await BuildSut(auditRepo, entityRepo, docRepo).GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.Equal(0, entityRepo.GetByIdCallCount);
        Assert.Equal(0, entityRepo.GetAllCallCount);
        Assert.Equal(1, entityRepo.LookupCallCount);
    }

    /// <summary>
    /// The lookup is driven by the rows in hand, not by the tenant: the distinct union of the ids
    /// "Entity" rows name directly and the owner ids reached through "Document" rows. Entities the
    /// page never mentions must not be asked for.
    /// </summary>
    [Fact]
    public async Task GetLogs_AsksOnlyForTheEntityIdsThePageActuallyNames()
    {
        var wantedEntity   = Guid.NewGuid();
        var docId          = Guid.NewGuid();
        var docOwner       = Guid.NewGuid();
        var strangerEntity = Guid.NewGuid();

        var auditRepo = new StubAuditLogRepository
        {
            // The same entity and the same document twice, so the de-duping is exercised, plus
            // rows whose ids must never reach the lookup at all.
            ItemsToReturn =
            [
                Log("Entity", wantedEntity.ToString(), 1),
                Log("Entity", wantedEntity.ToString(), 2),
                Log("Document", docId.ToString(), 3),
                Log("Document", docId.ToString(), 4),
                Log("Login", Guid.NewGuid().ToString(), 5),      // other type → never looked up
                Log("Analysis", Guid.NewGuid().ToString(), 6),   // other type → never looked up
                Log("Entity", "not-a-guid", 7),                  // unparseable → never looked up
                Log("Entity", null, 8),                          // no id → never looked up
                Log(null, null, 9)
            ],
            TotalToReturn = 9
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn =
            [
                Ent(wantedEntity, "Metalúrgica del Sur S.A."),
                Ent(docOwner, "Textil Andina S.R.L."),
                Ent(strangerEntity, "Otra S.A.")
            ]
        };
        var docRepo = new StubDocumentRepository { DocumentsToReturn = [Doc(docId, docOwner)] };

        await BuildSut(auditRepo, entityRepo, docRepo).GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.Equal(
            new[] { wantedEntity, docOwner }.OrderBy(g => g),
            entityRepo.LastLookupIds.OrderBy(g => g));

        // The repeated document was resolved once and cached, not re-read per row.
        Assert.Equal(1, docRepo.GetByIdCallCount);
    }

    // ── The output did not move ───────────────────────────────────────────────

    /// <summary>Both resolvable paths still spell the right name.</summary>
    [Fact]
    public async Task GetLogs_ResolvesNamesForEntityAndDocumentRows()
    {
        var entityId = Guid.NewGuid();
        var docId    = Guid.NewGuid();
        var docOwner = Guid.NewGuid();

        var auditRepo = new StubAuditLogRepository
        {
            ItemsToReturn =
            [
                Log("Entity", entityId.ToString(), 1),
                Log("Document", docId.ToString(), 2)
            ],
            TotalToReturn = 2
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A."), Ent(docOwner, "Textil Andina S.R.L.")]
        };
        var docRepo = new StubDocumentRepository { DocumentsToReturn = [Doc(docId, docOwner)] };

        var response = await BuildSut(auditRepo, entityRepo, docRepo)
            .GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.Equal("Metalúrgica del Sur S.A.", response.Items.Single(i => i.AuditLogId == 1).EntityName);
        Assert.Equal("Textil Andina S.R.L.", response.Items.Single(i => i.AuditLogId == 2).EntityName);
    }

    /// <summary>
    /// Everything that could not be resolved before still resolves to null: other entity types, an
    /// id belonging to no entity in this tenant, an unparseable id, and a document that is gone.
    /// </summary>
    [Fact]
    public async Task GetLogs_LeavesEntityNameNullWhenUnresolvable()
    {
        var missingEntity = Guid.NewGuid();
        var missingDoc    = Guid.NewGuid();

        var auditRepo = new StubAuditLogRepository
        {
            ItemsToReturn =
            [
                Log("Login", Guid.NewGuid().ToString(), 1),
                Log("System", Guid.NewGuid().ToString(), 2),
                Log("Config", Guid.NewGuid().ToString(), 3),
                Log("Analysis", Guid.NewGuid().ToString(), 4),
                Log("Entity", missingEntity.ToString(), 5),   // no such entity in this tenant
                Log("Entity", "not-a-guid", 6),
                Log("Document", missingDoc.ToString(), 7)     // no such document
            ],
            TotalToReturn = 7
        };
        var entityRepo = new StubEntityRepository();   // knows about nothing
        var docRepo    = new StubDocumentRepository();

        var response = await BuildSut(auditRepo, entityRepo, docRepo)
            .GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.All(response.Items, i => Assert.Null(i.EntityName));
        Assert.Equal(0, entityRepo.GetByIdCallCount);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────

    /// <summary>
    /// A name that cannot be spelled must not cost the auditor the page. The batch lookup failing
    /// degrades every EntityName to null; it does not 500 the audit log.
    /// </summary>
    [Fact]
    public async Task GetLogs_BatchFailureDegradesInsteadOfThrowing()
    {
        var entityId = Guid.NewGuid();

        var auditRepo = new StubAuditLogRepository
        {
            ItemsToReturn = [Log("Entity", entityId.ToString(), 1), Log("Login", null, 2)],
            TotalToReturn = 2
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A.")],
            LookupThrows     = true
        };

        var response = await BuildSut(auditRepo, entityRepo, new StubDocumentRepository())
            .GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.Equal(2, response.Items.Count);
        Assert.All(response.Items, i => Assert.Null(i.EntityName));
    }

    /// <summary>
    /// One unreadable document nulls its own row's name and nothing else — the other rows still
    /// resolve, exactly as the old per-item try/catch did.
    /// </summary>
    [Fact]
    public async Task GetLogs_DocumentFailureDoesNotAbortThePage()
    {
        var entityId  = Guid.NewGuid();
        var goodDocId = Guid.NewGuid();
        var badDocId  = Guid.NewGuid();
        var docOwner  = Guid.NewGuid();

        var auditRepo = new StubAuditLogRepository
        {
            ItemsToReturn =
            [
                Log("Entity", entityId.ToString(), 1),
                Log("Document", goodDocId.ToString(), 2),
                Log("Document", badDocId.ToString(), 3)
            ],
            TotalToReturn = 3
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A."), Ent(docOwner, "Textil Andina S.R.L.")]
        };
        var docRepo = new StubDocumentRepository
        {
            DocumentsToReturn  = [Doc(goodDocId, docOwner)],
            ThrowingDocumentId = badDocId
        };

        var response = await BuildSut(auditRepo, entityRepo, docRepo)
            .GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.Equal("Metalúrgica del Sur S.A.", response.Items.Single(i => i.AuditLogId == 1).EntityName);
        Assert.Equal("Textil Andina S.R.L.", response.Items.Single(i => i.AuditLogId == 2).EntityName);
        Assert.Null(response.Items.Single(i => i.AuditLogId == 3).EntityName);
    }

    // ── Contract ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The response is a pure perf change: same envelope, same per-item fields, same Spanish type
    /// labels, users still resolved. Deployable without touching the Portal.
    /// </summary>
    [Fact]
    public async Task GetLogs_ResponseShapeUnchanged()
    {
        var entityId = Guid.NewGuid();
        var docId    = Guid.NewGuid();
        var docOwner = Guid.NewGuid();
        var userId   = Guid.NewGuid();

        var entityLog = Log("Entity", entityId.ToString(), 1);
        entityLog.UserId    = userId;
        entityLog.IpAddress = "10.0.0.1";
        entityLog.Source    = "Portal";

        var auditRepo = new StubAuditLogRepository
        {
            ItemsToReturn =
            [
                entityLog,
                Log("Document", docId.ToString(), 2),
                Log("Analysis", Guid.NewGuid().ToString(), 3)
            ],
            TotalToReturn = 57
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A."), Ent(docOwner, "Textil Andina S.R.L.")]
        };
        var docRepo  = new StubDocumentRepository { DocumentsToReturn = [Doc(docId, docOwner)] };
        var userRepo = new StubUserRepository
        {
            UsersToReturn =
            [
                new User { UserId = userId, TenantId = Tenant, DisplayName = "Ana Pérez", Email = "ana@example.com" }
            ]
        };

        var response = await BuildSut(auditRepo, entityRepo, docRepo, userRepo)
            .GetLogsAsync(Tenant, Filter, CancellationToken.None);

        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
        Assert.Equal(57, response.Total);
        Assert.Equal(3, response.Items.Count);

        var entityRow = response.Items.Single(i => i.AuditLogId == 1);
        Assert.Equal("Empresa", entityRow.EntityTypeLabel);
        Assert.Equal("Metalúrgica del Sur S.A.", entityRow.EntityName);
        Assert.Equal("Ana Pérez", entityRow.UserDisplayName);
        Assert.Equal("ana@example.com", entityRow.UserEmail);
        Assert.Equal("10.0.0.1", entityRow.IpAddress);
        Assert.Equal("Portal", entityRow.Source);
        Assert.Equal("UPDATE", entityRow.Action);
        Assert.Equal("Info", entityRow.Severity);
        Assert.Equal(entityId.ToString(), entityRow.EntityId);

        Assert.Equal("Documento", response.Items.Single(i => i.AuditLogId == 2).EntityTypeLabel);

        var analysisRow = response.Items.Single(i => i.AuditLogId == 3);
        Assert.Equal("Análisis", analysisRow.EntityTypeLabel);
        Assert.Null(analysisRow.EntityName);
    }
}
