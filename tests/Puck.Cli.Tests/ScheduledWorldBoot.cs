using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Boots one authored test world through the real <c>Puck.World</c> executable, headless, with a piped
/// script — the door a law uses when it needs the host's own behaviour rather than <c>puck test</c>'s report.</summary>
/// <remarks>The artifact is the repository's own Release output: this test project already depends on that build,
/// so a law pays for no second one.</remarks>
internal static class ScheduledWorldBoot {
    /// <summary>Boots the world headless and returns the captured process result. The script's own <c>quit</c> ends the
    /// run; no wall-clock exit or kill ceiling decides it, and cancelling the test kills the child.</summary>
    /// <param name="world">The test world's file name under <c>tests/Puck.World.Verdicts</c>.</param>
    /// <param name="legDirectory">The directory the leg's state (and, when armed, its output) lives under.</param>
    /// <param name="script">The piped stdin script, newline-separated and ending in <c>quit</c>.</param>
    /// <param name="scheduleDirectory">The <c>--schedule-dir</c> to arm the schedule with, or
    /// <see langword="null"/> to leave the section inert.</param>
    /// <returns>The captured run, which exited 0.</returns>
    public static CliProcessResult Boot(string world, string legDirectory, string script, string? scheduleDirectory = null) {
        var run = Run(
            legDirectory: legDirectory,
            scheduleDirectory: scheduleDirectory,
            script: script,
            world: world
        );

        Assert.True(
            condition: (run.ExitCode == 0),
            userMessage: $"the {world} boot exited {run.ExitCode} rather than on its script's quit:{Environment.NewLine}{run.Stderr}"
        );
        return run;
    }
    /// <summary>Boots the world headless and returns the captured process result, whatever it exited with.</summary>
    /// <param name="world">The test world's file name under <c>tests/Puck.World.Verdicts</c>.</param>
    /// <param name="legDirectory">The directory the leg's state (and, when armed, its output) lives under.</param>
    /// <param name="script">The piped stdin script, newline-separated and ending in <c>quit</c>.</param>
    /// <param name="scheduleDirectory">The <c>--schedule-dir</c> to arm the schedule with, or
    /// <see langword="null"/> to leave the section inert.</param>
    /// <param name="options">Further host options, such as <c>--listen</c>, appended to the boot's own.</param>
    /// <returns>The captured run.</returns>
    public static CliProcessResult Run(string world, string legDirectory, string script, string? scheduleDirectory = null, IReadOnlyList<string>? options = null) {
        var arguments = new List<string> {
            Artifact(),
            "--world", WorldPath(world: world),
            "--headless", "true",
            "--exit-after-seconds", "0",
            "--state-dir", Path.Combine(
                path1: legDirectory,
                path2: "state"
            ),
        };

        if (scheduleDirectory is { }) {
            arguments.Add(item: "--schedule-dir");
            arguments.Add(item: scheduleDirectory);
        }
        if (options is { }) {
            arguments.AddRange(collection: options);
        }

        _ = Directory.CreateDirectory(path: legDirectory);

        return CliProcess.RunCaptured(
            arguments: arguments,
            fileName: "dotnet",
            input: script,
            timeout: Timeout.InfiniteTimeSpan,
            cancellationToken: TestContext.Current.CancellationToken
        );
    }
    /// <summary>Returns a fresh, disposable leg root under the machine's temporary root — the caller builds the
    /// actual leg directory under it with <see cref="TemporaryDirectory.PathOf"/> and disposes the root once the
    /// boot it drove is done (a headless <c>Puck.World</c> process writes real files under it, one leg per boot).</summary>
    public static TemporaryDirectory Leg() => new(prefix: "puck-boot-");
    /// <summary>Reads the schedule manifest a leg wrote.</summary>
    /// <param name="scheduleDirectory">The armed output directory.</param>
    /// <returns>The manifest document.</returns>
    public static JsonObject Manifest(string scheduleDirectory) => ((JsonNode.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(
        path1: scheduleDirectory,
        path2: Puck.World.WorldScheduleSection.ManifestFileName
    ))) as JsonObject) ?? throw new JsonException(message: "the leg wrote a non-object schedule manifest"));
    /// <summary>Returns the path of a test world under <c>tests/Puck.World.Verdicts</c>.</summary>
    /// <param name="world">The document's file name.</param>
    /// <returns>The rooted path.</returns>
    public static string WorldPath(string world) => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "tests",
        path3: "Puck.World.Verdicts",
        path4: world
    );

    private static string Artifact() => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net10.0/Puck.World.dll"
    );
    // The checkout root, found the way every repository tool finds it.
}
