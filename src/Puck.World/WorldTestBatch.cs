using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A worker retains its native host while consecutive scenarios share its boot composition.</summary>
internal sealed class WorldTestBatch {
    private sealed record Leg(string World, string Directory, int ExitAfterSeconds, string BootShape, bool Reproduce = false, string? CacheDirectory = null);

    private readonly Leg[] m_legs;
    private readonly Output m_output;
    private readonly Output m_error;

    private IServiceProvider m_services = null!;
    private WorldAuthorityCheckpoint m_boot = null!;
    private Dictionary<string, byte[]> m_bootFiles = [];
    private TextCommandSession m_control = null!;
    private readonly List<(string Name, WorldReplayProbe Probe)> m_probes = [];
    private readonly HashSet<WorldInstance> m_observed = [];

    private bool m_observing;
    private int m_index;
    private bool m_failed;
    // Set when the worker itself could not report or open a leg, as opposed to a leg whose own verdict failed.
    private bool m_aborted;

    private WorldTestHost m_host = null!;

    private int m_reportedBoots;
    private bool m_reusable;

    private string m_bootShape = string.Empty;

    private Timer? m_legDeadline;
    private string? m_reproductionFailure;

    private string m_workingDirectory = string.Empty;

    private long m_legStarted;
    private long m_runStarted;
    private long m_bootTicks;
    private long m_restoreTicks;
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

        if (Directory.Exists(path: stateDirectory)) { Directory.Delete(stateDirectory, recursive: true); }
        Console.SetOut(newOut: TextWriter.Synchronized(writer: output));
        Console.SetError(newError: TextWriter.Synchronized(writer: error));
        host.Initialize = batch.Initialize;
        try {
            var exit = 0;

            while (batch.m_index < legs.Length) {
                batch.OpenLeg();
                if (Directory.Exists(path: stateDirectory)) { Directory.Delete(stateDirectory, recursive: true); }
                var first = legs[batch.m_index];
                var startingIndex = batch.m_index;

                exit = await boot([
                "--devices", "none", "--world", batch.WorkingWorld, "--headless", "true", "--unpaced", "true",
                "--exit-after-seconds", "0",
                "--state-dir", stateDirectory,
                .. ((first.CacheDirectory is { } cache) ? new[] { "--cache-dir", cache } : []),
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
            batch.m_legDeadline?.Dispose();
            batch.m_control?.Dispose();
            Console.Out.Flush(); Console.Error.Flush();
            Console.SetOut(newOut: previousOut); Console.SetError(newError: previousError);
        }
    }

    private T Get<T>() where T : notnull => m_services.GetRequiredService<T>();
    private void Initialize(IHost host) {
        m_services = host.Services;
        m_bootShape = m_legs[m_index].BootShape;
        var server = Get<WorldServer>();
        var instances = Get<WorldInstanceHost>();

        m_reusable = server.TryCaptureCheckpoint(instances.CaptureRow(row: instances.Boot!), out var checkpoint, out var reason);
        if (m_reusable) {
            m_boot = checkpoint!;
            try { instances.CaptureScenarioBoot(); } catch (InvalidOperationException exception) { m_reusable = false; reason = exception.Message; }
        }
        if (!m_reusable) { Console.Error.WriteLine(value: $"[test.batch: boot cannot be reused ({reason}); this scenario owns its native host]"); }
        var stateRoot = Get<WorldStateRoot>().FullPath;

        m_bootFiles = (Directory.Exists(path: stateRoot) ? Directory.EnumerateFiles(path: stateRoot, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .ToDictionary(path => Path.GetRelativePath(path: path, relativeTo: stateRoot), File.ReadAllBytes) : []);
        m_control = Get<TextCommandSource>().CreateSession(Principal.Console);
        Get<WorldTestRun>().Exported += FreezeLeg;
        Get<WorldTestRun>().FirstTick += () => m_firstTickTicks = (Stopwatch.GetTimestamp() - m_runStarted);
        Get<WorldTestRun>().Progress += tick => Console.WriteLine(value: $"[test.batch: tick {tick}]");
        instances.Admitted += Observe;
        StartLeg(freshHost: true);
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
        if (m_observing && m_legs[m_index].Reproduce && m_observed.Add(item: instance)) {
            try { m_probes.Add(item: (instance.Name, Get<WorldReplayTape>().ObserveReplay(instance: instance))); } catch (InvalidOperationException exception) { m_reproductionFailure = $"cannot capture '{instance.Name}': {exception.Message}"; }
        }
    }
    private void StartLeg(bool freshHost = false) {
        var started = Stopwatch.GetTimestamp();

        m_bootTicks = (freshHost ? (started - m_legStarted) : 0);
        var leg = m_legs[m_index];
        var server = Get<WorldServer>();
        var source = Get<WorldDefinitionSource>();

        m_reproductionFailure = null;
        m_observing = false;
        m_observed.Clear();
        var index = m_index;
        var lifetime = Get<IHostApplicationLifetime>();

        m_legDeadline?.Dispose();
        m_legDeadline = new Timer(_ => {
            if (Volatile.Read(location: ref m_index) == index) { lifetime.StopApplication(); }
        }, null, TimeSpan.FromSeconds(seconds: leg.ExitAfterSeconds), Timeout.InfiniteTimeSpan);
        source.SourcePath = WorkingWorld;
        if (m_reusable && !freshHost) {
            if (!WorldDefinitionLoader.TryLoadFileForAdmission(WorkingWorld, out var admission, out _, out var reason,
                catalog: server.Machines.ValidationCatalog)) {
                throw new InvalidOperationException(message: reason);
            }
            var checkpoint = server.Persistence.ScenarioCheckpoint(admission: admission!, boot: m_boot);

            Get<InputRouter>().ResetTimeline();
            Get<Puck.World.Client.WorldClient>().ResetInputTimeline();
            Get<WorldHistory>().Off();
            Get<WorldInstanceHost>().RestoreScenario(checkpoint: checkpoint);
            // Restoring a crossed seat's boot claim is not a live handoff. Its queued edge would admit input
            // before tick one, whereas a fresh host first admits it during ordinary post-step synchronization.
            Get<Puck.World.Client.WorldSeatAuthorityRouter>().DiscardPendingRouteChanges();
            RestoreStateFiles();
        }
        Get<WorldTestRun>().Reset(WorkingWorld, Path.Combine(path1: leg.Directory, path2: "out"));
        m_observing = true;
        foreach (var name in Get<WorldInstanceHost>().Names) {
            if (Get<WorldInstanceHost>().TryGet(instance: out var row, name: name) && (row is not null)) { Observe(instance: row); }
        }
        Get<WorldTestRun>().ArmInstances();
        m_runStarted = Stopwatch.GetTimestamp();
        m_restoreTicks = (m_runStarted - started);
    }
    private void FreezeLeg() {
        m_authoredTicks = (Stopwatch.GetTimestamp() - m_runStarted);
        m_observing = false;
        foreach (var (_, probe) in m_probes) { probe.Dispose(); }
        try { ArchiveStateFiles(directory: m_legs[m_index].Directory); } catch (IOException exception) { m_reproductionFailure = $"cannot archive authored state: {exception.Message}"; }
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

            if (m_reproductionFailure is not null) { throw new InvalidOperationException(message: m_reproductionFailure); }
            foreach (var (name, probe) in m_probes) {
                int divergence;

                try {
                    Console.WriteLine(value: $"[test.batch: tick replay {name} 0]");
                    divergence = probe.Verify(progress: tick => {
                        if ((tick % 120) == 0) { Console.WriteLine(value: $"[test.batch: tick replay {name} {tick}]"); }
                    });
                } catch (Exception exception) { throw new InvalidOperationException($"reproduction of '{name}' failed: {exception.Message}", exception); }

                results.Add(item: new { world = name, divergence, hashes = probe.Hashes });
                if (divergence >= 0) { throw new InvalidOperationException(message: $"reproduction diverged in '{name}' at recorded tick {divergence}"); }
            }
            if (leg.Reproduce) { File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "reproduction.json"), JsonSerializer.Serialize(results)); }
            var replayTicks = (Stopwatch.GetTimestamp() - replayStarted);
            var run = Get<WorldTestRun>();

            static double Milliseconds(long ticks) => ((1000.0 * ticks) / Stopwatch.Frequency);
            File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "timing.json"), JsonSerializer.Serialize(new {
                authorityTicks = run.AuthorityTicks,
                bootMilliseconds = Milliseconds(ticks: m_bootTicks),
                restoreMilliseconds = Milliseconds(ticks: m_restoreTicks),
                authoredMilliseconds = Milliseconds(ticks: m_authoredTicks),
                firstTickMilliseconds = Milliseconds(ticks: m_firstTickTicks),
                exportMilliseconds = Milliseconds(ticks: run.ExportTicks),
                replayMilliseconds = Milliseconds(ticks: replayTicks),
                otherMilliseconds = Milliseconds(ticks: (((((Stopwatch.GetTimestamp() - m_legStarted) - m_bootTicks) - m_restoreTicks) - m_authoredTicks) - replayTicks)),
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
        m_output.Flush(); m_error.Flush();
        m_index++;
        if ((m_index == m_legs.Length) || !m_reusable || (m_legs[m_index].BootShape != m_bootShape)) {
            Get<IHostApplicationLifetime>().StopApplication();
        } else {
            try { OpenLeg(); StartLeg(); } catch (Exception exception) {
                m_failed = true;
                m_aborted = true;
                try {
                    Console.Error.WriteLine(value: $"[test.batch: {exception}]");
                    m_error.Flush();
                } finally { Get<IHostApplicationLifetime>().StopApplication(); }
            }
        }
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
    private void RestoreStateFiles() {
        var root = Get<WorldStateRoot>().FullPath;

        Directory.CreateDirectory(path: root);
        foreach (var file in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")) { File.Delete(path: file); }
        foreach (var (relative, bytes) in m_bootFiles) {
            var target = Path.Combine(path1: root, path2: relative);

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.WriteAllBytes(bytes: bytes, path: target);
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
