// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-16

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// <see cref="AnalysisApprovalVoteRepository"/> over the EF Core in-memory provider — the store of
/// the per-manager signatures a credit decision rests on.
/// </summary>
/// <remarks>
/// TENANT SCOPING IS THE POINT OF MOST OF THESE. [dbo].[Analyses].AnalysisId is globally unique, so
/// every query here would happily reach another tenant's analysis if its predicate were dropped, and
/// <see cref="FinanciaDbContext"/> has no global query filter to catch it.
///
/// <para>
/// NOT COVERED HERE: the unique-constraint branch of <see cref="AnalysisApprovalVoteRepository.RecordAsync"/>.
/// The in-memory provider does not enforce unique indexes, so the duplicate INSERT never throws and
/// the catch cannot be reached — the same limitation <c>FiadorRepositoryTests</c> and
/// <c>CreditLineOperationRepositoryTests</c> record for their own <c>ExecuteUpdateAsync</c> paths.
/// What IS reachable, and asserted, is the pre-read that answers the ordinary case: the same manager
/// voting twice. The catch behind it exists for the race two requests can still lose, which only a
/// real SQL Server can demonstrate.
/// </para>
/// </remarks>
public sealed class AnalysisApprovalVoteRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static readonly Guid AnalysisId      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherAnalysisId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ManagerA        = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ManagerB        = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static FinanciaDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new FinanciaDbContext(options);
    }

    private static AnalysisApprovalVoteRepository Sut(FinanciaDbContext ctx)
        => new(ctx, NullLogger<AnalysisApprovalVoteRepository>.Instance);

    private static AnalysisApprovalVote Vote(
        Guid userId, string vote = AnalysisVoteValues.Approved,
        Guid? analysisId = null, string tenant = Tenant)
        => new()
        {
            VoteId     = Guid.NewGuid(),
            AnalysisId = analysisId ?? AnalysisId,
            UserId     = userId,
            TenantId   = tenant,
            Vote       = vote,
            VotedAt    = DateTime.UtcNow,
        };

    [Fact]
    public async Task Record_StoresOneVote_AndReportsIt()
    {
        await using var ctx = NewContext(nameof(Record_StoresOneVote_AndReportsIt));
        var sut = Sut(ctx);

        Assert.True(await sut.RecordAsync(Vote(ManagerA), CancellationToken.None));

        Assert.Equal([ManagerA], await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Record_IsIdempotentPerManagerAndAnalysis()
    {
        // One manager, one vote. The second call is an ordinary outcome — a double click, a retry —
        // not an error, and it must not become a second signature.
        await using var ctx = NewContext(nameof(Record_IsIdempotentPerManagerAndAnalysis));
        var sut = Sut(ctx);

        Assert.True(await sut.RecordAsync(Vote(ManagerA), CancellationToken.None));
        Assert.False(await sut.RecordAsync(Vote(ManagerA), CancellationToken.None));

        Assert.Single(await ctx.AnalysisApprovalVotes.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Record_DoesNotCollideAcrossAnalysesOrManagers()
    {
        // The uniqueness is on the PAIR. One manager voting on two analyses, and two managers voting
        // on one, are both perfectly ordinary.
        await using var ctx = NewContext(nameof(Record_DoesNotCollideAcrossAnalysesOrManagers));
        var sut = Sut(ctx);

        Assert.True(await sut.RecordAsync(Vote(ManagerA), CancellationToken.None));
        Assert.True(await sut.RecordAsync(Vote(ManagerB), CancellationToken.None));
        Assert.True(await sut.RecordAsync(Vote(ManagerA, analysisId: OtherAnalysisId), CancellationToken.None));

        Assert.Equal(2, (await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None)).Count);
        Assert.Single(await sut.GetApproverUserIdsAsync(OtherAnalysisId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Record_IsIdempotentPerTenant_NotGlobally()
    {
        // Two tenants cannot share an AnalysisId in practice, but the predicate has to be there:
        // without the tenant clause the pre-read would report a foreign tenant's vote as this one's
        // and silently swallow a legitimate signature.
        await using var ctx = NewContext(nameof(Record_IsIdempotentPerTenant_NotGlobally));
        var sut = Sut(ctx);

        Assert.True(await sut.RecordAsync(Vote(ManagerA, tenant: Other), CancellationToken.None));
        Assert.True(await sut.RecordAsync(Vote(ManagerA), CancellationToken.None));

        Assert.Single(await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None));
        Assert.Single(await sut.GetApproverUserIdsAsync(AnalysisId, Other, CancellationToken.None));
    }

    [Fact]
    public async Task GetApproverUserIds_ExcludesRejections()
    {
        // A veto is recorded in the same table. Counting it as an approval would confirm an analysis
        // on the strength of somebody refusing it.
        await using var ctx = NewContext(nameof(GetApproverUserIds_ExcludesRejections));
        var sut = Sut(ctx);

        await sut.RecordAsync(Vote(ManagerA), CancellationToken.None);
        await sut.RecordAsync(Vote(ManagerB, AnalysisVoteValues.Rejected), CancellationToken.None);

        Assert.Equal([ManagerA], await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetApproverUserIds_IsTenantScoped()
    {
        await using var ctx = NewContext(nameof(GetApproverUserIds_IsTenantScoped));
        var sut = Sut(ctx);

        await sut.RecordAsync(Vote(ManagerA, tenant: Other), CancellationToken.None);

        Assert.Empty(await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Withdraw_RemovesOnlyThatManagersOwnVote()
    {
        // The rule the endpoint rests on. If this reached further, one manager could erase another's
        // signature and quietly lower the bar on a credit decision.
        await using var ctx = NewContext(nameof(Withdraw_RemovesOnlyThatManagersOwnVote));
        var sut = Sut(ctx);

        await sut.RecordAsync(Vote(ManagerA), CancellationToken.None);
        await sut.RecordAsync(Vote(ManagerB), CancellationToken.None);

        Assert.True(await sut.WithdrawAsync(AnalysisId, ManagerA, Tenant, CancellationToken.None));

        Assert.Equal([ManagerB], await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Withdraw_IsTenantScoped()
    {
        await using var ctx = NewContext(nameof(Withdraw_IsTenantScoped));
        var sut = Sut(ctx);

        await sut.RecordAsync(Vote(ManagerA, tenant: Other), CancellationToken.None);

        Assert.False(await sut.WithdrawAsync(AnalysisId, ManagerA, Tenant, CancellationToken.None));
        Assert.Single(await sut.GetApproverUserIdsAsync(AnalysisId, Other, CancellationToken.None));
    }

    [Fact]
    public async Task Withdraw_WithNothingOnRecord_ReportsFalse()
    {
        await using var ctx = NewContext(nameof(Withdraw_WithNothingOnRecord_ReportsFalse));

        Assert.False(await Sut(ctx).WithdrawAsync(AnalysisId, ManagerA, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteByAnalysis_RemovesEveryVoteOnThatAnalysisOnly()
    {
        // Returning an analysis for rework resets ITS tally. Reaching another analysis would discard
        // signatures that were never in question.
        await using var ctx = NewContext(nameof(DeleteByAnalysis_RemovesEveryVoteOnThatAnalysisOnly));
        var sut = Sut(ctx);

        await sut.RecordAsync(Vote(ManagerA), CancellationToken.None);
        await sut.RecordAsync(Vote(ManagerB, AnalysisVoteValues.Rejected), CancellationToken.None);
        await sut.RecordAsync(Vote(ManagerA, analysisId: OtherAnalysisId), CancellationToken.None);

        // Rejections go too: the reset is of the whole record, not only the approvals.
        Assert.Equal(2, await sut.DeleteByAnalysisAsync(AnalysisId, Tenant, CancellationToken.None));

        Assert.Empty(await sut.GetApproverUserIdsAsync(AnalysisId, Tenant, CancellationToken.None));
        Assert.Single(await sut.GetApproverUserIdsAsync(OtherAnalysisId, Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteByAnalysis_IsTenantScoped()
    {
        await using var ctx = NewContext(nameof(DeleteByAnalysis_IsTenantScoped));
        var sut = Sut(ctx);

        await sut.RecordAsync(Vote(ManagerA, tenant: Other), CancellationToken.None);

        Assert.Equal(0, await sut.DeleteByAnalysisAsync(AnalysisId, Tenant, CancellationToken.None));
        Assert.Single(await sut.GetApproverUserIdsAsync(AnalysisId, Other, CancellationToken.None));
    }

    [Fact]
    public async Task Record_RoundTripsEveryMappedColumn()
    {
        // The mapping itself: six columns, and a Vote value stored verbatim. DDL 067 leaves the
        // vocabulary to the API, so nothing in the database would catch a mangled one.
        await using var ctx = NewContext(nameof(Record_RoundTripsEveryMappedColumn));
        var votedAt = new DateTime(2026, 9, 16, 12, 30, 45, DateTimeKind.Utc);
        var vote = Vote(ManagerA);
        vote.VotedAt = votedAt;

        await Sut(ctx).RecordAsync(vote, CancellationToken.None);

        var stored = Assert.Single(await ctx.AnalysisApprovalVotes.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Equal(vote.VoteId, stored.VoteId);
        Assert.Equal(AnalysisId, stored.AnalysisId);
        Assert.Equal(ManagerA, stored.UserId);
        Assert.Equal(Tenant, stored.TenantId);
        Assert.Equal(AnalysisVoteValues.Approved, stored.Vote);
        Assert.Equal(votedAt, stored.VotedAt);
    }
}
