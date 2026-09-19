using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Cli.Tests;

/// <summary>Boots one authored test world through the real <c>Puck.World</c> executable, headless, with a piped
/// script — the door a law uses when it needs the host's own behaviour rather than <c>puck test</c>'s report.</summary>
/// <remarks>The artifact is the repository's own Release output: this test project already depends on that build,
/// so a law pays for no second one.</remarks>
internal static class ScheduledWorldBoot {
    /// <summary>Boots the world headless and returns the captured process result.</summary>
    /// <param name="world">The test world's file name under <c>tests/Puck.World.Verdicts</c>.</param>
    /// <param name="legDirectory">The directory the leg's state (and, when armed, its output) lives under.</param>
    /// <param name="script">The piped stdin script, newline-separated.</param>
    /// <param name="scheduleDirectory">The <c>--schedule-dir</c> to arm the schedule with, or
    /// <see langword="null"/> to leave the section inert.</param>
    /// <returns>The captured run.</returns>
    public static CliProcessResult Boot(string world, string legDirectory, string script, string? scheduleDirectory = null) {
        var arguments = new List<string> {
            Artifact(),
            "--world", WorldPath(world: world),
            "--headless", "true",
            "--exit-after-seconds", "40",
            "--state-dir", Path.Combine(
                path1: legDirectory,
                path2: "state"
            ),
        };

        if (scheduleDirectory is { }) {
            arguments.Add(item: "--schedule-dir");
            arguments.Add(item: scheduleDirectory);
        }

        _ = Directory.CreateDirectory(path: legDirectory);

        return CliProcess.RunCaptured(
            arguments: arguments,
            fileName: "dotnet",
            input: script,
            timeout: TimeSpan.FromSeconds(value: 120)
        );
    }
    /// <summary>Returns a fresh leg directory under the machine's temporary root.</summary>
    /// <param name="name">A short label for the leg.</param>
    /// <returns>The directory path, not yet created.</returns>
    public static string Leg(string name) => Path.Combine(
        path1: Path.GetTempPath(),
        path2: ("puck-boot-" + Guid.NewGuid().ToString(format: "N")),
        path3: name
    );
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
        path1: Root(),
        path2: "tests",
        path3: "Puck.World.Verdicts",
        path4: world
    );

    private static string Artifact() => Path.Combine(
        path1: Root(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net10.0/Puck.World.dll"
    );
    // The checkout root: ascend from the test assembly until the solution file appears.
    private static string Root() {
        for (var directory = new DirectoryInfo(path: AppContext.BaseDirectory); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(message: "the repository root (the directory holding Puck.slnx) was not found above the test assembly");
    }
}
