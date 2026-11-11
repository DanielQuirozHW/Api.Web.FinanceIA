// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Opt-in LOCAL harness, never a CI test: runs <see cref="PdfAttachmentExtractor.Unwrap"/> over a
/// folder of REAL legalization wrappers (CPCE obleas, encrypted or not) and prints, per file, what
/// the unwrap saw and chose. No model call, no cost.
///
/// <para>
/// Gated by <see cref="EnvironmentFactAttribute"/> on FINANCIA_OBLEA_CASES_DIR, a local folder of
/// <c>*.pdf</c>: unset, it reports as skipped and reads nothing.
/// </para>
///
/// <para>
/// ⚠ The PDFs are clients' documents: the folder must be OUTSIDE the repository (enforced), and real
/// PDFs are never committed — the committed fixtures under Fixtures/EncryptedOblea are synthetic.
/// The report names each file by its INDEX in sorted order, never by file name, and carries counts,
/// encryption parameters and exception TYPES only — never a figure, a name, a path or content.
/// </para>
///
/// <para>Per file it prints:</para>
/// <list type="bullet">
/// <item>whether the outer PDF is encrypted, with /V, /R and the stream crypt method when readable;</item>
/// <item>whether PdfPig's own <c>TryGetEmbeddedFiles</c> — the path the extractor used before — sees
/// readable name-tree keys (the root cause of the CPCE miss: it reads a pre-decryption catalog);</item>
/// <item>attachments seen, whether one was selected, its page count and whether it is itself
/// encrypted;</item>
/// <item><c>AttachmentUnread</c> and the failure type.</item>
/// </list>
/// </summary>
public sealed class EncryptedObleaHarnessTests
{
    private const string DirVariable      = "FINANCIA_OBLEA_CASES_DIR";
    private const string SolutionFileName = "Api.Web.ServiciosFinancieros.slnx";

    private readonly ITestOutputHelper _output;

    public EncryptedObleaHarnessTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(DirVariable)]
    public void Unwrap_OnRealWrappers_ReportsWhatWasSelected()
    {
        var inputDir = Path.GetFullPath(Environment.GetEnvironmentVariable(DirVariable)!);
        if (!Directory.Exists(inputDir))
            throw new InvalidOperationException($"{DirVariable} does not point to an existing folder.");

        GuardOutsideRepository(inputDir);

        var files = Directory.GetFiles(inputDir, "*.pdf", SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.Ordinal)
                             .ToList();
        Assert.NotEmpty(files);

        _output.WriteLine($"{files.Count} file(s). Columns: #, outer encryption, old-path keys readable, " +
                          "seen, selected, selected pages, selected encrypted, unread, failure type");

        for (var i = 0; i < files.Count; i++)
        {
            var outer = File.ReadAllBytes(files[i]);

            var result = PdfAttachmentExtractor.Unwrap(
                outer, NullLogger<EncryptedObleaHarnessTests>.Instance, Guid.Empty, "harness");

            var selected = !ReferenceEquals(result.Bytes, outer);
            var (pages, innerEncrypted) = selected ? Describe(result.Bytes) : ("-", "-");

            _output.WriteLine(
                $"#{i + 1}: outer={OuterEncryption(outer)}, oldPathKeysReadable={OldPathKeysReadable(outer)}, " +
                $"seen={result.AttachmentsSeen}, selected={selected}, pages={pages}, encrypted={innerEncrypted}, " +
                $"unread={result.AttachmentUnread}, failure={result.FailureType ?? "-"}");
        }
    }

    private static string OuterEncryption(byte[] bytes)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            if (!doc.IsEncrypted)
                return "none";

            var scanner = doc.Structure.TokenScanner;
            var token   = doc.Structure.Trailer.EncryptionToken;
            var encrypt = token is IndirectReferenceToken reference
                ? scanner.Get(reference.Data)?.Data as DictionaryToken
                : token as DictionaryToken;

            if (encrypt is null)
                return "encrypted (dictionary unreadable)";

            var v   = encrypt.TryGet<NumericToken>(NameToken.Create("V"), scanner, out var vToken) ? vToken.Int.ToString() : "?";
            var r   = encrypt.TryGet<NumericToken>(NameToken.Create("R"), scanner, out var rToken) ? rToken.Int.ToString() : "?";
            var cfm = "-";
            if (encrypt.TryGet<DictionaryToken>(NameToken.Create("CF"), scanner, out var cf)
                && cf.TryGet<DictionaryToken>(NameToken.Create("StdCF"), scanner, out var std)
                && std.TryGet<NameToken>(NameToken.Create("CFM"), scanner, out var method))
            {
                cfm = method.Data;
            }

            return $"V{v}/R{r}/{cfm}";
        }
        catch (Exception ex)
        {
            return $"open failed ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Whether PdfPig's TryGetEmbeddedFiles, the extractor's previous path, returns printable keys.
    /// "n/a" when it finds nothing.
    /// </summary>
    private static string OldPathKeysReadable(byte[] bytes)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            if (!doc.Advanced.TryGetEmbeddedFiles(out var files) || files is null || files.Count == 0)
                return "n/a";

            return files.All(f => f.Name.All(c => c >= ' ' && c != '\u007f' && c < 'Ā' && !char.IsControl(c))
                                  && f.Name.Length > 0)
                ? "yes"
                : "no";
        }
        catch (Exception ex)
        {
            return $"threw ({ex.GetType().Name})";
        }
    }

    private static (string Pages, string Encrypted) Describe(byte[] bytes)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            return (doc.NumberOfPages.ToString(), doc.IsEncrypted ? "yes" : "no");
        }
        catch (Exception ex)
        {
            return ($"open failed ({ex.GetType().Name})", "?");
        }
    }

    private static void GuardOutsideRepository(string path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            dir = dir.Parent;

        if (dir is null)
            return;

        var root       = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate  = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (candidate.StartsWith(root, comparison))
            throw new InvalidOperationException($"{DirVariable} is inside the repository. Clients' documents must stay outside it.");
    }
}
