using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>world.counters</c> readout: every <see cref="IWorkCounterSource"/> the World registered, the GPU work of
/// every render node on the host's <see cref="IGpuWorkRegistry"/>, and whether reading them allocates. A source is
/// discovered by being registered; the verb knows none of them by name. The text form prints one section per source,
/// sorted by name with the <c>allocation</c> and <c>gpu</c> sections: the source's name, then one
/// <c>&lt;kind&gt; &lt;value&gt;</c> line per kind (<see cref="WorkCounterReport"/>). The GPU nodes form the
/// <c>gpu</c> section: its header carries the device's identity (<see cref="GpuDeviceIdentity.AppendFields"/>), then
/// each node's <c>node &lt;name&gt; work …</c> lines (<see cref="GpuWorkReport"/>). A host with no renderer has no
/// <c>gpu</c> section. The <c>allocation</c> section measures the <see cref="ReadWindow"/> window — one read of every
/// registered count and every node's newest completed submission, into storage the verb keeps — with
/// <see cref="AllocationWindow.Measure"/>, and prints the host's GC mode and the fewest bytes a run of it allocated.
/// </summary>
/// <param name="sources">Every counter source registered in the World.</param>
/// <param name="gpu">The render nodes' registry, or <see langword="null"/> when the host composed no renderer.</param>
public sealed class WorldCountersCommandModule(IEnumerable<IWorkCounterSource> sources, IGpuWorkRegistry? gpu = null) : ICommandModule {
    /// <summary>The name of the section the allocation reading forms, and the filter that selects it.</summary>
    public const string AllocationSection = WorkCounterReport.AllocationSection;
    /// <summary>The name of the section the GPU nodes form, and the filter that selects it.</summary>
    public const string GpuSection = GpuWorkReport.Section;
    /// <summary>The allocation window the <see cref="AllocationSection"/> reports: one read of every count the verb
    /// reports.</summary>
    public const string ReadWindow = "world.counters.read";

    private const string JsonFlag = "--json";
    private const string Usage = "[world.counters: expected world.counters [<source-or-prefix>] [--json]]";
    private const string Verb = "world.counters";

    private readonly IWorkCounterSource[] m_sources = [.. sources.OrderBy(
        comparer: StringComparer.Ordinal,
        keySelector: static source => source.Name
    )];
    // The storage the read window reads into, kept across readouts so the window measures the reads alone.
    private readonly List<GpuWorkNode> m_readNodes = [];
    private readonly GpuWorkSample m_readSample = new();

    private Action? m_readWindow;

    private CommandResult Describe(WireArgs args) {
        string? filter = null;
        var json = false;

        for (var index = 0; (index < args.Count); index++) {
            if (args.Is(
                index: index,
                value: JsonFlag
            )) {
                json = true;
            } else if (filter is null) {
                filter = args[index].ToString();
            } else {
                return CommandResult.Error(output: Usage);
            }
        }

        var selected = m_sources.Where(predicate: source => Selects(
            filter: filter,
            name: source.Name
        )).ToArray();
        var includeGpu = ((gpu is not null) && Selects(
            filter: filter,
            name: GpuSection
        ));
        long? allocated = (Selects(
            filter: filter,
            name: AllocationSection
        )
            ? MeasureRead()
            : null
        );

        if ((selected.Length == 0) && !includeGpu && (allocated is null)) {
            return CommandResult.Error(output: $"[{Verb}: no source is named '{filter}' or sits under it — sources: {string.Join(
                separator: ", ",
                values: Names()
            )}]");
        }

        return new CommandResult(Output: (json
            ? WriteJson(
                allocated: allocated,
                includeGpu: includeGpu,
                selected: selected
            )
            : WriteText(
                allocated: allocated,
                includeGpu: includeGpu,
                selected: selected
            )
        ));
    }
    private static bool Selects(string? filter, string name) =>
        ((filter is null) || WorkCounterReport.Matches(
            filter: filter,
            name: name
        ));
    // Gathers the nodes outside the window, then measures the reads alone.
    private long MeasureRead() {
        m_readNodes.Clear();
        gpu?.CopyNodes(nodes: m_readNodes);

        return AllocationWindow.Measure(window: (m_readWindow ??= ReadEveryCount));
    }
    private void ReadEveryCount() {
        foreach (var source in m_sources) {
            ReadKinds(source: source);
        }

        foreach (var node in m_readNodes) {
            _ = node.Work.TryReadCompleted(sample: m_readSample);

            if (node.Lifetime is { } lifetime) {
                ReadKinds(source: lifetime);
            }
        }
    }
    private static void ReadKinds(IWorkCounterSource source) {
        foreach (var kind in source.WorkKinds) {
            _ = source.TryRead(
                kind: kind,
                value: out _
            );
        }
    }
    private IEnumerable<string> Names() {
        var names = m_sources.Select(selector: static source => source.Name).Append(element: AllocationSection);

        return ((gpu is null)
            ? names
            : names.Append(element: GpuSection)
        ).Order(comparer: StringComparer.Ordinal);
    }
    private string WriteJson(IWorkCounterSource[] selected, bool includeGpu, long? allocated) {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer)) {
            writer.WriteStartObject();
            writer.WriteStartArray(propertyName: "sources");

            foreach (var source in selected) {
                WorkCounterReport.WriteSource(
                    source: source,
                    writer: writer
                );
            }

            writer.WriteEndArray();

            if (includeGpu) {
                var sample = new GpuWorkSample();

                writer.WriteStartObject(propertyName: GpuSection);
                writer.WritePropertyName(propertyName: "device");

                if (gpu?.DeviceIdentity is { } identity) {
                    identity.WriteJson(writer: writer);
                } else {
                    writer.WriteNullValue();
                }

                writer.WriteStartArray(propertyName: "nodes");

                foreach (var node in GpuNodes()) {
                    GpuWorkReport.WriteNode(
                        node: node,
                        sample: sample,
                        writer: writer
                    );
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (allocated is { } bytes) {
                writer.WriteStartObject(propertyName: AllocationSection);
                writer.WriteString(
                    propertyName: "gcMode",
                    value: AllocationWindow.GcMode
                );
                writer.WriteStartObject(propertyName: "windows");
                writer.WriteNumber(
                    propertyName: ReadWindow,
                    value: bytes
                );
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            WriteLegend(
                includeGpu: includeGpu,
                selected: selected,
                writer: writer
            );
            writer.WriteEndObject();
        }

        return $"[{Verb}: {Encoding.UTF8.GetString(bytes: buffer.WrittenSpan)}]";
    }
    // Every kind the object reports, once, with its unit and class: the sources' kinds in report order, then the GPU
    // submission and lifetime kinds when the gpu section is present.
    private static void WriteLegend(Utf8JsonWriter writer, IWorkCounterSource[] selected, bool includeGpu) {
        var written = new HashSet<string>(comparer: StringComparer.Ordinal);

        writer.WriteStartObject(propertyName: "kinds");

        void Write(ReadOnlySpan<WorkKind> kinds) {
            foreach (var kind in kinds) {
                if (written.Add(item: kind.Name)) {
                    WorkCounterReport.WriteKind(
                        kind: kind,
                        writer: writer
                    );
                }
            }
        }

        foreach (var source in selected) {
            Write(kinds: source.WorkKinds);
        }

        if (includeGpu) {
            Write(kinds: GpuWork.SubmissionKinds);
            Write(kinds: GpuWork.LifetimeKinds);
        }

        writer.WriteEndObject();
    }
    private string WriteText(IWorkCounterSource[] selected, bool includeGpu, long? allocated) {
        var sections = new List<(string Name, Action<StringBuilder> Append)>(capacity: (selected.Length + 2));

        foreach (var source in selected) {
            sections.Add(item: (source.Name, builder => WorkCounterReport.AppendSection(
                builder: builder,
                source: source
            )));
        }

        if (includeGpu) {
            sections.Add(item: (GpuSection, AppendGpu));
        }

        if (allocated is { } bytes) {
            sections.Add(item: (AllocationSection, builder => builder.Append(value: AllocationSection).Append(value: " gc=\"").Append(value: AllocationWindow.GcMode).Append(value: "\"\n").Append(value: ReadWindow).Append(value: ' ').Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{bytes}"
            ).Append(value: '\n')));
        }

        sections.Sort(comparison: static (left, right) => StringComparer.Ordinal.Compare(
            x: left.Name,
            y: right.Name
        ));

        var text = new StringBuilder(value: $"[{Verb}: ");

        foreach (var (_, append) in sections) {
            append(obj: text);
        }

        return text.Append(value: ']').ToString();
    }
    private void AppendGpu(StringBuilder builder) {
        _ = builder.Append(value: GpuSection);
        _ = ((gpu?.DeviceIdentity is { } identity)
            ? identity.AppendFields(builder: builder)
            : builder.Append(value: " device unavailable")
        ).Append(value: '\n');

        var nodes = GpuNodes();

        if (nodes.Count == 0) {
            _ = builder.Append(value: "nodes none: the renderer is not built yet\n");

            return;
        }

        var sample = new GpuWorkSample();

        foreach (var node in nodes) {
            _ = GpuWorkReport.AppendNode(
                builder: builder,
                node: node,
                sample: sample
            );
        }
    }
    private List<GpuWorkNode> GpuNodes() {
        var nodes = new List<GpuWorkNode>();

        gpu?.CopyNodes(nodes: nodes);

        return nodes;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            description: $"Echoes every registered work counter: world.counters [<source-or-prefix>] [--json]. One section per counter source, sorted by name — the source's dotted name, then a `<kind> <value>` line per kind it counts (counts only go up; a reader diffs two reads). The `{GpuSection}` section's header names the device (backend, adapter, vendor and device ids, driver version, API version), then it holds each render node's GPU work for its newest completed submission: per node (world first, then its hosted pipelines, overlay, view:<name>) a `node <name> work submission=S revision=R` line, one `work <pass> executed|skipped|not-reached` line per pass with its counts, `work outside` for work between passes, and `work lifetime` for created objects; `work unavailable` until a submission completes. The `{AllocationSection}` section names the GC mode and the fewest managed bytes one read of every count allocated ({ReadWindow}, the least of up to {AllocationWindow.MaximumWindows} reads; only zero or not zero means anything). A filter selects the sections named by it or under it by whole dotted segments (world.counters {GpuSection}, world.counters state). --json prints one line of JSON: {{\"sources\":[{{\"name\":…,\"counts\":{{\"<kind>\":<value>}}}}],\"{GpuSection}\":{{\"device\":{{…}},\"nodes\":[…]}},\"{AllocationSection}\":{{\"gcMode\":…,\"windows\":{{\"{ReadWindow}\":<bytes>}}}},\"kinds\":{{\"<kind>\":{{\"unit\":…,\"class\":\"deterministic|per-backend-deterministic|pacing\"}}}}}}. Counts run always.",
            handler: (_, args) => Describe(args: args),
            name: Verb
        );
    }
}
