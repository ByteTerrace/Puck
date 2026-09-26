using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;
using Puck.World;

namespace Puck.Cli.Counters;

/// <summary>
/// Turns one <c>world.counters --json</c> reading into a report run: every source's counts, each render node's newest
/// completed submission (its passes and their counts, the work outside every pass, its submission and revision) and
/// created objects, and the allocation reading, each count tagged with the class the World's <c>kinds</c> legend
/// declares for it, loosened to its pass's class: a deterministic kind counted in a per-backend-deterministic pass (one
/// whose work follows the device, as the SDF engine's upload follows its residency policy) is
/// per-backend-deterministic there, so the two backends are not held to it. The submission and revision identities are
/// not work kinds; they are recorded as
/// <see cref="WorkClass.Pacing"/> under <see cref="SubmissionKind"/> and <see cref="RevisionKind"/>, since which
/// submission a read lands on depends on when it ran.
/// </summary>
internal static class CountersReading {
    /// <summary>The name a node's submission identity is recorded under.</summary>
    public const string SubmissionKind = "gpu.sample.submission";
    /// <summary>The name a node's revision identity is recorded under.</summary>
    public const string RevisionKind = "gpu.sample.revision";

    private const string AllocationSection = WorkCounterReport.AllocationSection;
    private const string GpuSection = GpuWorkReport.Section;
    private const string Prefix = "[world.counters: {";

    /// <summary>Finds the one <c>world.counters --json</c> response in a leg's standard output and reads it into a
    /// run.</summary>
    /// <param name="stdout">The leg's standard output lines.</param>
    /// <param name="backend">The backend the leg ran on, which the device must report.</param>
    /// <param name="width">The offscreen width the leg ran at.</param>
    /// <param name="height">The offscreen height the leg ran at.</param>
    /// <param name="compiler">The shader toolchain identity.</param>
    /// <param name="run">The run, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the reading is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the output held exactly one well-formed reading.</returns>
    public static bool TryRead(IEnumerable<string> stdout, string backend, int width, int height, string compiler, [NotNullWhen(returnValue: true)] out WorldCountersRun? run, out string reason) {
        run = null;

        var readings = stdout.Where(predicate: IsReading).ToArray();

        if (readings.Length != 1) {
            reason = $"expected exactly one 'world.counters --json' response on standard output, found {readings.Length}";

            return false;
        }

        return TryReadLine(
            backend: backend,
            compiler: compiler,
            height: height,
            line: readings[0],
            reason: out reason,
            run: out run,
            width: width
        );
    }
    /// <summary>Indicates whether a standard output line is a <c>world.counters --json</c> response.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> when the line is one bracketed JSON reading.</returns>
    public static bool IsReading(string line) => (line.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: Prefix
    ) && line.EndsWith(
        comparisonType: StringComparison.Ordinal,
        value: "}]"
    ));
    /// <summary>Reads one <c>world.counters --json</c> response line into a run.</summary>
    /// <param name="line">The response line, as <see cref="IsReading"/> recognizes it.</param>
    /// <param name="backend">The backend the leg ran on, which the device must report.</param>
    /// <param name="width">The offscreen width the leg ran at.</param>
    /// <param name="height">The offscreen height the leg ran at.</param>
    /// <param name="compiler">The shader toolchain identity.</param>
    /// <param name="run">The run, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the reading is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the line held a well-formed reading.</returns>
    public static bool TryReadLine(string line, string backend, int width, int height, string compiler, [NotNullWhen(returnValue: true)] out WorldCountersRun? run, out string reason) {
        run = null;

        try {
            using var document = JsonDocument.Parse(json: line.AsMemory(
                length: ((line.Length - (Prefix.Length - 1)) - 1),
                start: (Prefix.Length - 1)
            ));

            return TryRead(
                backend: backend,
                compiler: compiler,
                height: height,
                reading: document.RootElement,
                reason: out reason,
                run: out run,
                width: width
            );
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)) {
            reason = $"the response is not the world.counters shape: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
    /// <summary>Reads one parsed <c>world.counters --json</c> object into a run.</summary>
    /// <param name="reading">The parsed object.</param>
    /// <param name="backend">The backend the leg ran on, which the device must report.</param>
    /// <param name="width">The offscreen width the leg ran at.</param>
    /// <param name="height">The offscreen height the leg ran at.</param>
    /// <param name="compiler">The shader toolchain identity.</param>
    /// <param name="run">The run, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the reading is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the reading is complete: a device of the leg's backend, an allocation
    /// reading, and a class for every count.</returns>
    public static bool TryRead(JsonElement reading, string backend, int width, int height, string compiler, [NotNullWhen(returnValue: true)] out WorldCountersRun? run, out string reason) {
        run = null;

        var classes = new Dictionary<string, WorkClass>(comparer: StringComparer.Ordinal);

        foreach (var kind in reading.GetProperty(propertyName: "kinds").EnumerateObject()) {
            var spelled = kind.Value.GetProperty(propertyName: "class").GetString()!;

            if (!EnumWireName<WorkClass>.TryParse(
                name: spelled,
                value: out var workClass
            )) {
                reason = $"kind '{kind.Name}' has the unknown class '{spelled}'";

                return false;
            }

            classes[kind.Name] = workClass;
        }

        var counts = new List<WorldCount>();
        var passes = new List<WorldCountersPass>();
        string? missing = null;

        // A count reads its kind's class, loosened to its pass's: a deterministic kind in a per-backend-deterministic
        // pass, whose work follows the device, is per-backend-deterministic there.
        void Add(string source, string? node, string? pass, JsonElement values, WorkClass passClass = WorkClass.Deterministic) {
            foreach (var count in values.EnumerateObject()) {
                if (!classes.TryGetValue(
                    key: count.Name,
                    value: out var workClass
                )) {
                    missing ??= count.Name;

                    continue;
                }

                counts.Add(item: new WorldCount(
                    Class: (((workClass == WorkClass.Deterministic) && (passClass == WorkClass.PerBackendDeterministic))
                        ? WorkClass.PerBackendDeterministic
                        : workClass
                    ),
                    Kind: count.Name,
                    Node: node,
                    Pass: pass,
                    Source: source,
                    Value: count.Value.GetInt64()
                ));
            }
        }

        foreach (var source in reading.GetProperty(propertyName: "sources").EnumerateArray()) {
            Add(
                node: null,
                pass: null,
                source: source.GetProperty(propertyName: "name").GetString()!,
                values: source.GetProperty(propertyName: "counts")
            );
        }

        if (!reading.TryGetProperty(
            propertyName: GpuSection,
            value: out var gpu
        )) {
            reason = $"the reading has no {GpuSection} section: the World composed no renderer";

            return false;
        }
        if (gpu.GetProperty(propertyName: "device").ValueKind == JsonValueKind.Null) {
            reason = "the device is unavailable: the renderer never brought one up";

            return false;
        }

        var device = gpu.GetProperty(propertyName: "device").Deserialize(jsonTypeInfo: WorldJsonContext.Default.GpuDeviceIdentity)!;

        if (!string.Equals(
            a: device.Backend,
            b: backend,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"the leg asked for {backend} but the device reports {device.Backend}";

            return false;
        }

        foreach (var node in gpu.GetProperty(propertyName: "nodes").EnumerateArray()) {
            var name = node.GetProperty(propertyName: "name").GetString()!;

            if (node.GetProperty(propertyName: "sample") is { ValueKind: JsonValueKind.Object } sample) {
                counts.Add(item: new WorldCount(
                    Class: WorkClass.Pacing,
                    Kind: SubmissionKind,
                    Node: name,
                    Pass: null,
                    Source: GpuSection,
                    Value: sample.GetProperty(propertyName: "submission").GetInt64()
                ));
                counts.Add(item: new WorldCount(
                    Class: WorkClass.Pacing,
                    Kind: RevisionKind,
                    Node: name,
                    Pass: null,
                    Source: GpuSection,
                    Value: sample.GetProperty(propertyName: "revision").GetInt64()
                ));

                foreach (var pass in sample.GetProperty(propertyName: "passes").EnumerateArray()) {
                    var label = pass.GetProperty(propertyName: "label").GetString()!;
                    var spelled = pass.GetProperty(propertyName: "state").GetString()!;
                    var spelledClass = pass.GetProperty(propertyName: "class").GetString()!;

                    if (
                        !EnumWireName<WorkClass>.TryParse(
                            name: spelledClass,
                            value: out var passClass
                        ) ||
                        (passClass is not (WorkClass.Deterministic or WorkClass.PerBackendDeterministic))
                    ) {
                        reason = $"node '{name}' pass '{label}' has the unknown class '{spelledClass}'";

                        return false;
                    }

                    if (!EnumWireName<GpuPassState>.TryParse(
                        name: spelled,
                        value: out var state
                    )) {
                        reason = $"node '{name}' pass '{label}' has the unknown state '{spelled}'";

                        return false;
                    }

                    passes.Add(item: new WorldCountersPass(
                        Label: label,
                        Node: name,
                        State: state
                    ));

                    if (pass.TryGetProperty(
                        propertyName: "counts",
                        value: out var passCounts
                    )) {
                        Add(
                            node: name,
                            pass: label,
                            passClass: passClass,
                            source: GpuSection,
                            values: passCounts
                        );
                    }
                }

                Add(
                    node: name,
                    pass: null,
                    source: GpuSection,
                    values: sample.GetProperty(propertyName: "outside")
                );
            }

            if (node.GetProperty(propertyName: "lifetime") is { ValueKind: JsonValueKind.Object } lifetime) {
                Add(
                    node: name,
                    pass: null,
                    source: GpuSection,
                    values: lifetime
                );
            }
        }

        if (!reading.TryGetProperty(
            propertyName: AllocationSection,
            value: out var allocation
        )) {
            reason = $"the reading has no {AllocationSection} section";

            return false;
        }

        foreach (var window in allocation.GetProperty(propertyName: "windows").EnumerateObject()) {
            counts.Add(item: new WorldCount(
                Class: WorkClass.AllocationZeroNonzero,
                Kind: window.Name,
                Node: null,
                Pass: null,
                Source: AllocationSection,
                Value: window.Value.GetInt64()
            ));
        }

        if (missing is not null) {
            reason = $"kind '{missing}' is not in the reading's kinds legend";

            return false;
        }

        run = new WorldCountersRun(
            Backend: backend,
            Compiler: compiler,
            Counts: counts,
            Device: device,
            GcMode: allocation.GetProperty(propertyName: "gcMode").GetString()!,
            Height: height,
            Passes: passes,
            Width: width
        );
        reason = string.Empty;

        return true;
    }
}
