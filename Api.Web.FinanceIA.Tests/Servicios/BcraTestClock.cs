// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// A hand-advanced clock for <see cref="MemoryCache"/>, so a test can step past the Central de
/// Deudores failure window (60 s by default) instead of sleeping through it.
/// </summary>
internal sealed class BcraTestClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 19, 13, 53, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>A <see cref="MemoryCache"/> whose expirations read this clock.</summary>
    public MemoryCache CreateCache() => new(Options.Create(new MemoryCacheOptions { Clock = this }));
}
