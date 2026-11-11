// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Builds a <see cref="BcraService"/> for the BCRA test suites, so the Central de Deudores gate and
/// options added on 2026-10-19 are wired in one place rather than in every file's BuildSut. Defaults
/// are the production defaults (<see cref="BcraOptions"/>) and a fresh gate per SUT.
/// </summary>
internal static class BcraServiceTestFactory
{
    public static BcraService Create(
        IHttpClientFactory         httpClientFactory,
        IMemoryCache?              cache   = null,
        BcraOptions?               options = null,
        IBcraCentralDeudoresGate?  gate    = null,
        ILogger<BcraService>?      logger  = null)
    {
        var opts = Options.Create(options ?? new BcraOptions());

        return new BcraService(
            httpClientFactory,
            cache ?? new MemoryCache(Options.Create(new MemoryCacheOptions())),
            gate ?? new BcraCentralDeudoresGate(opts),
            opts,
            logger ?? NullLogger<BcraService>.Instance);
    }
}
