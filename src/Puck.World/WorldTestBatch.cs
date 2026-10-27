using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.World;

/// <summary>Amortizes process and container startup for scheduled tests. Every entry goes through the normal boot
/// and disposes its isolated run scope before the next entry starts.</summary>
internal static class WorldTestBatch {
    private sealed record Leg(string World, string Directory, string Script, int ExitAfterSeconds);

    public static async Task<int> RunAsync(string path, WorldTestHost host, Func<string[], string?, Task<int>> boot) {
        var legs = (JsonSerializer.Deserialize<Leg[]>(json: File.ReadAllText(path: path))
            ?? throw new InvalidDataException(message: "A test batch must contain its legs."));
        var output = Console.Out;
        var error = Console.Error;
        var failed = false;

        foreach (var leg in legs) {
            Directory.CreateDirectory(path: leg.Directory);
            File.WriteAllText(path: Path.Combine(path1: leg.Directory, path2: "started"), contents: string.Empty);
            output.WriteLine(value: $"[test.batch: begin {leg.Directory}]");
            output.Flush();
            using var transcript = new StreamWriter(path: Path.Combine(path1: leg.Directory, path2: "stdout.log"), append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var errors = new StreamWriter(path: Path.Combine(path1: leg.Directory, path2: "stderr.log"), append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.SetOut(newOut: TextWriter.Synchronized(writer: new Tee(progress: output, transcript: transcript)));
            Console.SetError(newError: TextWriter.Synchronized(writer: errors));
            var exit = 1;
            var boots = host.Boots;

            try {
                exit = await boot([
                    "--devices", "none", "--world", leg.World, "--headless", "true", "--unpaced", "true",
                    "--exit-after-seconds", leg.ExitAfterSeconds.ToString(provider: CultureInfo.InvariantCulture),
                    "--state-dir", Path.Combine(path1: leg.Directory, path2: "state"),
                    "--schedule-dir", Path.Combine(path1: leg.Directory, path2: "out"),
                ], leg.Script);
            } finally {
                Console.Out.Flush();
                Console.Error.Flush();
                Console.SetOut(newOut: output);
                Console.SetError(newError: error);
                File.WriteAllText(path: Path.Combine(path1: leg.Directory, path2: "exit-code"), contents: exit.ToString(provider: CultureInfo.InvariantCulture));
                File.WriteAllText(path: Path.Combine(path1: leg.Directory, path2: "host-boots"), contents: (host.Boots - boots).ToString(provider: CultureInfo.InvariantCulture));
            }
            failed |= (exit != 0);
        }
        return (failed ? 1 : 0);
    }

    // The parent's existing progress watchdog sees the normal world.wait replies, while every leg keeps its own
    // transcript. The host installs a fresh HostLog over this writer and never retains a previous host's log.
    private sealed class Tee(TextWriter transcript, TextWriter progress) : TextWriter {
        public override Encoding Encoding => transcript.Encoding;

        public override void Write(char value) { transcript.Write(value: value); progress.Write(value: value); }
        public override void Write(string? value) { transcript.Write(value: value); progress.Write(value: value); }
        public override void WriteLine(string? value) { transcript.WriteLine(value: value); progress.WriteLine(value: value); }
        public override void Flush() { transcript.Flush(); progress.Flush(); }
    }
}
