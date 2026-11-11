// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Microsoft.Extensions.Logging;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>Keeps every log entry (level, formatted message and the structured values) for assertions.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    internal sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, object?>();

        lock (_entries)
            _entries.Add(new Entry(logLevel, formatter(state, exception), values));
    }
}
