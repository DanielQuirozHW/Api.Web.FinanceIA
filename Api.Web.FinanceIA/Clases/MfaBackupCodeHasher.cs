// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Generates MFA backup codes and hashes/verifies them with PBKDF2-HMAC-SHA256.
/// Pure functions — no state, no I/O, no logging. The plaintext code never leaves this class
/// except as the return value of <see cref="GenerateCode"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why PBKDF2 and not a bare SHA-256.</b> A backup code is short enough to be typed by hand,
/// so it is far below the entropy of a session token: it needs a salted, deliberately slow KDF,
/// exactly like a password. PBKDF2 is used rather than an invented construction, and comes from
/// the BCL (<see cref="Rfc2898DeriveBytes"/>) rather than a new dependency.
/// </para>
/// <para>
/// <b>Why 100 000 iterations and not OWASP's 210 000.</b> Verification cost here is paid up to
/// <c>N</c> times per request, not once: the submitted code is compared against every unspent
/// hash the user holds (10 by default), because each row carries its own salt and there is no
/// way to know which row to test. 100 000 keeps a validation at roughly 300 ms; 210 000 would
/// put it near 700 ms and hand an attacker a cheap amplification lever. The tight per-user and
/// per-IP rate limits on the validate endpoint are the other half of that trade-off.
/// The iteration count is stored inside every hash, so raising it later does not orphan
/// existing rows — <see cref="Verify"/> reads each row's own count.
/// </para>
/// </remarks>
internal static class MfaBackupCodeHasher
{
    /// <summary>
    /// Alphabet used for generated codes: 32 characters, dropping the two pairs a human
    /// transcribes wrongly off a printed sheet — 0/O and 1/I.
    /// </summary>
    /// <remarks>
    /// L is deliberately KEPT, which is what makes the set exactly 32 = 2^5: each character
    /// carries exactly 5 bits, a code is exactly 40, and
    /// <see cref="RandomNumberGenerator.GetInt32(int)"/> draws without bias or rejection.
    /// Dropping L as well would buy little — codes are displayed upper-case, where L and 1 do
    /// not look alike — and would cost the clean power-of-two entropy story.
    /// </remarks>
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>Characters per code, excluding the display hyphen. 8 x 5 bits = 40 bits.</summary>
    private const int CodeLength = 8;

    /// <summary>Where the display hyphen is inserted: "ABCD-2345".</summary>
    private const int GroupLength = 4;

    private const string AlgorithmId = "pbkdf2-sha256";
    private const int    Iterations  = 100_000;
    private const int    SaltBytes   = 16;
    private const int    HashBytes   = 32;

    private static readonly HashAlgorithmName Prf = HashAlgorithmName.SHA256;

    /// <summary>
    /// Draws one cryptographically random code in display form ("ABCD-2345").
    /// </summary>
    public static string GenerateCode()
    {
        var buffer = new char[CodeLength];

        for (var i = 0; i < CodeLength; i++)
        {
            buffer[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var raw = new string(buffer);

        return string.Concat(raw[..GroupLength], "-", raw[GroupLength..]);
    }

    /// <summary>
    /// Hashes <paramref name="code"/> with a fresh random salt.
    /// The code is normalised first, so the stored hash does not depend on how the user typed it.
    /// </summary>
    /// <returns>
    /// "pbkdf2-sha256${iterations}${base64 salt}${base64 hash}" — about 85 characters,
    /// comfortably inside the column's 255.
    /// </returns>
    public static string Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(Normalize(code), salt, Iterations);

        return string.Join(
            '$',
            AlgorithmId,
            Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Verifies <paramref name="code"/> against a stored hash produced by <see cref="Hash"/>.
    /// Returns false — never throws — for a malformed or unrecognised stored value, so one
    /// corrupted row cannot fail the whole validation pass.
    /// </summary>
    public static bool Verify(string code, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');

        if (parts.Length != 4 || !string.Equals(parts[0], AlgorithmId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt     = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Derive(Normalize(code), salt, iterations, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Burns one PBKDF2 derivation against a throwaway salt and discards the result.
    /// Called when a user has no unspent codes at all, so that "no codes enrolled" cannot be
    /// told apart from "wrong code" by how long the response took.
    /// </summary>
    public static void BurnOneVerification()
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var derived = Derive("dummy", salt, Iterations);

        CryptographicOperations.ZeroMemory(derived);
    }

    /// <summary>
    /// Upper-cases and strips every character outside <see cref="Alphabet"/>, so the display
    /// hyphen, stray spaces and lower case all collapse to the same 8-character form.
    /// Hashing and verification both run on this, never on the raw input.
    /// </summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(code.Length);

        foreach (var character in code)
        {
            var upper = char.ToUpperInvariant(character);

            if (Alphabet.Contains(upper, StringComparison.Ordinal))
            {
                builder.Append(upper);
            }
        }

        return builder.ToString();
    }

    private static byte[] Derive(string code, byte[] salt, int iterations, int outputLength = HashBytes)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(code),
            salt,
            iterations,
            Prf,
            outputLength);
}
