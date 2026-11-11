// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The legajo pipeline forces its tool at temperature 0 on every request, so a model that rejects
/// either must never be sent: <see cref="LegajoModelSupport"/> falls back to the service's default
/// and logs an Error once.
/// </summary>
public sealed class LegajoModelGuardTests
{
    [Theory]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4-6")]
    [InlineData(" claude-haiku-4-5 ", "claude-haiku-4-5")]
    [InlineData(null, LegajoExtractionOptions.DefaultModel)]
    [InlineData("", LegajoExtractionOptions.DefaultModel)]
    public void Extraction_supported_or_blank_model_passes_through_silently(string? configured, string expected)
    {
        var logger = new RecordingLogger<LegajoExtractionService>();

        var sut = BuildExtraction(configured, logger);

        Assert.Equal(expected, sut.Model);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-4-8")]
    public void Extraction_model_that_rejects_forced_tool_use_or_temperature_falls_back_with_one_error(string model)
    {
        // A unique suffix keeps the process-wide once-gate from being consumed by another test; the
        // bare id is checked through the static resolver below.
        var configured = $"{model}-{Guid.NewGuid():N}";
        var logger     = new RecordingLogger<LegajoExtractionService>();

        var first  = BuildExtraction(configured, logger);
        var second = BuildExtraction(configured, logger);

        Assert.Equal(LegajoExtractionOptions.DefaultModel, first.Model);
        Assert.Equal(LegajoExtractionOptions.DefaultModel, second.Model);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(LegajoModelSupport.ExtractionModelSetting, entry.Message);
        Assert.False(LegajoModelSupport.SupportsForcedToolUse(model));
    }

    [Fact]
    public void Classification_sonnet55_falls_back_to_haiku_with_one_error()
    {
        var configured = $"claude-sonnet-5-5-{Guid.NewGuid():N}";
        var logger     = new RecordingLogger<LegajoClassificationService>();

        var first  = BuildClassification(configured, logger);
        var second = BuildClassification(configured, logger);

        Assert.Equal(LegajoClassificationOptions.DefaultModel, first.Model);
        Assert.Equal(LegajoClassificationOptions.DefaultModel, second.Model);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(LegajoModelSupport.ClassificationModelSetting, entry.Message);
    }

    [Fact]
    public void Classification_supported_model_passes_through()
    {
        var logger = new RecordingLogger<LegajoClassificationService>();

        Assert.Equal("claude-sonnet-4-6", BuildClassification("claude-sonnet-4-6", logger).Model);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("CLAUDE-SONNET-4-6", true)]
    [InlineData("claude-haiku-4-5", true)]
    [InlineData(null, false)]
    public void SupportsForcedToolUse_is_an_allow_list(string? model, bool expected)
    {
        Assert.Equal(expected, LegajoModelSupport.SupportsForcedToolUse(model));
    }

    [Fact]
    public void Both_defaults_are_on_the_allow_list()
    {
        Assert.True(LegajoModelSupport.SupportsForcedToolUse(LegajoExtractionOptions.DefaultModel));
        Assert.True(LegajoModelSupport.SupportsForcedToolUse(LegajoClassificationOptions.DefaultModel));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LegajoExtractionService BuildExtraction(string? model, ILogger<LegajoExtractionService> logger) =>
        new(LegajoTestClients.Messages(new ScriptedHandler((System.Net.HttpStatusCode.OK, "{}"))),
            new StubPageRenderer(),
            Options.Create(new LegajoExtractionOptions { ApiKey = "test-key", LegajoExtractionModel = model! }),
            Options.Create(new LegajoOptions()),
            logger);

    private static LegajoClassificationService BuildClassification(string? model, ILogger<LegajoClassificationService> logger) =>
        new(LegajoTestClients.Messages(new ScriptedHandler((System.Net.HttpStatusCode.OK, "{}"))),
            new StubPageRenderer(),
            Options.Create(new LegajoClassificationOptions { ApiKey = "test-key", LegajoClassificationModel = model! }),
            logger);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
