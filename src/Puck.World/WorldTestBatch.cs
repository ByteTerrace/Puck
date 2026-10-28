using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One native host per compatible batch; each scenario starts from its boot checkpoint.</summary>
internal sealed class WorldTestBatch {
    private sealed record Leg(string World, string Directory, int ExitAfterSeconds, bool Reproduce = false);

    private readonly Leg[] m_legs;
    private readonly Output m_output;
    private readonly Output m_error;

    private IServiceProvider m_services = null!;
    private WorldAuthorityCheckpoint m_boot = null!;
    private Dictionary<string, byte[]> m_bootFiles = [];
    private TextCommandSession m_control = null!;
    private readonly List<(string Name, WorldReplayProbe Probe)> m_probes = [];

    private int m_index;
    private bool m_failed;

    private WorldTestHost m_host = null!;

    private int m_reportedBoots;

    private string m_workingDirectory = string.Empty;

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
        batch.OpenLeg();
        var stateDirectory = Path.Combine(path1: batch.m_workingDirectory, path2: "state");

        if (Directory.Exists(path: stateDirectory)) { Directory.Delete(stateDirectory, recursive: true); }
        Console.SetOut(newOut: TextWriter.Synchronized(writer: output));
        Console.SetError(newError: TextWriter.Synchronized(writer: error));
        host.Initialize = batch.Initialize;
        try {
            var first = legs[0];
            var exit = await boot([
                "--devices", "none", "--world", batch.WorkingWorld, "--headless", "true", "--unpaced", "true",
                "--exit-after-seconds", legs.Sum(selector: leg => leg.ExitAfterSeconds).ToString(provider: CultureInfo.InvariantCulture),
                "--state-dir", Path.Combine(path1: batch.m_workingDirectory, path2: "state"),
                "--schedule-dir", Path.Combine(path1: first.Directory, path2: "out"),
            ], null);

            return (((exit == 0) && !batch.m_failed && (batch.m_index == legs.Length)) ? 0 : 1);
        } finally {
            foreach (var (_, probe) in batch.m_probes) { probe.Dispose(); }
            batch.m_control?.Dispose();
            Console.Out.Flush(); Console.Error.Flush();
            Console.SetOut(newOut: previousOut); Console.SetError(newError: previousError);
        }
    }

    private T Get<T>() where T : notnull => m_services.GetRequiredService<T>();
    private void Initialize(IHost host) {
        m_services = host.Services;
        var server = Get<WorldServer>();
        var instances = Get<WorldInstanceHost>();

        if (!server.TryCaptureCheckpoint(instances.CaptureRow(row: instances.Boot!), out var checkpoint, out var reason)) {
            throw new InvalidOperationException(message: $"Test boot cannot be checkpointed: {reason}");
        }
        m_boot = checkpoint!;
        instances.CaptureScenarioBoot();
        var stateRoot = Get<WorldStateRoot>().FullPath;

        m_bootFiles = (Directory.Exists(path: stateRoot) ? Directory.EnumerateFiles(path: stateRoot, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .ToDictionary(path => Path.GetRelativePath(path: path, relativeTo: stateRoot), File.ReadAllBytes) : []);
        m_control = Get<TextCommandSource>().CreateSession(Principal.Console);
        Get<WorldTestRun>().Exported += () => { _ = m_control.InvokeAsync(operation: CompleteLeg); };
        instances.Admitted += Observe;
        StartLeg();
    }
    private void OpenLeg() {
        var leg = m_legs[m_index];

        Directory.CreateDirectory(path: m_workingDirectory);
        foreach (var file in Directory.EnumerateFiles(path: Path.GetDirectoryName(path: leg.World)!)) {
            File.Copy(file, Path.Combine(path1: m_workingDirectory, path2: Path.GetFileName(path: file)), overwrite: true);
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
        if (m_legs[m_index].Reproduce && (instance.Tape is { } tape)) { m_probes.Add(item: (instance.Name, tape.ObserveReplay())); }
    }
    private void StartLeg() {
        var leg = m_legs[m_index];
        var server = Get<WorldServer>();
        var source = Get<WorldDefinitionSource>();

        source.SourcePath = WorkingWorld;
        if (!WorldDefinitionLoader.TryLoadForAdmission(File.ReadAllBytes(path: WorkingWorld), WorkingWorld, out var admission, out var reason,
            neighbours: server.Neighbours, catalog: server.Machines.ValidationCatalog, documentDirectory: m_workingDirectory)) {
            throw new InvalidOperationException(message: reason);
        }
        var definition = admission!.Definition;
        var checkpoint = server.Persistence.ScenarioCheckpoint(boot: m_boot, definition: definition);

        Get<InputRouter>().ResetTimeline();
        Get<Puck.World.Client.WorldClient>().ResetInputTimeline();
        Get<WorldHistory>().Off();
        Get<WorldInstanceHost>().RestoreScenario(checkpoint: checkpoint);
        RestoreStateFiles();
        Get<WorldTestRun>().Reset(WorkingWorld, Path.Combine(path1: leg.Directory, path2: "out"));
        foreach (var name in Get<WorldInstanceHost>().Names) {
            if (Get<WorldInstanceHost>().TryGet(instance: out var row, name: name) && (row is not null)) { Observe(instance: row); }
        }
        Get<WorldTestRun>().ArmInstances();
    }
    private bool CompleteLeg() {
        var leg = m_legs[m_index];

        try {
            var results = new List<object>();

            foreach (var (_, probe) in m_probes) { probe.Dispose(); }
            foreach (var (name, probe) in m_probes) {
                int divergence;

                try { divergence = probe.Verify(); } catch (Exception exception) { throw new InvalidOperationException($"reproduction of '{name}' failed: {exception.Message}", exception); }

                results.Add(item: new { world = name, divergence });
                if (divergence >= 0) { throw new InvalidOperationException(message: $"reproduction diverged in '{name}' at recorded tick {divergence}"); }
            }
            if (leg.Reproduce) { File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "reproduction.json"), JsonSerializer.Serialize(results)); }
            File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "exit-code"), "0");
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[test.batch: {exception}]");
            File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "exit-code"), "1");
            m_failed = true;
        }
        m_probes.Clear();
        ArchiveStateFiles(directory: leg.Directory);
        File.WriteAllText(Path.Combine(path1: leg.Directory, path2: "host-boots"), (m_host.Boots - m_reportedBoots).ToString(provider: CultureInfo.InvariantCulture));
        m_reportedBoots = m_host.Boots;
        m_output.Flush(); m_error.Flush();
        m_index++;
        if ((m_index == m_legs.Length) || m_failed) { Get<IHostApplicationLifetime>().StopApplication(); } else { OpenLeg(); StartLeg(); }
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

        public override Encoding Encoding => Encoding.UTF8;

        public void Open(string path) { m_transcript?.Dispose(); m_transcript = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); }
        public override void Write(char value) { m_transcript?.Write(value: value); progress?.Write(value: value); }
        public override void Write(string? value) { m_transcript?.Write(value: value); progress?.Write(value: value); }
        public override void WriteLine(string? value) { m_transcript?.WriteLine(value: value); progress?.WriteLine(value: value); }
        public override void Flush() { m_transcript?.Flush(); progress?.Flush(); }

        protected override void Dispose(bool disposing) { if (disposing) { m_transcript?.Dispose(); } base.Dispose(disposing: disposing); }
    }
}
