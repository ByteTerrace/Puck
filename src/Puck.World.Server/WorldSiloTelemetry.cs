using System.Text.Json;

namespace Puck.World.Server;

/// <summary>The severity of a <see cref="WorldTelemetryRecord"/>, in the order and with the values of the .NET logging
/// levels a host translates from.</summary>
public enum WorldTelemetryLevel {
    /// <summary>The most detailed diagnostic messages.</summary>
    Trace = 0,
    /// <summary>Diagnostic messages for development.</summary>
    Debug = 1,
    /// <summary>The host's ordinary progress, including its narration.</summary>
    Information = 2,
    /// <summary>An unexpected event the host recovered from.</summary>
    Warning = 3,
    /// <summary>A failure of the current operation.</summary>
    Error = 4,
    /// <summary>A failure that ends the host.</summary>
    Critical = 5,
}

/// <summary>One host log line a telemetry sink receives: never simulation state.</summary>
/// <param name="Timestamp">The host clock's UTC instant the line was written.</param>
/// <param name="Level">The line's severity.</param>
/// <param name="Category">The writer's category, such as a logger name or <c>Puck.World.Silo.Narration</c>.</param>
/// <param name="Message">The formatted line.</param>
/// <param name="Exception">The exception the line reports, as text, or <see langword="null"/>.</param>
public sealed record WorldTelemetryRecord(DateTimeOffset Timestamp, WorldTelemetryLevel Level, string Category, string Message, string? Exception);

/// <summary>Where a host's telemetry goes: a provider-neutral sink an installed extension contributes. Writes come from
/// any thread; disposal sends what the sink still holds, within the sink's own bound.</summary>
public interface IWorldTelemetrySink : IDisposable {
    /// <summary>Writes one record without blocking on the network.</summary>
    /// <param name="record">The record.</param>
    void Write(WorldTelemetryRecord record);
}

/// <summary>A telemetry sink provider an installed extension contributes, selected by its key.</summary>
/// <param name="Type">The contribution key a silo document's <c>lifecycle.telemetry</c> selects.</param>
/// <param name="Create">Validates provider-owned settings and creates an owned sink without network I/O.</param>
public sealed record WorldSiloTelemetryProvider(string Type, Func<JsonElement, IWorldTelemetrySink> Create);

/// <summary>The built-in <c>file</c> telemetry sink: one JSON object per line appended to the file its single
/// <c>path</c> setting names, which a local rehearsal reads back. The file is opened for shared reading.</summary>
public sealed class WorldFileTelemetrySink : IWorldTelemetrySink {
    /// <summary>The contribution key that selects this sink.</summary>
    public const string Type = "file";

    private readonly Lock m_gate = new();
    private readonly StreamWriter m_writer;

    /// <summary>Gets the provider every world host registers under <see cref="Type"/>.</summary>
    public static WorldSiloTelemetryProvider Provider { get; } = new(
        Type,
        static settings => {
            var path = WorldExtensionSettings.OnlySetting(name: "path", settings: settings);

            if ((path.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: path.GetString())) {
                throw new ArgumentException(message: "file telemetry requires exactly one nonempty path setting.");
            }
            return new WorldFileTelemetrySink(path: path.GetString()!);
        }
    );

    /// <summary>Initializes a new instance of the <see cref="WorldFileTelemetrySink"/> class, appending to
    /// <paramref name="path"/>.</summary>
    /// <param name="path">The file the records are appended to; its directory is created.</param>
    public WorldFileTelemetrySink(string path) {
        var full = Path.GetFullPath(path: path);

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
        m_writer = new StreamWriter(stream: new FileStream(path: full, mode: FileMode.Append, access: FileAccess.Write, share: FileShare.ReadWrite)) { AutoFlush = true };
    }

    /// <inheritdoc/>
    public void Write(WorldTelemetryRecord record) {
        var line = JsonSerializer.Serialize(value: record);

        lock (m_gate) { m_writer.WriteLine(value: line); }
    }
    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) { m_writer.Dispose(); }
    }
}
