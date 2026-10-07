using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;
using Puck.Assets;
using Puck.World;

namespace Puck.Cli.Counters;

/// <summary>
/// The counted-cost ceilings the counters workload is held to (<c>puck.counters.ceilings.v1</c>). Every GPU submission
/// count of a render node, in a pass or outside every pass, that is deterministic or per-backend-deterministic is held
/// under a ceiling: <see cref="Record"/> takes each count's reading as its ceiling, so a count that read zero is a required
/// zero, and <see cref="Check"/> fails a count over its ceiling, a required zero broken and a count no ceiling was recorded
/// for, naming its backend, kind, pass and node. A backend's deterministic ceilings are shared by every device. Required
/// zeros of kernel kinds are shared unless another retained device record owns that count;
/// then the newly recorded zero belongs to its recording device. Other per-backend-deterministic ceilings are
/// one record per device (<see cref="IsSameDevice"/>), and a run is judged against its own device's record. A run on a
/// device with no record fails by name.
/// </summary>
internal static class CountersCeilings {
    /// <summary>The ceilings the workload is held to, repository-relative.</summary>
    public const string CeilingsPath = "tests/Puck.Counters/counters.ceilings.json";

    private const string Outside = "outside";

    private static readonly Dictionary<string, WorkKind> SubmissionKindsByName = GpuWork.SubmissionKinds.ToArray().ToDictionary(keySelector: static kind => kind.Name);

    /// <summary>What a check found: the counts that failed their ceilings, and the notes about how they were judged.</summary>
    /// <param name="Failures">One line per failure, naming the backend, class, kind, pass and node, or the device no
    /// ceilings were recorded for.</param>
    /// <param name="Notes">One line per backend whose device record was recorded under another driver version.</param>
    public sealed record Verdict(IReadOnlyList<string> Failures, IReadOnlyList<string> Notes);

    /// <summary>Indicates whether a count is held under a ceiling: a GPU submission kind of a render node, in a pass or
    /// outside every pass, whose class two runs are held to.</summary>
    /// <param name="count">The count.</param>
    /// <returns><see langword="true"/> when a ceiling holds the count.</returns>
    public static bool IsCeiled(WorldCount count) => (
        string.Equals(a: count.Source, b: GpuWorkReport.Section, comparisonType: StringComparison.Ordinal) &&
        (count.Node is not null) &&
        SubmissionKindsByName.ContainsKey(key: count.Kind) &&
        (count.Class is WorkClass.Deterministic or WorkClass.PerBackendDeterministic)
    );
    /// <summary>Indicates whether a fresh ceiling starts among its backend's shared ceilings: a deterministic count's, or a
    /// required zero. A merge scopes a required zero to its recording device when another retained device owns the count.</summary>
    /// <param name="recorded">The class the count is recorded as.</param>
    /// <param name="kind">The submission kind, which owns its default class.</param>
    /// <param name="ceiling">The budget; zero is implicit in the document.</param>
    /// <returns><see langword="true"/> when the ceiling starts among the backend's shared ceilings before merging.</returns>
    public static bool IsShared(WorkClass recorded, WorkKind kind, long ceiling) =>
        ((recorded == WorkClass.Deterministic) || ((ceiling == 0L) &&
        (recorded == WorkClass.PerBackendDeterministic) && (kind.Class == WorkClass.PerBackendDeterministic)));

    private static bool IsShared(WorldCountCeiling ceiling) => IsShared(recorded: ceiling.Class,
        kind: SubmissionKindsByName[ceiling.Kind], ceiling: ceiling.Ceiling);

    /// <summary>Indicates whether two devices share one ceilings record: the same backend, the same adapter (its PCI vendor
    /// and device identifiers) and the same driver implementation (<see cref="GpuDeviceIdentity.DriverId"/>). The driver
    /// version is recorded evidence, not part of the key: a driver update keeps the device's record, and a count the update
    /// moves is judged against it.</summary>
    /// <param name="left">The first device.</param>
    /// <param name="right">The second device.</param>
    /// <returns><see langword="true"/> when one record holds both.</returns>
    public static bool IsSameDevice(GpuDeviceIdentity left, GpuDeviceIdentity right) => (
        string.Equals(a: left.Backend, b: right.Backend, comparisonType: StringComparison.Ordinal) &&
        (left.VendorId == right.VendorId) &&
        (left.DeviceId == right.DeviceId) &&
        (left.DriverId == right.DriverId)
    );
    /// <summary>Records a report's counts as ceilings: every ceiled count's reading its ceiling, and for every pass that did
    /// not execute a zero for each submission kind, which the pass reads once it does. Each backend's deterministic
    /// ceilings and required zeros start among its shared ceilings, and the rest are the record of the device its run ran on.
    /// <see cref="TryMerge"/> scopes a required zero when another retained device owns that count.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The ceilings, holding one device per backend.</returns>
    public static WorldCountersCeilings Record(WorldCountersReport report) => new(
        Backends: [.. report.Runs.Select(selector: static run => {
            var ceilings = CeilingsOf(run: run);

            return new WorldCountersBackendCeilings(
                Backend: run.Backend,
                Ceilings: [.. ceilings.Where(predicate: IsShared)],
                Devices: [new WorldCountersDeviceCeilings(
                    Ceilings: [.. ceilings.Where(predicate: static ceiling => !IsShared(ceiling: ceiling))],
                    Device: run.Device
                )]
            );
        })],
        Height: ((report.Runs.Count > 0) ? report.Runs[0].Height : 0),
        Script: report.Script,
        Width: ((report.Runs.Count > 0) ? report.Runs[0].Width : 0),
        Workload: report.Workload
    );
    /// <summary>Merges a fresh record into the ceilings already recorded: each backend's shared ceilings and the resolution
    /// become the record's, the record's device replaces that device's record in place or joins after the others, and
    /// every other device's record is kept as it was. A fresh required zero whose count another retained device owns moves
    /// from the shared ceilings to the fresh device records; deterministic ceilings remain shared.</summary>
    /// <param name="recorded">The fresh record (<see cref="Record"/>).</param>
    /// <param name="existing">The ceilings already recorded.</param>
    /// <param name="merged">The merged ceilings, or <see langword="null"/> when the method returns
    /// <see langword="false"/>.</param>
    /// <param name="reason">Why the two cannot be merged, or empty.</param>
    /// <returns><see langword="true"/> when the record merges: the same workload and script, and no ceiling the record
    /// must share across devices that another device's record holds as its own reading.</returns>
    public static bool TryMerge(WorldCountersCeilings recorded, WorldCountersCeilings existing, [NotNullWhen(returnValue: true)] out WorldCountersCeilings? merged, out string reason) {
        merged = null;

        if (
            !string.Equals(a: recorded.Workload, b: existing.Workload, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: recorded.Script, b: existing.Script, comparisonType: StringComparison.Ordinal)
        ) {
            reason = $"the ceilings hold {existing.Workload} run by {existing.Script}, not {recorded.Workload} run by {recorded.Script}; record another workload into a ceilings file of its own";

            return false;
        }

        var backends = new List<WorldCountersBackendCeilings>(collection: existing.Backends);

        foreach (var backend in recorded.Backends) {
            var index = backends.FindIndex(match: candidate => string.Equals(a: candidate.Backend, b: backend.Backend, comparisonType: StringComparison.Ordinal));

            if (index < 0) {
                backends.Add(item: backend);

                continue;
            }

            var devices = new List<WorldCountersDeviceCeilings>(collection: backends[index].Devices);

            foreach (var device in backend.Devices) {
                var at = devices.FindIndex(match: candidate => IsSameDevice(left: candidate.Device, right: device.Device));

                if (at < 0) {
                    devices.Add(item: device);
                } else {
                    devices[at] = device;
                }
            }

            var shared = backend.Ceilings.Select(selector: static ceiling => KeyOf(ceiling: ceiling)).ToHashSet();
            var requiredZeros = backend.Ceilings.Where(predicate: static ceiling => ((ceiling.Class == WorkClass.PerBackendDeterministic) && (ceiling.Ceiling == 0L)))
                .Select(selector: static ceiling => KeyOf(ceiling: ceiling)).ToHashSet();
            var scoped = new HashSet<(string Node, string? Pass, string? Detail, string Kind)>();

            foreach (var other in devices.Where(predicate: other => !backend.Devices.Any(predicate: device => IsSameDevice(left: device.Device, right: other.Device)))) {
                foreach (var held in other.Ceilings.Where(predicate: ceiling => shared.Contains(item: KeyOf(ceiling: ceiling)))) {
                    if (requiredZeros.Contains(item: KeyOf(ceiling: held))) {
                        _ = scoped.Add(item: KeyOf(ceiling: held));

                        continue;
                    }

                    reason = $"{backend.Backend}: {Where(ceiling: held)} is a ceiling every device shares in this record and {Describe(device: other.Device)}'s own reading in the ledger; record that device again on it";

                    return false;
                }
            }

            if (scoped.Count > 0) {
                var zeros = backend.Ceilings.Where(predicate: ceiling => scoped.Contains(item: KeyOf(ceiling: ceiling))).ToArray();

                foreach (var device in backend.Devices) {
                    var at = devices.FindIndex(match: candidate => IsSameDevice(left: candidate.Device, right: device.Device));

                    devices[at] = device with { Ceilings = [.. device.Ceilings, .. zeros] };
                }
            }

            backends[index] = backend with {
                Ceilings = [.. backend.Ceilings.Where(predicate: ceiling => !scoped.Contains(item: KeyOf(ceiling: ceiling)))],
                Devices = devices,
            };
        }

        merged = recorded with { Backends = backends };
        reason = string.Empty;

        return true;
    }
    /// <summary>Holds a report to ceilings, backend by backend, each run against its backend's shared ceilings and its own
    /// device's record. Every recorded ceiling must have been measured: its count read, of the class it was recorded as, or
    /// its pass present and not executed, which reads zero; and every ceiled count read must have a ceiling. A run on a
    /// device with no record fails by name, and is still held to the shared ceilings.</summary>
    /// <param name="report">The report.</param>
    /// <param name="ceilings">The ceilings.</param>
    /// <returns>The failures and the notes; no failure when every expectation was measured and every count is within its
    /// ceiling.</returns>
    public static Verdict Check(WorldCountersReport report, WorldCountersCeilings ceilings) {
        var failures = new List<string>();
        var notes = new List<string>();

        if (
            !string.Equals(a: report.Workload, b: ceilings.Workload, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: report.Script, b: ceilings.Script, comparisonType: StringComparison.Ordinal)
        ) {
            failures.Add(item: $"the ceilings hold {ceilings.Workload} run by {ceilings.Script}, not {report.Workload} run by {report.Script}");

            return new Verdict(
                Failures: failures,
                Notes: notes
            );
        }

        foreach (var recorded in ceilings.Backends) {
            if (!report.Runs.Any(predicate: run => string.Equals(a: run.Backend, b: recorded.Backend, comparisonType: StringComparison.Ordinal))) {
                failures.Add(item: $"{recorded.Backend}: the ceilings were recorded for the backend, which the run did not measure");
            }
        }

        foreach (var run in report.Runs) {
            var recorded = ceilings.Backends.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Backend, b: run.Backend, comparisonType: StringComparison.Ordinal));

            if (recorded is null) {
                failures.Add(item: $"{run.Backend}: no ceilings were recorded for the backend");

                continue;
            }
            if ((ceilings.Width, ceilings.Height) != (run.Width, run.Height)) {
                failures.Add(item: $"{run.Backend}: the ceilings were recorded at {ceilings.Width}x{ceilings.Height}, the run is {run.Width}x{run.Height}");

                continue;
            }

            var device = recorded.Devices.FirstOrDefault(predicate: candidate => IsSameDevice(left: candidate.Device, right: run.Device));

            if (device is null) {
                failures.Add(item: $"{run.Backend}: no ceilings recorded for {Describe(device: run.Device)}; run puck counters --record on it");
            } else if (device.Device.DriverVersionRaw != run.Device.DriverVersionRaw) {
                notes.Add(item: $"{run.Backend}: the ceilings for {Describe(device: run.Device)} were recorded under driver {DriverOf(device: device.Device)}, this run's driver is {DriverOf(device: run.Device)}");
            }

            var held = ((device is null)
                ? recorded.Ceilings
                : recorded.Ceilings.Concat(second: device.Ceilings).ToArray()
            );
            var measured = new Dictionary<(string Node, string? Pass, string? Detail, string Kind), WorldCount>();
            var passes = run.Passes.ToDictionary(
                elementSelector: static pass => pass.State,
                keySelector: static pass => (pass.Node, pass.Label)
            );

            foreach (var count in run.Counts.Where(predicate: static count => (
                string.Equals(a: count.Source, b: GpuWorkReport.Section, comparisonType: StringComparison.Ordinal) &&
                (count.Node is not null) &&
                SubmissionKindsByName.ContainsKey(key: count.Kind)
            ))) {
                measured[(count.Node!, count.Pass, count.Detail, count.Kind)] = count;
            }

            foreach (var ceiling in held) {
                var where = $"{run.Backend}: {Where(ceiling: ceiling)}";

                if (!measured.Remove(
                    key: KeyOf(ceiling: ceiling),
                    value: out var count
                )) {
                    // A pass that did not execute reads zero, which any ceiling holds.
                    if (
                        (ceiling.Pass is null) ||
                        !passes.TryGetValue(key: (ceiling.Node, ceiling.Pass), value: out var state) ||
                        (state == GpuPassState.Executed) ||
                        ((ceiling.Detail is not null) && !run.Passes.Single(predicate: pass => ((pass.Node == ceiling.Node) && (pass.Label == ceiling.Pass))).Details.Contains(value: ceiling.Detail))
                    ) {
                        failures.Add(item: $"{where} was recorded but not measured");
                    }

                    continue;
                }
                if (count.Class != ceiling.Class) {
                    failures.Add(item: $"{where} was measured as {EnumWireName<WorkClass>.Of(value: count.Class)}");

                    continue;
                }
                if ((ceiling.Ceiling == 0L) && (count.Value != 0L)) {
                    failures.Add(item: $"{where} breaks its required zero: reads {Spell(value: count.Value)}");
                } else if (count.Value > ceiling.Ceiling) {
                    failures.Add(item: $"{where} is over its ceiling: reads {Spell(value: count.Value)}, ceiling {Spell(value: ceiling.Ceiling)}");
                }
            }

            // On a device with no record, its own readings are covered by the one line naming the device.
            foreach (var count in measured.Values.Where(predicate: count => (IsCeiled(count: count) && ((device is not null) || IsShared(
                recorded: count.Class,
                kind: SubmissionKindsByName[count.Kind], ceiling: count.Value
            ))))) {
                failures.Add(item: $"{run.Backend}: {EnumWireName<WorkClass>.Of(value: count.Class)} kind={count.Kind} pass={(count.Pass ?? Outside)} detail={(count.Detail ?? "-")} node={count.Node} reads {Spell(value: count.Value)} with no ceiling recorded");
            }
            foreach (var pass in run.Passes.Where(predicate: static pass => (pass.State != GpuPassState.Executed))) {
                foreach (var detail in pass.Details) {
                    if (!held.Any(predicate: ceiling => ((ceiling.Node == pass.Node) && (ceiling.Pass == pass.Label) && (ceiling.Detail == detail)))) {
                        failures.Add(item: $"{run.Backend}: pass={pass.Label} detail={detail} node={pass.Node} has no ceiling recorded");
                    }
                }
            }
        }

        return new Verdict(
            Failures: failures,
            Notes: notes
        );
    }
    /// <summary>Reads a ceilings document, refusing one that is not a well-formed <c>puck.counters.ceilings.v1</c>
    /// document: every ceiling of a GPU submission kind, each shared ceiling
    /// a deterministic count's or a required zero and no device's ceiling deterministic, each device of its backend and recorded
    /// once, and no ceiling both shared and a device's own.</summary>
    /// <param name="path">The document's path.</param>
    /// <param name="ceilings">The ceilings, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the file is not a ceilings document, or empty.</param>
    /// <returns><see langword="true"/> when the file is a ceilings document.</returns>
    public static bool TryRead(string path, [NotNullWhen(returnValue: true)] out WorldCountersCeilings? ceilings, out string reason) {
        ceilings = null;

        try {
            ceilings = JsonSerializer.Deserialize(
                jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings,
                utf8Json: File.ReadAllBytes(path: path)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"unreadable: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        } catch (JsonException exception) {
            reason = $"not a {WorldCountersCeilings.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (ceilings is null) {
            reason = $"not a {WorldCountersCeilings.SchemaVersion} document: the document is null";

            return false;
        }
        if (!string.Equals(
            a: ceilings.Schema,
            b: WorldCountersCeilings.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"a foreign document: its schema is '{ceilings.Schema}', not '{WorldCountersCeilings.SchemaVersion}'";
            ceilings = null;

            return false;
        }

        reason = Malformation(ceilings: ceilings);

        if (reason.Length > 0) {
            ceilings = null;

            return false;
        }

        return true;
    }
    /// <summary>Atomically replaces a ceilings document with indented UTF-8 JSON ending in one line feed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="ceilings">The ceilings.</param>
    public static void Write(string path, WorldCountersCeilings ceilings) {
        AtomicFile.WriteAllText(
            contents: $"{JsonSerializer.Serialize(value: ceilings, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)}\n",
            path: path
        );
    }

    // Every ceiled count's reading, then a zero for each submission kind of every pass that did not execute, in report order.
    private static WorldCountCeiling[] CeilingsOf(WorldCountersRun run) => [
        .. run.Counts.Where(predicate: IsCeiled).Select(selector: static count => new WorldCountCeiling(
            Ceiling: count.Value,
            Detail: count.Detail,
            Class: count.Class,
            Kind: count.Kind,
            Node: count.Node!,
            Pass: count.Pass
        )),
        .. run.Passes.Where(predicate: static pass => (pass.State != GpuPassState.Executed)).SelectMany(selector: static pass => new string?[] { null }.Concat(second: pass.Details).SelectMany(selector: detail => GpuWork.SubmissionKinds.ToArray().Select(selector: kind => {
            var recorded = CountersReading.ClassIn(kind: kind.Class, pass: pass.Class);

            return new WorldCountCeiling(
                Ceiling: 0L,
                Detail: detail,
                Class: recorded,
                Kind: kind.Name,
                Node: pass.Node,
                Pass: pass.Label
            );
        }))),
    ];
    private static string Describe(GpuDeviceIdentity device) {
        var builder = new StringBuilder().Append(value: device.AdapterName).Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" (vendor=0x{device.VendorId:x4} device=0x{device.DeviceId:x4}"
        );

        if (device.DriverId != 0U) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" driver.id={device.DriverId}");
        }

        return builder.Append(value: ')').ToString();
    }
    private static string DriverOf(GpuDeviceIdentity device) => ((device.DriverVersion.Length > 0)
        ? device.DriverVersion
        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"0x{device.DriverVersionRaw:x}")
    );
    private static (string Node, string? Pass, string? Detail, string Kind) KeyOf(WorldCountCeiling ceiling) =>
        (ceiling.Node, ceiling.Pass, ceiling.Detail, ceiling.Kind);
    // Why a read document is not a well-formed ledger, or empty.
    private static string Malformation(WorldCountersCeilings ceilings) {
        foreach (var backend in ceilings.Backends) {
            var shared = new HashSet<(string Node, string? Pass, string? Detail, string Kind)>();

            foreach (var ceiling in backend.Ceilings) {
                if (Malformed(backend: backend.Backend, ceiling: ceiling) is { Length: > 0 } malformed) {
                    return malformed;
                }
                if (!IsShared(ceiling: ceiling)) {
                    return $"a malformed ceiling: {backend.Backend} {Where(ceiling: ceiling)} is one device's reading among the ceilings every device shares";
                }

                _ = shared.Add(item: KeyOf(ceiling: ceiling));
            }

            for (var index = 0; (index < backend.Devices.Count); index++) {
                var device = backend.Devices[index];

                if (!string.Equals(a: device.Device.Backend, b: backend.Backend, comparisonType: StringComparison.Ordinal)) {
                    return $"a malformed device record: {Describe(device: device.Device)} ran on {device.Device.Backend}, recorded under {backend.Backend}";
                }
                if (backend.Devices.Take(count: index).Any(predicate: earlier => IsSameDevice(left: earlier.Device, right: device.Device))) {
                    return $"a malformed device record: {backend.Backend} records {Describe(device: device.Device)} twice";
                }

                foreach (var ceiling in device.Ceilings) {
                    if (Malformed(backend: backend.Backend, ceiling: ceiling) is { Length: > 0 } malformed) {
                        return malformed;
                    }
                    if (ceiling.Class == WorkClass.Deterministic) {
                        return $"a malformed ceiling: {backend.Backend} {Where(ceiling: ceiling)} is a ceiling every device shares, recorded as {Describe(device: device.Device)}'s own";
                    }
                    if (shared.Contains(item: KeyOf(ceiling: ceiling))) {
                        return $"a malformed ceiling: {backend.Backend} {Where(ceiling: ceiling)} is both a shared ceiling and {Describe(device: device.Device)}'s own";
                    }
                }
            }
        }

        return string.Empty;
    }
    private static string Malformed(string backend, WorldCountCeiling ceiling) {
        if (!SubmissionKindsByName.ContainsKey(key: ceiling.Kind)) {
            return $"a malformed ceiling: {backend} kind={ceiling.Kind} is not a GPU submission kind";
        }

        return string.Empty;
    }
    private static string Spell(long value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
    private static string Where(WorldCountCeiling ceiling) =>
        $"{EnumWireName<WorkClass>.Of(value: ceiling.Class)} kind={ceiling.Kind} pass={(ceiling.Pass ?? Outside)} detail={(ceiling.Detail ?? "-")} node={ceiling.Node}";
}
