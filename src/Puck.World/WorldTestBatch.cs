using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.Confinement;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A worker process boots its scenarios one after another, each in a fresh native host.</summary>
internal sealed class WorldTestBatch {
    // Workers is the --workers count the leg boots with (absent, the host's own); Record writes the leg's per-tick
    // authority hashes and crossing log beside its exports, which a worker-count or oracle comparison reads.
    // VerifyHash checks each recorded tick's incremental hash against a full recompute during the authored run.
    // Disclosure makes the leg one world of a noninterference pair (WorldDisclosureRun), which writes what its observed
    // principals received beside the exports.
    private sealed record Leg(string World, string Directory, int ExitAfterSeconds, bool Reproduce = false, string? CacheDirectory = null, int? Workers = null, bool Record = false,
        bool VerifyHash = false, WorldDisclosureLeg? Disclosure = null) {
        public bool Probed => (Reproduce || Record);
    }

    private readonly Leg[] m_legs;
    private readonly Output m_output;
    private readonly Output m_error;

    private IServiceProvider m_services = null!;
    private TextCommandSession m_control = null!;
    private readonly List<(string Name, WorldReplayProbe Probe)> m_probes = [];
    private readonly List<SnapshotLog> m_snapshots = [];
    private readonly HashSet<WorldInstance> m_observed = [];

    private bool m_observing;
    private int m_index;
    private bool m_failed;
    // Set when the worker itself could not report or open a leg, as opposed to a leg whose own verdict failed.
    private bool m_aborted;

    private WorldTestHost m_host = null!;

    private int m_reportedBoots;

    private Timer? m_legDeadline;
    private string? m_legFailure;
    private WorldDisclosureRun? m_disclosure;
    private CrossingLog m_crossings = null!;

    private string m_workingDirectory = string.Empty;

    private long m_legStarted;
    private long m_runStarted;
    private long m_bootTicks;
    private long m_armTicks;
    private long m_authoredTicks;
    private long m_firstTickTicks;

    private string WorkingWorld => Path.Combine(path1: m_workingDirectory, path2: Path.GetFileName(path: m_legs[m_index].World));

    private WorldTestBatch(Leg[] legs, Output output, Output error) { m_legs = legs; m_output = output; m_error = error; }

    public static async Task<int> RunAsync(string path, WorldTestHost host, Func<string[], string?, Task<int>> boot) {
        var legs = (JsonSerializer.Deserialize<Leg[]>(File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: "Missing test legs."));

        if (legs.Length == 0) { return 0; }
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new Output(progress: previousOut);
        using var error = new Output(progress: null);
        var batch = new WorldTestBatch(error: error, legs: legs, output: output) { m_host = host };

        batch.m_workingDirectory = Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: legs[0].World)!, path2: "..",
            path3: ("worker-" + Path.GetFileName(path: Path.GetDirectoryName(path: legs[0].Directory)))));
        var stateDirectory = (batch.m_workingDirectory + "-state");

        ConfinedDirectory.RemoveTree(path: stateDirectory);
        Console.SetOut(newOut: TextWriter.Synchronized(writer: output));
        Console.SetError(newError: TextWriter.Synchronized(writer: error));
        host.Initialize = batch.Initialize;
        try {
            var exit = 0;

            while (batch.m_index < legs.Length) {
                batch.OpenLeg();
                ConfinedDirectory.RemoveTree(path: stateDirectory);
                var first = legs[batch.m_index];
                var startingIndex = batch.m_index;

                exit = await boot([
                "--devices", "none", "--world", batch.WorkingWorld, "--headless", "true", "--unpaced", "true",
                "--exit-after-seconds", "0",
                "--state-dir", stateDirectory,
                .. ((first.CacheDirectory is { } cache) ? new[] { "--cache-dir", cache } : []),
                .. ((first.Workers is { } workers) ? new[] { "--workers", workers.ToString(provider: CultureInfo.InvariantCulture) } : []),
                "--schedule-dir", Path.Combine(path1: first.Directory, path2: "out"),
            ], null);
                batch.m_legDeadline?.Dispose();
                batch.m_control?.Dispose();
                if ((exit != 0) || batch.m_aborted || (batch.m_index == startingIndex)) { break; }
            }

            return (((exit == 0) && !batch.m_failed && (batch.m_index == legs.Length)) ? 0 : 1);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            try { previousError.WriteLine(value: $"[test.batch: {exception}]"); } catch (Exception) { }
            return 1;
        } finally {
            foreach (var (_, probe) in batch.m_probes) { probe.Dispose(); }
            foreach (var snapshots in batch.m_snapshots) { snapshots.Dispose(); }
            batch.m_legDeadline?.Dispose();
            batch.m_control?.Dispose();
            Console.Out.Flush(); Console.Error.Flush();
            Console.SetOut(newOut: previousOut); Console.SetError(newError: previousError);
        }
    }

    private T Get<T>() where T : notnull => m_services.GetRequiredService<T>();
    private void Initialize(IHost host) {
        m_services = host.Services;
        m_control = Get<TextCommandSource>().CreateSession(Principal.Console);
        Get<WorldTestRun>().Exported += FreezeLeg;
        Get<WorldTestRun>().FirstTick += () => m_firstTickTicks = (Stopwatch.GetTimestamp() - m_runStarted);
        Get<WorldTestRun>().Progress += tick => Console.WriteLine(value: $"[test.batch: tick {tick}]");
        Get<WorldInstanceHost>().Admitted += Observe;
        var server = Get<WorldServer>();

        m_crossings = new CrossingLog(tick: () => (server.NextInputTick - 1UL));
        _ = Get<WorldInstanceHost>().AttachNarrationSink(sink: m_crossings);
        StartLeg();
    }
    private void OpenLeg() {
        m_legStarted = Stopwatch.GetTimestamp();
        var leg = m_legs[m_index];

        Directory.CreateDirectory(path: m_workingDirectory);
        foreach (var file in Directory.EnumerateFiles(path: m_workingDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*")) { File.Delete(path: file); }
        var sourceDirectory = Path.GetDirectoryName(path: leg.World)!;

        foreach (var file in Directory.EnumerateFiles(path: sourceDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var target = Path.Combine(path1: m_workingDirectory, path2: Path.GetRelativePath(path: file, relativeTo: sourceDirectory));

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(file, target, overwrite: true);
        }
        Directory.CreateDirectory(path: leg.Directory);
        File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "process-id"), Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture));
        m_output.Open(path: Path.Combine(path1: leg.Directory, path2: "stdout.log"));
        m_error.Open(path: Path.Combine(path1: leg.Directory, path2: "stderr.log"));
        File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "started"), string.Empty);
        m_output.WriteLine(value: $"[test.batch: begin {leg.Directory}]");
        m_output.Flush();
    }
    private void Observe(WorldInstance instance) {
        if (m_observing && m_legs[m_index].Probed && m_observed.Add(item: instance)) {
            try {
                var probe = Get<WorldReplayTape>().ObserveReplay(instance: instance);

                probe.VerifiesIncrementalHash = m_legs[m_index].VerifyHash;
                m_probes.Add(item: (instance.Name, probe));
            } catch (InvalidOperationException exception) { m_legFailure = $"cannot capture '{instance.Name}': {exception.Message}"; }
            if (m_legs[m_index].Record) { m_snapshots.Add(item: SnapshotLog.Attach(name: instance.Name, server: instance.Server)); }
        }
    }
    private void StartLeg() {
        var started = Stopwatch.GetTimestamp();

        m_bootTicks = (started - m_legStarted);
        var leg = m_legs[m_index];

        m_legFailure = null;
        m_observing = false;
        m_observed.Clear();
        var index = m_index;
        var lifetime = Get<IHostApplicationLifetime>();

        m_legDeadline?.Dispose();
        m_legDeadline = new Timer(_ => {
            if (Volatile.Read(location: ref m_index) == index) { lifetime.StopApplication(); }
        }, null, TimeSpan.FromSeconds(seconds: leg.ExitAfterSeconds), Timeout.InfiniteTimeSpan);
        Get<WorldDefinitionSource>().SourcePath = WorkingWorld;
        Get<WorldTestRun>().Reset(WorkingWorld, Path.Combine(path1: leg.Directory, path2: "out"));
        m_disclosure = null;
        if (leg.Disclosure is { } disclosure) {
            try { m_disclosure = WorldDisclosureRun.Start(leg: disclosure, services: m_services); } catch (InvalidOperationException exception) { m_legFailure = exception.Message; }
        }
        m_observing = true;
        m_crossings.Recording = leg.Record;
        foreach (var name in Get<WorldInstanceHost>().Names) {
            if (Get<WorldInstanceHost>().TryGet(instance: out var row, name: name) && (row is not null)) { Observe(instance: row); }
        }
        Get<WorldTestRun>().ArmInstances();
        m_runStarted = Stopwatch.GetTimestamp();
        m_armTicks = (m_runStarted - started);
    }
    private void FreezeLeg() {
        m_authoredTicks = (Stopwatch.GetTimestamp() - m_runStarted);
        m_observing = false;
        m_crossings.Recording = false;
        foreach (var (_, probe) in m_probes) { probe.Dispose(); }
        foreach (var snapshots in m_snapshots) { snapshots.Dispose(); }
        try { m_disclosure?.Write(directory: Path.Combine(path1: m_legs[m_index].Directory, path2: "out")); } catch (IOException exception) { m_legFailure = $"cannot write the disclosure transcript: {exception.Message}"; }
        m_disclosure = null;
        try { ArchiveStateFiles(directory: m_legs[m_index].Directory); } catch (IOException exception) { m_legFailure = $"cannot archive authored state: {exception.Message}"; }
        _ = m_control.InvokeAsync(operation: CompleteLegSafely);
    }
    // The session owns the returned task. No reporting failure may escape it and strand an unpaced worker
    // after its index has advanced beyond the old deadline.
    private bool CompleteLegSafely() {
        try { return CompleteLeg(); } catch (Exception exception) {
            m_failed = true;
            m_aborted = true;
            try { Console.Error.WriteLine(value: $"[test.batch: {exception}]"); } catch (Exception) { } finally { Get<IHostApplicationLifetime>().StopApplication(); }
            return true;
        }
    }
    private bool CompleteLeg() {
        var leg = m_legs[m_index];
        var replayStarted = Stopwatch.GetTimestamp();

        m_observing = false;
        try {
            var results = new List<object>();

            if (m_legFailure is not null) { throw new InvalidOperationException(message: m_legFailure); }
            foreach (var (name, probe) in m_probes) {
                int? divergence = null;

                if (leg.Reproduce) {
                    try {
                        Console.WriteLine(value: $"[test.batch: tick replay {name} 0]");
                        divergence = probe.Verify(progress: tick => {
                            if ((tick % 120) == 0) { Console.WriteLine(value: $"[test.batch: tick replay {name} {tick}]"); }
                        });
                    } catch (Exception exception) { throw new InvalidOperationException($"reproduction of '{name}' failed: {exception.Message}", exception); }
                }
                if (probe.StaleDigest is { } stale) { throw new InvalidOperationException(message: $"'{name}': {stale}"); }

                results.Add(item: new { world = name, divergence, hashes = probe.Hashes });
                if (divergence >= 0) { throw new InvalidOperationException(message: $"reproduction diverged in '{name}' at recorded tick {divergence}"); }
            }
            // One row per authority: the per-tick hashes the probe recorded during the authored run, and under
            // --reproduce the replay's first divergent tick (-1 when it reproduced).
            if (leg.Probed) { File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "reproduction.json"), JsonSerializer.Serialize(results)); }
            if (leg.Record) {
                File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "crossings.json"), m_crossings.Serialize());
                File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "snapshots.json"), JsonSerializer.Serialize(m_snapshots.Select(selector: static log => new { world = log.Name, hashes = log.Hashes })));
            }
            var replayTicks = (Stopwatch.GetTimestamp() - replayStarted);
            var run = Get<WorldTestRun>();

            static double Milliseconds(long ticks) => ((1000.0 * ticks) / Stopwatch.Frequency);
            var server = Get<WorldServer>();

            File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "timing.json"), JsonSerializer.Serialize(new {
                // The boot authority's incremental hash work: rows refolded against the arena's rows, and the time its
                // always-on digest sample took.
                arenaRows = server.Arena.Layout.RowCount,
                arenaRowFolds = server.Arena.RowDigestFolds,
                digestCheckMilliseconds = Milliseconds(ticks: server.Persistence.DigestCheckTicks),
                authorityTicks = run.AuthorityTicks,
                bootMilliseconds = Milliseconds(ticks: m_bootTicks),
                armMilliseconds = Milliseconds(ticks: m_armTicks),
                authoredMilliseconds = Milliseconds(ticks: m_authoredTicks),
                firstTickMilliseconds = Milliseconds(ticks: m_firstTickTicks),
                exportMilliseconds = Milliseconds(ticks: run.ExportTicks),
                replayMilliseconds = Milliseconds(ticks: replayTicks),
                otherMilliseconds = Milliseconds(ticks: (((((Stopwatch.GetTimestamp() - m_legStarted) - m_bootTicks) - m_armTicks) - m_authoredTicks) - replayTicks)),
            }));
            var boots = (m_host.Boots - m_reportedBoots);

            m_reportedBoots = m_host.Boots;
            File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "host-boots"), boots.ToString(provider: CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "exit-code"), "0");
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[test.batch: {exception}]");
            try { File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "exit-code"), "1"); } catch { }
            m_failed = true;
        }
        m_probes.Clear();
        m_snapshots.Clear();
        m_output.Flush(); m_error.Flush();
        m_index++;
        Get<IHostApplicationLifetime>().StopApplication();
        return true;
    }
    private void ArchiveStateFiles(string directory) {
        var root = Get<WorldStateRoot>().FullPath;

        if (!Directory.Exists(path: root)) { return; }
        foreach (var file in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var target = Path.Combine(path1: directory, path2: "state", path3: Path.GetRelativePath(path: file, relativeTo: root));

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // The run's crossing log: every crossing line the instance host narrates (a portal or adjacency entry queuing a
    // transfer, its refusal, a departure and arrival, an in-place crossing), stamped with the booted authority's
    // completed tick. The lines carry transfer ids, cohorts and poses, and none carries a wall-clock value.
    private sealed class CrossingLog(Func<ulong> tick) : IWorldNarrationSink {
        private readonly Lock m_gate = new();
        private readonly List<object> m_entries = [];

        public volatile bool Recording;

        public void Narrate(in WorldNarration narration) {
            if (!Recording || narration.Channel is not ("world.transfer" or "world.portal" or "world.adjacency" or "world.continuum")) { return; }
            var entry = new { tick = tick(), channel = narration.Channel, text = narration.Text };

            lock (m_gate) { m_entries.Add(item: entry); }
        }
        public string Serialize() { lock (m_gate) { return JsonSerializer.Serialize(value: m_entries); } }
    }

    // One authority's delivered snapshots: a hash per delivery of its tick and every entity entry in delivered order,
    // so two runs agree only when they delivered the same entries in the same positions. The attach primer is not
    // recorded; floats hash by their bits.
    private sealed class SnapshotLog : IClientSink, IDisposable {
        private readonly List<ulong> m_hashes = [];
        private IDisposable? m_lease;
        private volatile bool m_recording;

        private SnapshotLog(string name) { Name = name; }

        public string Name { get; }
        public IReadOnlyList<ulong> Hashes => m_hashes;

        public static SnapshotLog Attach(string name, WorldServer server) {
            var log = new SnapshotLog(name: name);

            log.m_lease = server.AttachSink(sink: log);
            log.m_recording = true;
            return log;
        }
        public void Dispose() {
            m_recording = false;
            Interlocked.Exchange(location1: ref m_lease, value: null)?.Dispose();
        }
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            if (!m_recording) { return; }
            var hash = Puck.Maths.Fnv1aHash.Create();

            hash.Add(value: snapshot.Tick);
            hash.Add(value: (uint)snapshot.Entries.Length);
            foreach (ref readonly var entry in snapshot.Entries.Span) {
                hash.Add(value: (uint)entry.Index);
                Add(hash: ref hash, value: entry.Position);
                Add(hash: ref hash, value: entry.Orientation);
                Add(hash: ref hash, value: entry.BodyColor);
                hash.Add(value: (byte)(entry.Active ? 1 : 0));
                hash.Add(value: entry.Kit);
                hash.Add(value: entry.Look);
                hash.Add(value: entry.CatalogRig);
                hash.Add(value: (uint)entry.Continuity.Kind);
                hash.Add(value: BitConverter.SingleToUInt32Bits(value: entry.Continuity.Seconds));
                if (entry.Continuity.Carried is { } carried) {
                    Add(hash: ref hash, value: carried.Rotation);
                    Add(hash: ref hash, value: carried.Translation);
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: carried.Scale));
                }
                hash.Add(value: (uint)entry.Generation);
                hash.Add(value: entry.PlacementId);
                hash.Add(value: BitConverter.SingleToUInt32Bits(value: entry.Heading));
                hash.Add(value: (ulong)entry.Facts);
                foreach (var cell in entry.State.Span) { hash.Add(value: cell); }
            }
            m_hashes.Add(item: hash.Value);
        }
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) { }

        private static void Add(ref Puck.Maths.Fnv1aHash hash, System.Numerics.Vector3 value) {
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.X));
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.Y));
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.Z));
        }
        private static void Add(ref Puck.Maths.Fnv1aHash hash, System.Numerics.Quaternion value) {
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.X));
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.Y));
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.Z));
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: value.W));
        }
    }

    // HostLog and logging providers retain this writer for the native host's entire lifetime.
    private sealed class Output(TextWriter? progress) : TextWriter {
        private StreamWriter? m_transcript;

        private readonly Lock m_gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public void Open(string path) {
            lock (m_gate) {
                var replacement = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var previous = m_transcript;

                m_transcript = replacement;
                previous?.Dispose();
            }
        }
        public override void Write(char value) { lock (m_gate) { m_transcript?.Write(value: value); progress?.Write(value: value); } }
        public override void Write(string? value) { lock (m_gate) { m_transcript?.Write(value: value); progress?.Write(value: value); } }
        public override void WriteLine(string? value) { lock (m_gate) { m_transcript?.WriteLine(value: value); progress?.WriteLine(value: value); } }
        public override void Flush() { lock (m_gate) { m_transcript?.Flush(); progress?.Flush(); } }

        protected override void Dispose(bool disposing) { lock (m_gate) { if (disposing) { m_transcript?.Dispose(); m_transcript = null; } base.Dispose(disposing: disposing); } }
    }
}
