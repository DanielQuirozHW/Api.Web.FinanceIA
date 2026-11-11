// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Pins the crypto contract of <see cref="MfaBackupCodeHasher"/>.
///
/// <para>
/// The properties tested here are the ones whose absence would be invisible in a passing feature
/// test: a hash that is really the plaintext, a salt that repeats across rows, and a stored format
/// that cannot be verified after the iteration count is raised. All three would still let a user
/// generate codes and log in with them.
/// </para>
/// </summary>
public sealed class MfaBackupCodeHasherTests
{
    // ── Generation ──────────────────────────────────────────────────────────────

    [Fact]
    public void GenerateCode_ReturnsEightCharactersInTwoHyphenatedGroups()
    {
        var code = MfaBackupCodeHasher.GenerateCode();

        Assert.Equal(9, code.Length);
        Assert.Equal('-', code[4]);
        Assert.Equal(8, MfaBackupCodeHasher.Normalize(code).Length);
    }

    [Fact]
    public void GenerateCode_UsesOnlyTheUnambiguousAlphabet()
    {
        // 0/O and 1/I are the pairs a human mistypes off a printed sheet. Neither may appear.
        // L is kept on purpose — it is what makes the set exactly 32 = 2^5 (see the hasher).
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        for (var i = 0; i < 200; i++)
        {
            var code = MfaBackupCodeHasher.GenerateCode().Replace("-", string.Empty);

            Assert.All(code, character => Assert.True(
                alphabet.Contains(character, StringComparison.Ordinal),
                $"'{character}' is outside the unambiguous alphabet."));
        }
    }

    [Fact]
    public void GenerateCode_DoesNotRepeatItselfAcrossDraws()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 200; i++)
        {
            codes.Add(MfaBackupCodeHasher.GenerateCode());
        }

        // 40 bits per code: a collision in 200 draws would mean the generator is not random.
        Assert.Equal(200, codes.Count);
    }

    // ── Hashing ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Hash_NeverContainsThePlaintext()
    {
        var code = MfaBackupCodeHasher.GenerateCode();

        var hash = MfaBackupCodeHasher.Hash(code);

        Assert.DoesNotContain(MfaBackupCodeHasher.Normalize(code), hash, StringComparison.Ordinal);
        Assert.DoesNotContain(code, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_SaltsEveryCallSeparately()
    {
        const string code = "ABCD-2345";

        var first  = MfaBackupCodeHasher.Hash(code);
        var second = MfaBackupCodeHasher.Hash(code);

        // Same input, different stored value: without a per-row salt an attacker who cracked one
        // row would have cracked every row holding the same code.
        Assert.NotEqual(first, second);
        Assert.True(MfaBackupCodeHasher.Verify(code, first));
        Assert.True(MfaBackupCodeHasher.Verify(code, second));
    }

    [Fact]
    public void Hash_ProducesTheDocumentedSelfDescribingFormat()
    {
        var hash = MfaBackupCodeHasher.Hash("ABCD-2345");

        var parts = hash.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal("100000", parts[1]);
        Assert.NotEmpty(Convert.FromBase64String(parts[2]));
        Assert.NotEmpty(Convert.FromBase64String(parts[3]));

        // Must fit [CodeHash] nvarchar(255).
        Assert.True(hash.Length <= 255, $"Hash length {hash.Length} exceeds the column's 255.");
    }

    // ── Verification ────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_AcceptsTheCorrectCodeAndRejectsAnother()
    {
        var hash = MfaBackupCodeHasher.Hash("ABCD-2345");

        Assert.True(MfaBackupCodeHasher.Verify("ABCD-2345", hash));
        Assert.False(MfaBackupCodeHasher.Verify("ABCD-2346", hash));
    }

    [Theory]
    [InlineData("abcd-2345")]
    [InlineData("ABCD2345")]
    [InlineData("  abcd 2345 ")]
    [InlineData("abcd--2345")]
    public void Verify_IgnoresCaseHyphenAndWhitespace(string typed)
    {
        var hash = MfaBackupCodeHasher.Hash("ABCD-2345");

        // The user reads the code off a sheet and types it however they like.
        Assert.True(MfaBackupCodeHasher.Verify(typed, hash));
    }

    [Fact]
    public void Verify_HonoursTheIterationCountStoredInEachHash()
    {
        // The count lives inside the hash so it can be raised later without orphaning old rows.
        // Rewriting it to a different number must therefore change the derived key.
        var hash  = MfaBackupCodeHasher.Hash("ABCD-2345");
        var parts = hash.Split('$');
        var retuned = string.Join('$', parts[0], "50000", parts[2], parts[3]);

        Assert.True(MfaBackupCodeHasher.Verify("ABCD-2345", hash));
        Assert.False(MfaBackupCodeHasher.Verify("ABCD-2345", retuned));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$100000$onlythreeparts")]
    [InlineData("bcrypt$100000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$0$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$100000$!!!notbase64!!!$aGFzaA==")]
    public void Verify_ReturnsFalseForAMalformedStoredValueInsteadOfThrowing(string storedHash)
    {
        // One corrupted row must not throw and abort the pass over the user's other codes.
        Assert.False(MfaBackupCodeHasher.Verify("ABCD-2345", storedHash));
    }

    [Fact]
    public void Verify_RejectsAnEmptySubmission()
    {
        var hash = MfaBackupCodeHasher.Hash("ABCD-2345");

        Assert.False(MfaBackupCodeHasher.Verify(string.Empty, hash));
        Assert.False(MfaBackupCodeHasher.Verify("----", hash));
    }

    // ── Normalisation ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_StripsEverythingOutsideTheAlphabet()
    {
        Assert.Equal("ABCD2345", MfaBackupCodeHasher.Normalize(" abcd-2345 "));
        Assert.Equal(string.Empty, MfaBackupCodeHasher.Normalize(null));
        Assert.Equal(string.Empty, MfaBackupCodeHasher.Normalize("  "));

        // 0, 1, I and O are outside the alphabet and vanish; L is inside it and survives,
        // upper-cased. This is the line that pins WHICH characters were excluded.
        Assert.Equal("L", MfaBackupCodeHasher.Normalize("01ilo"));
        Assert.Equal(string.Empty, MfaBackupCodeHasher.Normalize("01io"));
    }
}
