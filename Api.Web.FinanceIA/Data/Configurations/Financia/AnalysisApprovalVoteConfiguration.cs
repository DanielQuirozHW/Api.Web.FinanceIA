// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AnalysisApprovalVote"/>.
/// Maps [dbo].[AnalysisApprovalVotes] (DDL 067).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
/// <remarks>
/// This configuration is what brings DDL 067 under the <c>--verify-schema</c> deploy gate: the gate
/// reads the EF MODEL, so a table nothing maps is invisible to it. From this release on, a tenant
/// database that never ran 067 fails the gate instead of failing at runtime with
/// "Invalid object name 'AnalysisApprovalVotes'".
/// </remarks>
public sealed class AnalysisApprovalVoteConfiguration : IEntityTypeConfiguration<AnalysisApprovalVote>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AnalysisApprovalVote> builder)
    {
        builder.ToTable("AnalysisApprovalVotes", "dbo");

        builder.HasKey(x => x.VoteId);

        // The application supplies the Guid on insert, mirroring Team and Notification. Do NOT mark
        // it ValueGeneratedOnAdd: that tells EF the DB generates the key and makes EF omit the
        // explicit Guid, which breaks the INSERT silently. (The column does carry a
        // NEWSEQUENTIALID() default in the DDL, but that is there for rows written by hand.)
        builder.Property(x => x.VoteId)
            .HasColumnName("VoteId")
            .ValueGeneratedNever();

        // [dbo].[Analyses].AnalysisId — NOT a ResultId. See the remarks on the entity.
        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.UserId)
            .HasColumnName("UserId")
            .IsRequired();

        // nvarchar(100), NOT uniqueidentifier — TenantId is a Firebase slug everywhere in the
        // business database since DDL 005. Matches Analyses.TenantId and Users.TenantId exactly.
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        // nvarchar(20), matching the column. The vocabulary is validated in the API rather than by a
        // CHECK constraint — see AnalysisVoteValues and DDL 067's header for why.
        builder.Property(x => x.Vote)
            .HasColumnName("Vote")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.VotedAt)
            .HasColumnName("VotedAt")
            .IsRequired();

        // ONE MANAGER, ONE VOTE PER ANALYSIS — the business rule, declared structurally. The name
        // matches UQ_AnalysisApprovalVotes_Analysis_User in DDL 067 exactly, because the repository
        // catches that name to tell a duplicate vote apart from any other write failure.
        //
        // It leads with AnalysisId, so it also serves the only read the tally needs ("every vote on
        // this analysis"). Hence no second index, the same reasoning TeamManagerConfiguration gives.
        //
        // No HasOne/WithMany. The foreign keys to Analyses and Users exist in the database
        // (FK_AnalysisApprovalVotes_Analysis, FK_AnalysisApprovalVotes_User) but this context models
        // no relationships at all.
        builder.HasIndex(x => new { x.AnalysisId, x.UserId })
            .IsUnique()
            .HasDatabaseName("UQ_AnalysisApprovalVotes_Analysis_User");
    }
}
