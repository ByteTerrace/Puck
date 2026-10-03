using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Puck.Assets;

namespace Puck.Cli.Determinism;

/// <summary><c>puck determinism</c>: records what a manifest's scenarios hash to on this host, and compares two such
/// records, so the same world and input are shown to give the same state on every host that records them.</summary>
internal static class DeterminismCommand {
    private const string CompareVerb = "determinism compare";
    private const string RecordVerb = "determinism record";

    private static int Record(string manifestPath, string output) {
        if (!DeterminismManifest.TryLoad(
            error: out var manifestError,
            manifest: out var manifest,
            path: manifestPath
        )) {
            return CliExit.Refuse(verb: RecordVerb, what: manifestPath, why: manifestError);
        }

        var clock = Stopwatch.StartNew();

        if (!DeterminismRecorder.TryRecord(
            error: out var recordError,
            manifest: manifest!,
            stream: out var stream
        )) {
            return CliExit.Refuse(verb: RecordVerb, what: manifestPath, why: recordError);
        }

        clock.Stop();

        var full = Path.GetFullPath(path: output);

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
        AtomicFile.WriteAllBytes(
            bytes: Encoding.UTF8.GetBytes(s: stream!.Render()),
            path: full
        );

        var ticks = stream.Scenarios.Sum(selector: static scenario => ((long)scenario.Ticks.Count));
        var hashes = stream.Scenarios.Sum(selector: static scenario => (scenario.Documents.Count + (((long)scenario.Ticks.Count) * DeterminismStream.Components.Count)));

        Console.Out.WriteLine(value: $"determinism: recorded {stream.Scenarios.Count} scenario(s), {ticks} tick(s), {hashes} hash(es) into {CliPaths.ToDisplay(fullPath: full)} in {clock.Elapsed.TotalSeconds.ToString(format: "F1", provider: CultureInfo.InvariantCulture)}s.");

        return CliExit.Success;
    }
    private static int Compare(string leftPath, string rightPath) {
        var streams = new DeterminismStream[2];
        var paths = new[] { leftPath, rightPath };

        for (var index = 0; (index < 2); index++) {
            string text;

            try {
                text = File.ReadAllText(path: paths[index]);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                return CliExit.Refuse(verb: CompareVerb, what: paths[index], why: exception.Message);
            }

            if (!DeterminismStream.TryParse(
                error: out var parseError,
                stream: out var stream,
                text: text
            )) {
                return CliExit.Refuse(verb: CompareVerb, what: paths[index], why: parseError);
            }

            streams[index] = stream!;
        }

        if (!DeterminismComparison.TryCompare(
            comparison: out var comparison,
            left: streams[0],
            refusal: out var refusal,
            right: streams[1]
        )) {
            return CliExit.Refuse(verb: CompareVerb, what: $"{leftPath} and {rightPath}", why: refusal);
        }

        foreach (var divergence in comparison!.Divergences) {
            Console.Out.WriteLine(value: $"determinism: DIVERGED {divergence}");
        }

        Console.Out.WriteLine(value: $"determinism: compared {comparison.Scenarios} scenario(s), {comparison.Ticks} tick(s), {comparison.Hashes} hash(es); {comparison.Divergences.Count} divergence(s).");

        return ((comparison.Divergences.Count == 0)
            ? CliExit.Success
            : CliExit.Failed);
    }
    private static Command CreateRecord() {
        var manifestArgument = new Argument<string>(name: "manifest") { Description = "A puck.determinism.manifest.v1 file, such as tests/Puck.Determinism/determinism.json." };
        var outputOption = CliOptions.Output(description: "The stream file to write.", required: true);
        var command = new Command(
            description: "Record what each scenario of a manifest hashes to on this host.",
            name: "record"
        ) { manifestArgument, outputOption };

        command.Detail(detail: """
              Boots each scenario's world in this process the way the game boots it, joins its seats,
              and steps it for its ticks, submitting its held intents. The stream it writes
              (puck.determinism.stream.v1) holds, per scenario, the document-level hashes (the world's
              fingerprint, the compiled world's definition hash and catalog fingerprint, every asset
              row's and creation's canonical hash, every creation's bake key) and, per tick, each
              authoritative state component's hash, the population pose hash and the authoritative
              state hash the replay tape verifies. Nothing in the stream depends on the host, so two
              hosts that agree write the same bytes.

              Exit codes: 0 recorded; 2 the manifest or a scenario could not be read or run.
            """);
        command.SetAction(action: parseResult => Record(
            manifestPath: parseResult.GetRequiredValue(argument: manifestArgument),
            output: parseResult.GetRequiredValue(option: outputOption)
        ));

        return command;
    }
    private static Command CreateCompare() {
        var leftArgument = new Argument<string>(name: "left") { Description = "One stream." };
        var rightArgument = new Argument<string>(name: "right") { Description = "The other stream, recorded from the same manifest." };
        var command = new Command(
            description: "Compare two determinism streams and name the first divergence of each scenario.",
            name: "compare"
        ) { leftArgument, rightArgument };

        command.Detail(detail: """
              Compares two streams of one manifest, scenario by scenario: its document hashes in
              order, then its ticks in order until the first divergence, named by tick and by the
              first component of that tick's vector that differs (the components come before the
              aggregate hashes, so the name is the system that split). What follows a divergence is
              its consequence and is not reported. Prints what it counted: scenarios, ticks and hashes
              compared, and divergences.

              Exit codes: 0 no divergence; 1 a divergence; 2 a stream that cannot be read, of another
              version, or of another manifest, scenario list or tick count.
            """);
        command.SetAction(action: parseResult => Compare(
            leftPath: parseResult.GetRequiredValue(argument: leftArgument),
            rightPath: parseResult.GetRequiredValue(argument: rightArgument)
        ));

        return command;
    }

    /// <summary>Creates <c>puck determinism</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var command = new Command(
            description: "Record and compare what scenarios hash to, to show the simulation is the same on every host.",
            name: "determinism"
        );

        command.Subcommands.Add(item: CreateRecord());
        command.Subcommands.Add(item: CreateCompare());

        return command;
    }
}
