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
/// <c>gpu</c> section: its header carries the device's identity (<see cref="GpuDeviceIdentity.AppendFields"/>) and, once the device is up, a <c>capabilities</c> line (<see cref="GpuDeviceCapabilities.AppendFields"/>), then
/// each node's <c>node &lt;name&gt; work …</c> lines (<see cref="GpuWorkReport"/>). A host with no renderer has no
/// <c>gpu</c> section. The <c>allocation</c> section measures the <see cref="ReadWindow"/> window — one read of every
/// registered count and every node's newest completed submission, into storage the verb keeps — with
/// <see cref="AllocationWindow.Measure"/>, and prints the host's GC mode and the fewest bytes a run of it allocated.
/// <para>
/// The <c>gpu</c> section narrows with <c>node &lt;name&gt;</c>, <c>pass &lt;label&gt;</c>,
/// <c>counter &lt;prefix&gt;</c> and <c>summary</c> (<see cref="GpuWorkSelection"/>). A text <c>gpu</c> section whose
/// lines would pass <see cref="GpuTextBudget"/> characters prints the <c>summary</c> form under a note saying so; the
/// JSON form is never replaced.
/// </para>
/// </summary>
/// <param name="sources">Every counter source registered in the World.</param>
/// <param name="gpu">The render nodes' registry, or <see langword="null"/> when the host composed no renderer.</param>
public sealed class WorldCountersCommandModule(IEnumerable<IWorkCounterSource> sources, IGpuWorkRegistry? gpu = null) : ICommandModule {
    /// <summary>The name of the section the allocation reading forms, and the filter that selects it.</summary>
    public const string AllocationSection = WorkCounterReport.AllocationSection;
    /// <summary>The name of the section the GPU nodes form, and the filter that selects it.</summary>
    public const string GpuSection = GpuWorkReport.Section;
    /// <summary>The most characters the node lines of a text <c>gpu</c> section hold before the section prints its
    /// summary form instead: far under the 1 MiB an MCP result carries, and a few tens of thousands of tokens.</summary>
    public const int GpuTextBudget = 131072;
    /// <summary>The allocation window the <see cref="AllocationSection"/> reports: one read of every count the verb
    /// reports.</summary>
    public const string ReadWindow = "world.counters.read";

    private const string CounterWord = "counter";
    private const string JsonFlag = "--json";
    private const string NodeWord = "node";
    private const string PassWord = "pass";
    private const string SummaryWord = "summary";
    private const string Usage = "[world.counters: expected world.counters [<source-or-prefix>] [node <name>] [pass <label>] [counter <prefix>] [summary] [--json]]";
    private const string Verb = "world.counters";

    private readonly IWorkCounterSource[] m_sources = [.. sources.OrderBy(
        comparer: StringComparer.Ordinal,
        keySelector: static source => source.Name
    )];
    // The storage the read window reads into, kept across readouts so the window measures the reads alone.
    private readonly List<GpuWorkNode> m_readNodes = [];
    private readonly GpuWorkSample m_readSample = new();

    private Action? m_readWindow;

    // What the gpu section of one readout selects: the nodes by name, the passes by label, the counts by name prefix,
    // and whether it prints one summary line per node.
    private readonly record struct GpuQuery(string? Counter, string? Node, string? Pass, bool Summary) {
        public bool Narrows =>
            ((Counter is not null) || (Node is not null) || (Pass is not null) || Summary);
        public GpuWorkSelection Selection => ((Pass is null)
            ? new GpuWorkSelection(counter: Counter)
            : GpuWorkSelection.ForPass(
                counter: Counter,
                label: Pass
            )
        );
    }

    private CommandResult Describe(WireArgs args) {
        string? counter = null;
        string? filter = null;
        string? node = null;
        string? pass = null;
        var json = false;
        var summary = false;

        for (var index = 0; (index < args.Count); index++) {
            if (args.Is(
                index: index,
                value: JsonFlag
            )) {
                json = true;
            } else if (args.Is(
                index: index,
                value: SummaryWord
            )) {
                summary = true;
            } else if (args.Is(
                index: index,
                value: NodeWord
            )) {
                if (!TakeValue(
                    args: args,
                    index: (index++),
                    value: ref node
                )) {
                    return CommandResult.Error(output: Usage);
                }
            } else if (args.Is(
                index: index,
                value: PassWord
            )) {
                if (!TakeValue(
                    args: args,
                    index: (index++),
                    value: ref pass
                )) {
                    return CommandResult.Error(output: Usage);
                }
            } else if (args.Is(
                index: index,
                value: CounterWord
            )) {
                if (!TakeValue(
                    args: args,
                    index: (index++),
                    value: ref counter
                )) {
                    return CommandResult.Error(output: Usage);
                }
            } else if (filter is null) {
                filter = args[index].ToString();
            } else {
                return CommandResult.Error(output: Usage);
            }
        }

        var query = new GpuQuery(
            Counter: counter,
            Node: node,
            Pass: pass,
            Summary: summary
        );

        if (query.Narrows) {
            if ((filter is not null) && !Selects(
                filter: filter,
                name: GpuSection
            )) {
                return CommandResult.Error(output: $"[{Verb}: node, pass, counter and summary select within the {GpuSection} section, not '{filter}']");
            }

            filter = GpuSection;
        }

        if (summary && (counter is not null)) {
            return CommandResult.Error(output: $"[{Verb}: summary counts draws, vertices, texels and dispatches, so it takes node and pass but not counter]");
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

        var nodes = (includeGpu
            ? GpuNodes()
            : []);

        if (includeGpu && (Refuse(
            nodes: nodes,
            query: query
        ) is { } refusal)) {
            return CommandResult.Error(output: refusal);
        }

        return new CommandResult(Output: (json
            ? WriteJson(
                allocated: allocated,
                includeGpu: includeGpu,
                nodes: nodes,
                query: query,
                selected: selected
            )
            : WriteText(
                allocated: allocated,
                gpuText: (includeGpu
                    ? GpuText(
                        nodes: nodes,
                        query: query
                    )
                    : null
                ),
                selected: selected
            )
        ));
    }
    // Reads the value token after the keyword at index into value; false when the keyword has no value or repeats.
    private static bool TakeValue(WireArgs args, int index, ref string? value) {
        if ((value is not null) || ((index + 1) >= args.Count)) {
            return false;
        }

        value = args[(index + 1)].ToString();

        return true;
    }
    private static bool Selects(string? filter, string name) =>
        ((filter is null) || WorkCounterReport.Matches(
            filter: filter,
            name: name
        ));
    // The refusal text when the query names a node, pass or counter the GPU section does not have; null otherwise.
    private static string? Refuse(List<GpuWorkNode> nodes, GpuQuery query) {
        var selection = query.Selection;

        if ((query.Counter is not null) && !selection.SelectsAnyCount()) {
            return $"[{Verb}: no gpu count starts with '{query.Counter}' — counts are {string.Join(
                separator: ", ",
                values: GpuWork.SubmissionKinds.ToArray().Concat(second: GpuWork.LifetimeKinds.ToArray()).Select(selector: static kind => (kind.Name.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "gpu."
                )
                    ? kind.Name[4..]
                    : kind.Name)
                )
            )}]";
        }

        if (nodes.Count == 0) {
            return null;
        }

        var named = Selected(
            nodes: nodes,
            query: query
        );

        if (named.Count == 0) {
            var hint = ((nodes.Any(predicate: node => HasPass(
                node: node,
                selection: GpuWorkSelection.ForPass(label: query.Node!)
            )))
                ? $" — '{query.Node}' names a pass; use: {Verb} {GpuSection} pass {query.Node}"
                : string.Empty
            );

            return $"[{Verb}: no gpu node is named '{query.Node}' or sits under it — nodes: {string.Join(
                separator: ", ",
                values: nodes.Take(count: 16).Select(selector: static node => node.Name)
            )}{((nodes.Count > 16) ? $", … ({nodes.Count} in all)" : string.Empty)}{hint}]";
        }

        if ((query.Pass is not null) && !named.Any(predicate: node => HasPass(
            node: node,
            selection: selection
        ))) {
            return $"[{Verb}: no gpu pass is named '{query.Pass}' or sits under it in {(
                (query.Node is null)
                    ? "any node"
                    : $"node '{query.Node}'"
            )}]";
        }

        return null;
    }
    private static bool HasPass(GpuWorkNode node, GpuWorkSelection selection) {
        var sample = new GpuWorkSample();

        if (!node.Work.TryReadCompleted(sample: sample)) {
            return false;
        }

        foreach (var label in sample.PassLabels) {
            if (selection.IncludesPass(label: label)) {
                return true;
            }
        }

        return false;
    }
    private static List<GpuWorkNode> Selected(List<GpuWorkNode> nodes, GpuQuery query) =>
        ((query.Node is null)
            ? nodes
            : [.. nodes.Where(predicate: node => GpuWorkSelection.Matches(
                filter: query.Node,
                name: node.Name
            ))]
        );
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
    private string WriteJson(IWorkCounterSource[] selected, bool includeGpu, List<GpuWorkNode> nodes, GpuQuery query, long? allocated) {
        var buffer = new ArrayBufferWriter<byte>();
        var selection = query.Selection;
        var shown = Selected(
            nodes: nodes,
            query: query
        );

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

                writer.WritePropertyName(propertyName: GpuDeviceCapabilities.Section);

                if (gpu?.DeviceCapabilities is { } capabilities) {
                    capabilities.WriteJson(writer: writer);
                } else {
                    writer.WriteNullValue();
                }

                writer.WriteStartArray(propertyName: "nodes");

                foreach (var node in shown) {
                    _ = (query.Summary
                        ? GpuWorkReport.TryWriteSummary(
                            node: node,
                            sample: sample,
                            selection: selection,
                            writer: writer
                        )
                        : GpuWorkReport.TryWriteNode(
                            node: node,
                            sample: sample,
                            selection: selection,
                            writer: writer
                        )
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
                nodes: shown,
                query: query,
                selected: selected,
                writer: writer
            );
            writer.WriteEndObject();
        }

        return $"[{Verb}: {Encoding.UTF8.GetString(bytes: buffer.WrittenSpan)}]";
    }
    // Every kind the object reports, once, with its unit and class: the sources' kinds in report order, then the GPU
    // submission and lifetime kinds the query selects when the gpu section is present, and any other kind a node's
    // lifetime counters report (a routed residency's endpoint row, say). A summary reports no kind of its own.
    private static void WriteLegend(Utf8JsonWriter writer, IWorkCounterSource[] selected, bool includeGpu, List<GpuWorkNode> nodes, GpuQuery query) {
        var selection = query.Selection;
        var written = new HashSet<string>(comparer: StringComparer.Ordinal);

        writer.WriteStartObject(propertyName: "kinds");

        // The submission kinds are filtered by column, the lifetime kinds by name, and a source's kinds are not filtered.
        void Write(ReadOnlySpan<WorkKind> kinds, bool submission = false, bool lifetime = false) {
            for (var index = 0; (index < kinds.Length); index++) {
                var kind = kinds[index];

                if ((submission && !selection.IncludesColumn(column: index)) || (lifetime && !selection.IncludesLifetime(kind: kind.Name)) || !written.Add(item: kind.Name)) { continue; }
                WorkCounterReport.WriteKind(
                    kind: kind,
                    writer: writer
                );
            }
        }

        foreach (var source in selected) {
            Write(kinds: source.WorkKinds);
        }

        if (includeGpu && !query.Summary) {
            Write(
                kinds: GpuWork.SubmissionKinds,
                submission: true
            );

            if (query.Pass is null) {
                Write(
                    kinds: GpuWork.LifetimeKinds,
                    lifetime: true
                );

                foreach (var node in nodes) {
                    if (node.Lifetime is { } nodeLifetime) {
                        Write(
                            kinds: nodeLifetime.WorkKinds,
                            lifetime: true
                        );
                    }
                }
            }
        }

        writer.WriteEndObject();
    }
    private static string WriteText(IWorkCounterSource[] selected, string? gpuText, long? allocated) {
        var sections = new List<(string Name, Action<StringBuilder> Append)>(capacity: (selected.Length + 2));

        foreach (var source in selected) {
            sections.Add(item: (source.Name, builder => WorkCounterReport.AppendSection(
                builder: builder,
                source: source
            )));
        }

        if (gpuText is not null) {
            sections.Add(item: (GpuSection, builder => builder.Append(value: gpuText)));
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
    // The gpu section's text: the device header, then each selected node's lines, or one summary line per node when the
    // query asks for a summary or the lines would pass the budget.
    private string GpuText(List<GpuWorkNode> nodes, GpuQuery query) {
        var builder = new StringBuilder(value: GpuSection);

        _ = ((gpu?.DeviceIdentity is { } identity)
            ? identity.AppendFields(builder: builder)
            : builder.Append(value: " device unavailable")
        ).Append(value: '\n');

        if (gpu?.DeviceCapabilities is { } capabilities) {
            _ = capabilities.AppendFields(builder: builder.Append(value: GpuDeviceCapabilities.Section)).Append(value: '\n');
        }

        if (nodes.Count == 0) {
            return builder.Append(value: "nodes none: the renderer is not built yet\n").ToString();
        }

        var selection = query.Selection;
        var sample = new GpuWorkSample();
        var shown = Selected(
            nodes: nodes,
            query: query
        );
        var header = builder.Length;

        if (!query.Summary) {
            foreach (var node in shown) {
                _ = GpuWorkReport.TryAppendNode(
                    builder: builder,
                    node: node,
                    sample: sample,
                    selection: selection
                );
            }

            if ((builder.Length - header) <= GpuTextBudget) {
                return builder.ToString();
            }

            var characters = (builder.Length - header);

            builder.Length = header;
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"summary: the lines for these nodes run {characters} characters, past the {GpuTextBudget} the readout prints; showing one line per node — narrow with node <name>, pass <label> or counter <prefix>\n");
        }

        foreach (var node in shown) {
            _ = GpuWorkReport.TryAppendSummary(
                builder: builder,
                node: node,
                sample: sample,
                selection: selection
            );
        }

        return builder.ToString();
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
            description: $"Echoes every registered work counter: world.counters [<source-or-prefix>] [node <name>] [pass <label>] [counter <prefix>] [summary] [--json]. One section per counter source, sorted by name — the source's dotted name, then a `<kind> <value>` line per kind it counts (counts only go up; a reader diffs two reads). The `{GpuSection}` section's header names the device (backend, adapter, vendor and device ids, driver version, API version), then a `capabilities` line names what it can bind (descriptor sets or root-signature words, push-constant bytes, per-stage descriptor limits, and on Direct3D 12 the binding tier, root signature version, shader model and heap sizes), then it holds each render node's GPU work for its newest completed submission: per node (world first, then its hosted pipelines, overlay, view:<name>) a `node <name> work submission=S revision=R` line, one `work <pass> executed|skipped|not-reached` line per pass with its counts, `work outside` for work between passes, and `work lifetime` for created objects; `work unavailable` until a submission completes. A full `{GpuSection}` answer runs to hundreds of kilobytes in a large world, so narrow it: `node <name>` keeps one node (and the nodes under it, `session$26` keeps `session$26$24`), `pass <label>` keeps one pass (`sdf.world$mesh`, or `sdf.world` for every pass under it) in every node that has it and drops the lifetime, owned-bytes and outside lines, `counter <prefix>` keeps the counts whose name starts with the prefix (`draws` keeps draws and draws.indirect; `vertices`, `texels`), and `summary` prints one `node <name> draws=N vertices=N texels=N dispatches=N` line per node (dispatches are direct and indirect together; with `pass`, only that pass). The options combine, and select the `{GpuSection}` section by themselves. A text `{GpuSection}` section that would pass {GpuTextBudget} characters prints the summary form under a note saying so. The `{AllocationSection}` section names the GC mode and the fewest managed bytes one read of every count allocated ({ReadWindow}, the least of up to {AllocationWindow.MaximumWindows} reads; only zero or not zero means anything). A filter selects the sections named by it or under it by whole dotted segments (world.counters {GpuSection}, world.counters state). --json prints one line of JSON: {{\"sources\":[{{\"name\":…,\"counts\":{{\"<kind>\":<value>}}}}],\"{GpuSection}\":{{\"device\":{{…}},\"capabilities\":{{…}},\"nodes\":[…]}},\"{AllocationSection}\":{{\"gcMode\":…,\"windows\":{{\"{ReadWindow}\":<bytes>}}}},\"kinds\":{{\"<kind>\":{{\"unit\":…,\"class\":\"deterministic|per-backend-deterministic|pacing\"}}}}}}; the options narrow its nodes the same way and `summary` writes each node as {{\"name\":…,\"summary\":{{\"draws\":…,\"vertices\":…,\"texels\":…,\"dispatches\":…}}}}, and the JSON is never replaced by a summary. Counts run always.",
            handler: (_, args) => Describe(args: args),
            name: Verb
        );
    }
}
