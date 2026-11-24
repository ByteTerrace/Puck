using Microsoft.Extensions.Logging;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Writes the silo's logs and its narration to the telemetry sink the silo document selects
/// (<c>lifecycle.telemetry</c>), so a host that is deleted after it fails leaves its evidence where the deployment
/// keeps it. The logging framework reaches the sink as one <see cref="ILoggerProvider"/>; narration, the engine's
/// <c>[silo…]</c> lines written to standard error and output, reaches it through <see cref="Narrate"/>. Disposing the
/// provider disposes the sink, which sends what it still holds.</summary>
/// <param name="sink">The owned sink.</param>
/// <param name="clock">The host clock each record is stamped on.</param>
internal sealed class SiloTelemetry(IWorldTelemetrySink sink, TimeProvider clock) : ILoggerProvider {
    /// <summary>The category narration lines carry.</summary>
    public const string NarrationCategory = "Puck.World.Silo.Narration";

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Logger(owner: this, category: categoryName);
    /// <inheritdoc/>
    public void Dispose() => sink.Dispose();

    /// <summary>Writes one narration line at information level.</summary>
    /// <param name="line">The line, prefixed as the console shows it.</param>
    public void Narrate(string line) => Write(level: WorldTelemetryLevel.Information, category: NarrationCategory, message: line, exception: null);

    private void Write(WorldTelemetryLevel level, string category, string message, Exception? exception) {
        try {
            sink.Write(record: new(clock.GetUtcNow(), level, category, message, exception?.ToString()));
        } catch (Exception) {
            // A telemetry sink never fails the host it observes; the line is still on the process's own output.
        }
    }

    private sealed class Logger(SiloTelemetry owner, string category) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => (logLevel != LogLevel.None);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (logLevel == LogLevel.None) { return; }
            owner.Write(level: (WorldTelemetryLevel)logLevel, category: category, message: formatter(arg1: state, arg2: exception), exception: exception);
        }
    }
}
