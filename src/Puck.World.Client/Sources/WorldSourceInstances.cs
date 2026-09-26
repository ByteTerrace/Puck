using System.Buffers;
using System.Text.Json;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>
/// The render-graph source instances a world's screens read: one external instance per distinct producer, machine or
/// probe source shown, whose package is <c>source.&lt;producer id&gt;</c> (<see cref="RenderGraphInstance.SourcePackage"/>)
/// and which carries the source's settings. Screens showing equal sources read one instance, named after the first
/// screen showing it (<see cref="WorldViewNames.Source"/>), so the scheduler renders it at most once a frame however many
/// screens show it. A producer source carries its settings object as the document spelled it; a machine source is the
/// <see cref="WorldImageProducerSettings.MachineId"/> source with <c>instance</c> and <c>output</c> settings, and a probe
/// source the <see cref="WorldImageProducerSettings.ProbeId"/> source with an <c>id</c> setting. Every other arm (none,
/// view, session, text) is no source instance: a view and a session are rendered instances of their own. An instance's
/// <see cref="RenderGraphInstance.Handle"/> is its identity as a displayed source.
/// </summary>
public sealed class WorldSourceInstances {
    private const string IdSetting = "id";
    private const string InstanceSetting = "instance";
    private const string OutputSetting = "output";

    private readonly string?[] m_byScreen;

    private WorldSourceInstances(IReadOnlyList<RenderGraphInstance> instances, string?[] byScreen) {
        Instances = instances;
        m_byScreen = byScreen;
    }

    /// <summary>Gets the source instances in the order of the first screen showing each.</summary>
    public IReadOnlyList<RenderGraphInstance> Instances { get; }

    private static JsonElement Text(string value) {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer)) {
            writer.WriteStringValue(value: value);
        }

        using var document = JsonDocument.Parse(utf8Json: buffer.WrittenMemory);

        return document.RootElement.Clone();
    }
    private static string? TextOf(IReadOnlyDictionary<string, JsonElement>? settings, string name) => (((settings is not null) && settings.TryGetValue(
        key: name,
        value: out var value
    ) && (value.ValueKind == JsonValueKind.String))
        ? value.GetString()
        : null
    );
    // The producer id and settings a source is read through, or null for an arm no source instance reads.
    private static (string Producer, IReadOnlyDictionary<string, JsonElement>? Settings)? ReadThrough(WorldScreenSource? source) => source switch {
        WorldScreenSource.Producer producer => (producer.Id, producer.Settings),
        WorldScreenSource.Machine machine => (WorldImageProducerSettings.MachineId, new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) {
            [InstanceSetting] = Text(value: machine.Instance),
            [OutputSetting] = Text(value: machine.Output),
        }),
        WorldScreenSource.Probe probe => (WorldImageProducerSettings.ProbeId, new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) {
            [IdSetting] = Text(value: probe.Id),
        }),
        _ => null,
    };

    /// <summary>Derives the source instances the screens read.</summary>
    /// <param name="shown">The source each screen shows, by 0-based screen index, or <see langword="null"/> for a screen
    /// showing none.</param>
    /// <returns>The source instances and which one each screen reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shown"/> is <see langword="null"/>.</exception>
    public static WorldSourceInstances Of(IReadOnlyList<WorldScreenSource?> shown) {
        ArgumentNullException.ThrowIfNull(argument: shown);

        var instances = new List<RenderGraphInstance>();
        var byScreen = new string?[shown.Count];

        for (var screen = 0; (screen < shown.Count); screen++) {
            if (ReadThrough(source: shown[screen]) is not { } source) {
                continue;
            }

            var package = RenderGraphInstance.SourcePackage(producer: source.Producer);
            var existing = instances.Find(match: instance => (
                string.Equals(
                    a: instance.ExternalPackage,
                    b: package,
                    comparisonType: StringComparison.Ordinal
                ) &&
                ImageSourceSettings.Equal(
                    left: instance.Settings,
                    right: source.Settings
                )
            ));

            if (existing is null) {
                existing = RenderGraphInstance.Source(
                    name: WorldViewNames.Source(screen: screen),
                    producer: source.Producer,
                    settings: source.Settings
                );
                instances.Add(item: existing);
            }

            byScreen[screen] = existing.Name;
        }

        return new WorldSourceInstances(
            byScreen: byScreen,
            instances: instances
        );
    }
    /// <summary>Returns the screen source a source instance reads, rebuilt from its package and settings: the
    /// <see cref="WorldScreenSource.Producer"/>, <see cref="WorldScreenSource.Machine"/> or
    /// <see cref="WorldScreenSource.Probe"/> source it was derived from.</summary>
    /// <param name="instance">A source instance.</param>
    /// <returns>The source, or <see langword="null"/> when <paramref name="instance"/> is no source, or a machine or
    /// probe source whose settings lack a member that source names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    public static WorldScreenSource? SourceOf(RenderGraphInstance instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        return instance.SourceProducer switch {
            null => null,
            WorldImageProducerSettings.MachineId => (((TextOf(name: InstanceSetting, settings: instance.Settings) is { } machine) && (TextOf(name: OutputSetting, settings: instance.Settings) is { } output))
                ? new WorldScreenSource.Machine(
                    Instance: machine,
                    Output: output
                )
                : null),
            WorldImageProducerSettings.ProbeId => ((TextOf(name: IdSetting, settings: instance.Settings) is { } probe)
                ? new WorldScreenSource.Probe(Id: probe)
                : null),
            var producer => new WorldScreenSource.Producer(
                Id: producer,
                Settings: instance.Settings
            ),
        };
    }
    /// <summary>Returns the handle of the source instance a screen reads.</summary>
    /// <param name="screen">The 0-based screen index.</param>
    /// <returns>The instance's <see cref="RenderGraphInstance.Handle"/>, or <see langword="null"/> when the screen reads
    /// no source instance or is out of range.</returns>
    public SourceHandle? HandleOf(int screen) => ((InstanceOf(screen: screen) is { } name)
        ? SourceHandle.Producer(name: name)
        : null
    );
    /// <summary>Returns the name of the source instance a screen reads.</summary>
    /// <param name="screen">The 0-based screen index.</param>
    /// <returns>The instance name, or <see langword="null"/> when the screen reads no source instance or is out of
    /// range.</returns>
    public string? InstanceOf(int screen) => (((screen >= 0) && (screen < m_byScreen.Length))
        ? m_byScreen[screen]
        : null
    );
}
