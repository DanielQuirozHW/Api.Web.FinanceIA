// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// An <see cref="IScanOrientationDetector"/> that judges nothing and calls nothing: every page is
/// unknown, so the second pass renders it as it is — the behaviour before the image detector existed.
/// For the live harness's baseline and for tests; production registers
/// <see cref="ClaudeScanOrientationDetector"/>.
/// </summary>
public sealed class NullScanOrientationDetector : IScanOrientationDetector
{
    /// <summary>The shared instance (stateless).</summary>
    public static NullScanOrientationDetector Instance { get; } = new();

    /// <inheritdoc />
    public Task<ScanOrientationResult> DetectAsync(
        byte[]             pdfBytes,
        IReadOnlyList<int> pageIndexes,
        string             apiKey,
        CancellationToken  ct)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ScanOrientationResult.Unknown(pageIndexes.Count));
    }
}
