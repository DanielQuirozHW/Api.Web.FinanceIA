// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers how the three PyME-notes endpoints — <see cref="EntitiesController.GetNotes"/>,
/// <see cref="EntitiesController.CreateNote"/> and <see cref="EntitiesController.DeleteNote"/> —
/// decide 404-vs-proceed.
///
/// <para>
/// All three took ~10s on a warm database, and the notes were never the cost: <c>Notes</c> is read
/// through IX_Notes_TenantId_EntityId_NoteId and carries nothing heavy. The expensive step was the
/// 404 guard, which called <c>IEntityRepository.GetByIdAsync</c> — a full-row load whose SELECT
/// names all five <c>nvarchar(max)</c> source snapshots, the LUFE one alone ~26KB on a
/// representative PyME — purely to null-check the result. None of the three ever read a field off
/// the entity: GetNotes reads notes by id, CreateNote builds the note from the request and the
/// caller, DeleteNote authorises off the note and the caller. They now call
/// <c>ExistsByIdAsync</c>, whose EXISTS names no column and so follows no LOB pointer.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="NotesEndpoints_NeverLoadTheWholeEntityRow"/>. The rest pin
/// the output, because the whole point of the change is that nothing observable moved: same 404,
/// same detail string, same DTOs, same status codes. Same counter idiom as
/// <see cref="AlertsControllerLookupScopeTests"/> — a call count asserted to be zero rather than a
/// throwing stub, so a regression reads as a number and not as a NotImplementedException from
/// somewhere in the stack.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs (no mock library). Only the four dependencies the notes actions
/// touch are stubbed; the fifteen others are passed as null! because these paths never reach them
/// (the constructor performs plain assignment). Argument order mirrors the ctor signature.
/// </para>
/// </summary>
public sealed class EntitiesControllerNotesTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public bool Exists { get; set; } = true;

        /// <summary>
        /// How often the endpoint asked for the whole entity row. Must stay 0: answering "does this
        /// PyME exist?" used to read all five nvarchar(max) snapshots off disk.
        /// </summary>
        public int GetByIdCallCount { get; private set; }

        /// <summary>How often the cheap existence check ran. Exactly one per request.</summary>
        public int ExistsCallCount { get; private set; }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            ExistsCallCount++;
            return Task.FromResult(Exists);
        }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            GetByIdCallCount++;
            return Task.FromResult<Entity?>(Exists ? new Entity { EntityId = entityId, TenantId = tenantId } : null);
        }

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
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

    private sealed class StubNoteRepository : INoteRepository
    {
        public IReadOnlyList<Note> NotesToReturn { get; set; } = Array.Empty<Note>();
        public SoftDeleteResult DeleteResult { get; set; } = SoftDeleteResult.Success;

        public int  GetByEntityCallCount { get; private set; }
        public Note? CreatedNote { get; private set; }
        public long? LastDeletedNoteId { get; private set; }

        public Task<IReadOnlyList<Note>> GetByEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            GetByEntityCallCount++;
            return Task.FromResult(NotesToReturn);
        }

        public Task CreateAsync(Note note, CancellationToken ct)
        {
            CreatedNote = note;
            return Task.CompletedTask;
        }

        public Task<SoftDeleteResult> SoftDeleteAsync(
            long noteId, string tenantId, Guid requestingUserId, bool isAdmin, CancellationToken ct)
        {
            LastDeletedNoteId = noteId;
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static User BuildUser(string role = "Analyst") => new()
    {
        UserId      = Guid.NewGuid(),
        TenantId    = Tenant,
        FirebaseUid = FirebaseUid,
        DisplayName = "Ana Analista",
        Role        = role
    };

    private static EntitiesController BuildSut(
        StubEntityRepository entityRepo,
        StubNoteRepository   noteRepo,
        StubUserRepository?  userRepo = null)
    {
        var sut = new EntitiesController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // ITenantContextAccessor
            entityRepo,                        // IEntityRepository
            null!,                             // IArcaPadronService
            null!,                             // IArcaConstanciaService
            null!,                             // IArcaWsaaService
            null!,                             // IApiCallLogService
            new StubAuditService(),            // IAuditService
            null!,                             // ILufeService
            null!,                             // IBcraService
            null!,                             // ICasfogService
            null!,                             // INosisService
            null!,                             // IIntegrationAccessService — never resolved by the notes endpoints
            null!,                             // IDocumentRepository
            null!,                             // IDocumentStorageService
            userRepo ?? new StubUserRepository { UserToReturn = BuildUser() }, // IUserRepository
            null!,                             // ICreditLineRepository
            null!,                             // IAnalysisRepository
            noteRepo,                          // INoteRepository
            null!,                             // IPersonLegajoRepository
            null!,                             // INosisReportRepository
            null!,                             // INosisManagerReportParser
            null!,                             // KeyedAsyncLock
            null!,                             // IConfiguration
            NullLogger<EntitiesController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("uid", FirebaseUid) }, "Test"))
            }
        };
        return sut;
    }

    private static Note NoteFixture(Guid entityId, long noteId = 1, bool isDeleted = false) => new()
    {
        NoteId       = noteId,
        EntityId     = entityId,
        TenantId     = Tenant,
        Content      = "El balance 2025 llegó incompleto.",
        AuthorUserId = Guid.NewGuid(),
        AuthorName   = "Ana Analista",
        CreatedAt    = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc),
        IsDeleted    = isDeleted
    };

    /// <summary>The three actions under one signature, so the counter test can drive them all.</summary>
    private static Task<IActionResult> Invoke(string action, EntitiesController sut, Guid entityId)
        => action switch
        {
            "get"    => sut.GetNotes(entityId, CancellationToken.None),
            "create" => sut.CreateNote(entityId, new CreateNoteRequest { Content = "Una nota." }, CancellationToken.None),
            _        => sut.DeleteNote(entityId, 1L, CancellationToken.None)
        };

    // ── The change ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. Every notes endpoint must answer its 404 guard with the existence
    /// check and never with a full-row load — that read dragged five nvarchar(max) snapshots off
    /// disk on every note listed, added or removed. Asserted on both branches of every action,
    /// because the absent case is the one where it is tempting to reach for the entity.
    /// </summary>
    [Theory]
    [InlineData("get",    true)]
    [InlineData("get",    false)]
    [InlineData("create", true)]
    [InlineData("create", false)]
    [InlineData("delete", true)]
    [InlineData("delete", false)]
    public async Task NotesEndpoints_NeverLoadTheWholeEntityRow(string action, bool entityExists)
    {
        var entityId   = Guid.NewGuid();
        var entityRepo = new StubEntityRepository { Exists = entityExists };

        await Invoke(action, BuildSut(entityRepo, new StubNoteRepository()), entityId);

        Assert.Equal(0, entityRepo.GetByIdCallCount);
        Assert.Equal(1, entityRepo.ExistsCallCount);
    }

    // ── The output, unchanged ─────────────────────────────────────────────────

    /// <summary>
    /// A missing entity is still a 404 carrying the exact same detail string, on all three actions.
    /// The client matches on this response, so "how existence is determined" changing must not
    /// change what comes back.
    /// </summary>
    [Theory]
    [InlineData("get")]
    [InlineData("create")]
    [InlineData("delete")]
    public async Task NotesEndpoints_WhenEntityAbsent_Return404WithTheSameDetail(string action)
    {
        var noteRepo = new StubNoteRepository();

        var result = await Invoke(
            action, BuildSut(new StubEntityRepository { Exists = false }, noteRepo), Guid.NewGuid());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(
            "No se encontró la entidad.",
            Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail);

        // The guard still short-circuits: nothing is read or written for an entity that is not there.
        Assert.Equal(0, noteRepo.GetByEntityCallCount);
        Assert.Null(noteRepo.CreatedNote);
        Assert.Null(noteRepo.LastDeletedNoteId);
    }

    /// <summary>
    /// GetNotes still returns the tenant's notes for the entity, and still blanks a tombstone's
    /// content while retaining its author and timestamp.
    /// </summary>
    [Fact]
    public async Task GetNotes_WhenEntityExists_ReturnsTheNotesAndStillBlanksTombstones()
    {
        var entityId = Guid.NewGuid();
        var noteRepo = new StubNoteRepository
        {
            NotesToReturn = [NoteFixture(entityId, 1), NoteFixture(entityId, 2, isDeleted: true)]
        };

        var result = await BuildSut(new StubEntityRepository { Exists = true }, noteRepo)
            .GetNotes(entityId, CancellationToken.None);

        var notes = Assert.IsType<List<NoteResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, notes.Count);

        Assert.Equal("El balance 2025 llegó incompleto.", notes[0].Content);
        Assert.False(notes[0].IsDeleted);

        Assert.Equal(string.Empty, notes[1].Content);   // tombstone
        Assert.True(notes[1].IsDeleted);
        Assert.Equal("Ana Analista", notes[1].AuthorName);
    }

    /// <summary>
    /// CreateNote still 201s and still persists the note stamped with the entity, the tenant and the
    /// resolved caller — the fields it reads off the CALLER, never off the entity.
    /// </summary>
    [Fact]
    public async Task CreateNote_WhenEntityExists_Returns201AndPersistsTheNote()
    {
        var entityId = Guid.NewGuid();
        var user     = BuildUser();
        var noteRepo = new StubNoteRepository();

        var result = await BuildSut(
                new StubEntityRepository { Exists = true },
                noteRepo,
                new StubUserRepository { UserToReturn = user })
            .CreateNote(entityId, new CreateNoteRequest { Content = "  Falta el certificado.  " },
                        CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);

        Assert.NotNull(noteRepo.CreatedNote);
        Assert.Equal(entityId,          noteRepo.CreatedNote!.EntityId);
        Assert.Equal(Tenant,            noteRepo.CreatedNote.TenantId);
        Assert.Equal("Falta el certificado.", noteRepo.CreatedNote.Content);   // trimmed
        Assert.Equal(user.UserId,       noteRepo.CreatedNote.AuthorUserId);
        Assert.Equal("Ana Analista",    noteRepo.CreatedNote.AuthorName);
        Assert.Null(noteRepo.CreatedNote.AnalysisId);                          // PyME-level note
    }

    /// <summary>
    /// The content validations still run BEFORE the existence check, so an empty body is a 400 and
    /// never reaches the database at all.
    /// </summary>
    [Fact]
    public async Task CreateNote_WhenContentBlank_Returns400WithoutCheckingTheEntity()
    {
        var entityRepo = new StubEntityRepository { Exists = true };
        var noteRepo   = new StubNoteRepository();

        var result = await BuildSut(entityRepo, noteRepo)
            .CreateNote(Guid.NewGuid(), new CreateNoteRequest { Content = "   " }, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, entityRepo.ExistsCallCount);
        Assert.Equal(0, entityRepo.GetByIdCallCount);
        Assert.Null(noteRepo.CreatedNote);
    }

    /// <summary>DeleteNote still 204s on success and still forwards the note id to the repository.</summary>
    [Fact]
    public async Task DeleteNote_WhenEntityExistsAndDeleteSucceeds_Returns204()
    {
        var noteRepo = new StubNoteRepository { DeleteResult = SoftDeleteResult.Success };

        var result = await BuildSut(new StubEntityRepository { Exists = true }, noteRepo)
            .DeleteNote(Guid.NewGuid(), 42L, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(42L, noteRepo.LastDeletedNoteId);
    }

    /// <summary>
    /// The two outcomes that are NOT about the entity still come from the note repository and keep
    /// their own status codes — a missing note is a 404 about the NOTE, with its own detail string,
    /// and must not be confused with the entity 404 this change touched.
    /// </summary>
    [Theory]
    [InlineData(SoftDeleteResult.NotFound,  404, "No se encontró la nota.")]
    [InlineData(SoftDeleteResult.Forbidden, 403, "Solo se pueden eliminar las notas propias.")]
    public async Task DeleteNote_WhenTheNoteItselfFails_KeepsItsOwnStatusAndDetail(
        SoftDeleteResult outcome, int expectedStatus, string expectedDetail)
    {
        var noteRepo = new StubNoteRepository { DeleteResult = outcome };

        var result = await BuildSut(new StubEntityRepository { Exists = true }, noteRepo)
            .DeleteNote(Guid.NewGuid(), 42L, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, obj.StatusCode);
        Assert.Equal(expectedDetail, Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail);
    }
}
