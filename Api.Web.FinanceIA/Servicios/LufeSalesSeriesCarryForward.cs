// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Keeps a good stored LUFE monthly sales series when a refresh's indicadorespostbalance call FAILED.
/// Without it a transient failure of that one sub-call wiped the series from the snapshot, and with it
/// the §7.3a sales-drop signal of every later analysis, while the rest of the snapshot looked fresh.
/// <para>
/// Pure and static: no I/O, no clock, no logging (a snapshot is identifying data). It mutates the fresh
/// snapshot it is given and only when <see cref="LufeService"/> marked the sub-call as failed — a
/// genuine empty answer is unmarked and stays null, exactly as before.
/// </para>
/// </summary>
public static class LufeSalesSeriesCarryForward
{
    // The same reading every LufeDataJson consumer uses: the blob is written PascalCase with default
    // options, and read case-insensitively.
    private static readonly JsonSerializerOptions SnapshotReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// When <paramref name="fresh"/> is marked <see cref="LufeVentasComprasEstados.LookupFailed"/> and the
    /// stored snapshot holds a non-empty series, puts the stored series on <paramref name="fresh"/>, marks
    /// it <see cref="LufeVentasComprasEstados.CarriedForward"/> and dates it: the stored carry date when
    /// the stored series was itself carried (so a series carried twice keeps its ORIGINAL date), otherwise
    /// <paramref name="storedUpdatedAt"/>, when the stored snapshot was taken. With nothing usable stored
    /// the mark stays lookup_failed and the series null. Returns true when a series was carried.
    /// </summary>
    /// <param name="fresh">The snapshot just fetched, about to be persisted.</param>
    /// <param name="storedJson">The entity's current LufeDataJson, or null.</param>
    /// <param name="storedUpdatedAt">The entity's current LufeDataUpdatedAt, or null.</param>
    public static bool Apply(LufeData fresh, string? storedJson, DateTime? storedUpdatedAt)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        if (fresh.VentasComprasMensualEstado != LufeVentasComprasEstados.LookupFailed)
            return false;

        var stored = Read(storedJson);
        if (stored?.VentasComprasMensual is not { Count: > 0 } storedSeries)
        {
            fresh.VentasComprasMensual     = null;
            fresh.VentasComprasMensualAsOf = null;
            return false;
        }

        var asOf = stored.VentasComprasMensualEstado == LufeVentasComprasEstados.CarriedForward
                   && stored.VentasComprasMensualAsOf is not null
            ? stored.VentasComprasMensualAsOf
            : storedUpdatedAt;

        fresh.VentasComprasMensual       = storedSeries;
        fresh.VentasComprasMensualEstado = LufeVentasComprasEstados.CarriedForward;
        fresh.VentasComprasMensualAsOf   = asOf is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
        return true;
    }

    private static LufeData? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LufeData>(json, SnapshotReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
