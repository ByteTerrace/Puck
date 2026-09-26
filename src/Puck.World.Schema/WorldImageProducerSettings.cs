using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>The settings of the <c>testPattern</c> producer: the deterministic animated test pattern, rendered from the
/// world's simulation tick (never the wall clock) into a CPU buffer and uploaded each frame.</summary>
/// <param name="Width">The pattern framebuffer width in pixels, 1 to <see cref="WorldDefinitionValidator.MaxSurfaceDimension"/>.</param>
/// <param name="Height">The pattern framebuffer height in pixels, 1 to <see cref="WorldDefinitionValidator.MaxSurfaceDimension"/>.</param>
public sealed record WorldTestPatternSettings(int Width, int Height);
/// <summary>The settings of the <c>qr</c> producer: an authorable QR code (ISO/IEC 18004). The document names a payload
/// string and the engine derives the scannable module grid (<see cref="Puck.Assets.Qr.QrEncoder"/>), rendered CPU-side
/// into a static B8G8R8A8 framebuffer and uploaded once, never re-derived from the tick. <c>screen.source &lt;index&gt;
/// qr</c> is the live-authoring twin, and <c>world.identify</c> is the one caller that mints its payload (the running
/// world's own document id and content-address pin) rather than being handed one.</summary>
/// <param name="Payload">The encoded string, UTF-8 byte mode. It must fit version
/// <see cref="Puck.Assets.Qr.QrEncoder.MaxSupportedVersion"/> at <paramref name="EcLevel"/>; validation refuses an
/// oversized payload by name (its byte count against the level's capacity) and never truncates it.</param>
/// <param name="EcLevel">The error-correction level: <c>L</c>, <c>M</c>, <c>Q</c>, or <c>H</c> (case-insensitive,
/// parsed by <see cref="Puck.Assets.Qr.QrErrorCorrection.TryParse"/>). Defaults to <c>M</c>.</param>
/// <param name="QuietZoneModules">The white quiet-zone border width in modules on every side. ISO/IEC 18004 recommends
/// at least 4, and a borderless code does not scan; validation refuses only a negative width, since a screen's framing
/// sometimes supplies the margin itself.</param>
public sealed record WorldQrSettings(string Payload, string EcLevel = "M", int QuietZoneModules = 4);
/// <summary>The settings of the <c>camera</c> producer: the platform's live camera feed. The platform may negotiate a
/// nearby extent; every screen and probe socket naming the same sensor of the same seat shares one feed, opened at the
/// richest profile any consumer requests.</summary>
/// <param name="Profile">The preferred capture extent and maximum upload cadence, or <see langword="null"/> for the
/// platform default; a probe socket never needs one. Omitted from the wire when null.</param>
/// <param name="Controls">The authored device-control state (<see cref="WorldCameraControls"/>), or
/// <see langword="null"/> to leave every control at its driver default. One physical device carries one control state
/// across color and infrared, so the first declared camera screen authoring this for a seat wins regardless of sensor,
/// and a later <c>UpsertScreen</c> mutation re-resolves and applies the change live. Omitted from the wire when
/// null.</param>
/// <param name="Sensor">Which physical sensor this source's shared feed opens: <see cref="WorldCameraSensor.Color"/>
/// (the default) or <see cref="WorldCameraSensor.Infrared"/>, the infrared frame source a Windows Hello capable device
/// carries. Each sensor gets its own shared feed, so different sources may request different sensors at once; the
/// engine honors a Windows Face Authentication Profile V2 when published and admits simultaneous capture only after both
/// native streams prove live. An absent infrared source faults the bind loudly (the slot shows the no-signal
/// card).</param>
/// <param name="Seat">The 1-based local seat this source names: a camera is an input device seated like a pad, never
/// hardware named directly. <see langword="null"/> means the enclosing seat scope (an identity's HUD panel, a
/// seat-scoped probe socket) or seat 1 at world scope. Validated within <c>1..population.localSeats</c> when present.
/// Omitted from the wire when null.</param>
public sealed record WorldCameraSettings(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFeedProfile? Profile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCameraControls? Controls = null,
    WorldCameraSensor Sensor = WorldCameraSensor.Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Seat = null);
/// <summary>The settings of the <c>capture</c> producer: a live compositor capture of a desktop window keyed by title,
/// or of a whole monitor keyed by index. <paramref name="MonitorIndex"/> null is window mode; non-null is whole-monitor
/// mode, and <paramref name="WindowTitle"/> is then unused.</summary>
/// <param name="WindowTitle">The captured window's title in window mode; <see langword="null"/> in monitor mode.
/// Omitted from the wire when null.</param>
/// <param name="Profile">This capture's output extent and maximum refresh cadence.</param>
/// <param name="MonitorIndex">The 0-based monitor to capture whole (0 is the primary), or <see langword="null"/> for
/// window mode. Omitted from the wire when null.</param>
public sealed record WorldCaptureSettings(
    WorldFeedProfile Profile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WindowTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MonitorIndex = null);
/// <summary>
/// The settings shapes of the image producers the engine ships, and the one binding of a
/// <see cref="WorldScreenSource.Producer"/>'s settings object to a typed shape. A binding reads the object through the
/// world document's own serializer, so a member the shape does not declare is refused by name and a member it requires
/// is refused when absent, exactly as a document row is; the result is kept per source, so a source read every frame
/// binds once.
/// </summary>
public static class WorldImageProducerSettings {
    /// <summary>The camera producer's id.</summary>
    public const string CameraId = "camera";
    /// <summary>The desktop-capture producer's id.</summary>
    public const string CaptureId = "capture";
    /// <summary>The producer id of a <see cref="WorldScreenSource.Machine"/> source's render-graph instance. The machine
    /// arm stays typed because it names a <c>machines</c> row, so no document producer may take this id.</summary>
    public const string MachineId = "machine";
    /// <summary>The producer id of a <see cref="WorldScreenSource.Probe"/> source's render-graph instance. The probe arm
    /// stays typed because it names a <c>probes</c> row, so no document producer may take this id.</summary>
    public const string ProbeId = "probe";
    /// <summary>The QR producer's id.</summary>
    public const string QrId = "qr";
    /// <summary>The test-pattern producer's id.</summary>
    public const string TestPatternId = "testPattern";

    private static readonly ConditionalWeakTable<WorldScreenSource.Producer, Binding> Bindings = new();

    private static JsonTypeInfo<T> InfoOf<T>() => ((JsonTypeInfo<T>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(T)));
    private static bool TryReadShipped<T>(WorldScreenSource? source, string id, [NotNullWhen(returnValue: true)] out T? settings) where T : class {
        if (
            (source is WorldScreenSource.Producer producer) &&
            string.Equals(
                a: producer.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            ) &&
            (Bind<T>(producer: producer).Value is T bound)
        ) {
            settings = bound;

            return true;
        }

        settings = null;

        return false;
    }

    /// <summary>Binds a producer source's settings object to a settings shape.</summary>
    /// <typeparam name="T">The settings shape, registered with the world document's serializer.</typeparam>
    /// <param name="producer">The producer source.</param>
    /// <returns>The bound settings, or the refusal naming what did not bind.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="producer"/> is <see langword="null"/>.</exception>
    public static (T? Value, string? Refusal) Bind<T>(WorldScreenSource.Producer producer) where T : class {
        ArgumentNullException.ThrowIfNull(argument: producer);

        var binding = Bindings.GetValue(
            createValueCallback: static source => {
                var buffer = new ArrayBufferWriter<byte>();

                using (var writer = new Utf8JsonWriter(bufferWriter: buffer)) {
                    writer.WriteStartObject();

                    if (source.Settings is { } members) {
                        foreach (var (name, value) in members) {
                            writer.WritePropertyName(propertyName: name);
                            value.WriteTo(writer: writer);
                        }
                    }

                    writer.WriteEndObject();
                }

                return new Binding(Json: buffer.WrittenMemory.ToArray());
            },
            key: producer
        );

        lock (binding.Gate) {
            if (binding.Shape != typeof(T)) {
                try {
                    binding.Value = JsonSerializer.Deserialize(
                        jsonTypeInfo: InfoOf<T>(),
                        utf8Json: binding.Json
                    );
                    binding.Refusal = ((binding.Value is null)
                        ? "settings must be an object"
                        : null
                    );
                } catch (JsonException exception) {
                    binding.Value = null;
                    binding.Refusal = exception.Message;
                }

                binding.Shape = typeof(T);
            }

            return (((T?)binding.Value), binding.Refusal);
        }
    }
    /// <summary>Returns a producer source for settings: its id and its settings object as the document writes it.</summary>
    /// <typeparam name="T">The settings shape.</typeparam>
    /// <param name="id">The producer's id.</param>
    /// <param name="settings">The settings.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="settings"/> is
    /// <see langword="null"/>.</exception>
    public static WorldScreenSource.Producer SourceOf<T>(string id, T settings) where T : class {
        ArgumentNullException.ThrowIfNull(argument: id);
        ArgumentNullException.ThrowIfNull(argument: settings);

        var element = JsonSerializer.SerializeToElement(
            jsonTypeInfo: InfoOf<T>(),
            value: settings
        );
        var members = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var member in element.EnumerateObject()) {
            members[member.Name] = member.Value.Clone();
        }

        return new WorldScreenSource.Producer(
            Id: id,
            Settings: members
        );
    }
    /// <summary>Reads a source as the camera producer's, when it is one whose settings bind.</summary>
    /// <param name="source">The source.</param>
    /// <param name="camera">The camera settings, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> names the camera producer with valid settings.</returns>
    public static bool TryCamera(WorldScreenSource? source, [NotNullWhen(returnValue: true)] out WorldCameraSettings? camera) => TryReadShipped(
        id: CameraId,
        settings: out camera,
        source: source
    );
    /// <summary>Reads a source as the capture producer's, when it is one whose settings bind.</summary>
    /// <param name="source">The source.</param>
    /// <param name="capture">The capture settings, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> names the capture producer with valid settings.</returns>
    public static bool TryCapture(WorldScreenSource? source, [NotNullWhen(returnValue: true)] out WorldCaptureSettings? capture) => TryReadShipped(
        id: CaptureId,
        settings: out capture,
        source: source
    );
    /// <summary>Reads a source as the QR producer's, when it is one whose settings bind.</summary>
    /// <param name="source">The source.</param>
    /// <param name="qr">The QR settings, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> names the QR producer with valid settings.</returns>
    public static bool TryQr(WorldScreenSource? source, [NotNullWhen(returnValue: true)] out WorldQrSettings? qr) => TryReadShipped(
        id: QrId,
        settings: out qr,
        source: source
    );
    /// <summary>Reads a source as the test-pattern producer's, when it is one whose settings bind.</summary>
    /// <param name="source">The source.</param>
    /// <param name="pattern">The test-pattern settings, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> names the test-pattern producer with valid
    /// settings.</returns>
    public static bool TryTestPattern(WorldScreenSource? source, [NotNullWhen(returnValue: true)] out WorldTestPatternSettings? pattern) => TryReadShipped(
        id: TestPatternId,
        settings: out pattern,
        source: source
    );

    // One source's settings object, written once as JSON, and its last binding.
    private sealed class Binding(byte[] Json) {
        public Lock Gate { get; } = new();
        public byte[] Json { get; } = Json;

        public string? Refusal { get; set; }
        public Type? Shape { get; set; }
        public object? Value { get; set; }
    }
}
