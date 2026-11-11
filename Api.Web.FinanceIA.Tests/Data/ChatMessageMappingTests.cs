// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Model-metadata tests locking <see cref="ChatMessage"/>'s Fluent mapping to the DDL in
/// docs/database/, following the <c>FiadorMappingTests</c> / <c>TenantBillingConfigMappingTests</c>
/// precedent. The focus is the three attachment columns: <c>AttachmentMarkdown</c> from script 055,
/// <c>AttachmentFileName</c> from 056 and <c>HadEphemeralAttachment</c> from 053.
///
/// <para>
/// Why this exists: the project is Database First, so nothing validates the mapping at build time. A
/// typo in a HasColumnName or a column type that drifts from the DDL compiles cleanly and only fails
/// in Azure, at runtime. On THIS table that failure is maximal — <c>ChatRepository</c> reads and
/// inserts the full entity with no projection, so every chat turn and the whole conversation-history
/// endpoint go down with "Invalid column name". These assertions are the only thing standing between
/// a one-character mapping slip and that outage.
/// </para>
///
/// <para>
/// The context is built on the SQL Server provider rather than the in-memory one the repository tests
/// use, because GetTableName/GetColumnName/column types are relational-only concepts — the in-memory
/// provider would report nothing and the assertions would be vacuous. Reading
/// <see cref="DbContext.Model"/> builds the model in process; it never opens a connection, so the
/// bogus connection string is never dialled and no physical table has to exist.
/// </para>
/// </summary>
public sealed class ChatMessageMappingTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IEntityType MessageType()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(ChatMessage))!;
    }

    private static IProperty Property(string name) => MessageType().FindProperty(name)!;

    // ── Table ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ChatMessage_IsMappedToDboChatMessages()
    {
        var entityType = MessageType();

        Assert.NotNull(entityType);
        Assert.Equal("ChatMessages", entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());
    }

    // ── The attachment columns ──────────────────────────────────────────────────

    [Fact]
    public void AttachmentMarkdown_IsMappedToItsColumn()
    {
        // Script 055. The name has to match the DDL exactly — EF names it in every SELECT and INSERT
        // against this table, so a typo takes the whole chat down rather than degrading one field.
        Assert.Equal("AttachmentMarkdown", Property(nameof(ChatMessage.AttachmentMarkdown)).GetColumnName());
    }

    [Fact]
    public void AttachmentMarkdown_IsNvarcharMax()
    {
        // A capped attachment is up to 200.000 characters plus a truncation note. Anything shorter
        // than nvarchar(max) would truncate silently on write or throw on a real workbook.
        Assert.Equal("nvarchar(max)", Property(nameof(ChatMessage.AttachmentMarkdown)).GetColumnType());
    }

    [Fact]
    public void AttachmentMarkdown_IsNullable_BecauseMostTurnsCarryNoAttachment()
    {
        // NULL is the honest "this turn carried no attachment", and it is what the overwhelming
        // majority of turns store. NOT NULL here would fail the INSERT of every ordinary turn.
        Assert.True(Property(nameof(ChatMessage.AttachmentMarkdown)).IsNullable);
    }

    [Fact]
    public void AttachmentMarkdown_HasNoStoreDefault()
    {
        // Script 055 declares no DEFAULT. Mapping one would make EF omit the column on an INSERT
        // whose value it considered default, which for a reference type is exactly the null case.
        Assert.Null(Property(nameof(ChatMessage.AttachmentMarkdown)).GetDefaultValueSql());
    }

    [Fact]
    public void AttachmentFileName_IsMappedToItsColumn()
    {
        // Script 056. Same stakes as the column above: EF names it in every SELECT and INSERT against
        // this table, so a typo here takes the whole chat down rather than losing one label.
        Assert.Equal("AttachmentFileName", Property(nameof(ChatMessage.AttachmentFileName)).GetColumnName());
    }

    [Fact]
    public void AttachmentFileName_IsBoundedAt255()
    {
        // Matches the DDL's NVARCHAR(255). A longer max here would let EF send a value SQL Server
        // then rejects on INSERT — on a turn whose question the analyst has already asked. The
        // controller trims to the same number, and that number has to be this one.
        Assert.Equal(255, Property(nameof(ChatMessage.AttachmentFileName)).GetMaxLength());
    }

    [Fact]
    public void AttachmentFileName_IsNullable_BecauseMostTurnsCarryNoAttachment()
    {
        // NULL is the honest "no attachment", exactly as for the Markdown it labels — and it is what
        // every turn taken before script 056 stores. NOT NULL would fail every ordinary INSERT.
        Assert.True(Property(nameof(ChatMessage.AttachmentFileName)).IsNullable);
    }

    [Fact]
    public void AttachmentFileName_HasNoStoreDefault()
    {
        // Script 056 declares no DEFAULT, like 055 and unlike 053's flag. Mapping one would make EF
        // omit the column on an INSERT whose value it considered default — the null case here.
        Assert.Null(Property(nameof(ChatMessage.AttachmentFileName)).GetDefaultValueSql());
    }

    [Fact]
    public void HadEphemeralAttachment_IsStillMappedAndNotNull()
    {
        // Script 053, re-asserted rather than rewritten: 055 sits beside this column and must not
        // disturb it. The billing marker stays NOT NULL on the assistant turn while the Markdown is
        // nullable on the user turn — two columns, two turns, two questions.
        var flag = Property(nameof(ChatMessage.HadEphemeralAttachment));

        Assert.Equal("HadEphemeralAttachment", flag.GetColumnName());
        Assert.False(flag.IsNullable);
    }

    // ── The column the attachment must never become ─────────────────────────────

    [Fact]
    public void Content_IsStillRequired_AndSeparateFromTheAttachment()
    {
        // Content is what history is rebuilt from. The attachment lives in its own column precisely
        // so this one keeps holding the analyst's typed sentence and nothing else.
        var content = Property(nameof(ChatMessage.Content));

        Assert.Equal("Content", content.GetColumnName());
        Assert.Equal("nvarchar(max)", content.GetColumnType());
        Assert.False(content.IsNullable);
    }
}
