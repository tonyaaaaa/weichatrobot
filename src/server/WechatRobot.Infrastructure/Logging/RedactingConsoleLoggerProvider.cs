using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WechatRobot.Infrastructure.Logging;

[ProviderAlias("RedactingConsole")]
public sealed class RedactingConsoleLoggerProvider(TextWriter? writer = null)
    : ILoggerProvider, ISupportExternalScope
{
    private readonly TextWriter _writer = writer ?? Console.Out;
    private readonly object _sync = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new SafeLogger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception)
    {
        var properties = ReadProperties(state);
        var originalFormat = properties.Remove("{OriginalFormat}", out var template)
            ? Convert.ToString(template, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            : Convert.ToString(state, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var scopes = new List<object?>();
        _scopes.ForEachScope((scope, values) => values.Add(RedactScope(scope)), scopes);
        var payload = new Dictionary<string, object?>
        {
            ["timestampUtc"] = DateTimeOffset.UtcNow,
            ["level"] = level.ToString(),
            ["category"] = category,
            ["eventId"] = eventId.Id,
            ["messageTemplate"] = RedactionEnricher.RedactMessage(originalFormat),
            ["properties"] = properties,
            ["scopes"] = scopes,
            ["exception"] = exception is null ? null : RedactionEnricher.RedactMessage(exception.ToString())
        };
        lock (_sync)
        {
            _writer.WriteLine(JsonSerializer.Serialize(payload));
            _writer.Flush();
        }
    }

    private static Dictionary<string, object?> ReadProperties<TState>(TState state)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) return result;
        foreach (var pair in values)
        {
            result[pair.Key] = RedactionEnricher.RedactValue(
                pair.Key,
                Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static object? RedactScope(object? scope)
    {
        if (scope is IEnumerable<KeyValuePair<string, object?>> values)
            return values.ToDictionary(
                pair => pair.Key,
                pair => (object?)RedactionEnricher.RedactValue(
                    pair.Key,
                    Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)));
        if (scope is IEnumerable sequence and not string)
            return sequence.Cast<object?>().Select(value => RedactionEnricher.RedactMessage(Convert.ToString(value) ?? string.Empty)).ToArray();
        return RedactionEnricher.RedactMessage(Convert.ToString(scope) ?? string.Empty);
    }

    private sealed class SafeLogger(RedactingConsoleLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, state, exception);
        }
    }
}
