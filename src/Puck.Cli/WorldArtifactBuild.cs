using System.ComponentModel;
using System.Globalization;

namespace Puck.Cli;

/// <summary>Builds the real <c>Puck.World</c> executable once, in Release, for a verb that boots it.</summary>
internal static class WorldArtifactBuild {
    /// <summary>Builds <c>src/Puck.World</c> and names the exact artifact a leg launches.</summary>
    /// <param name="verb">The calling verb's name, which prefixes the one progress line.</param>
    /// <param name="repositoryRoot">The repository root the project path resolves against.</param>
    /// <param name="outputDirectory">The directory the build writes into, so concurrent verbs never share an output;
    /// <see langword="null"/> builds in place, under the project's own <c>bin/Release</c>.</param>
    /// <param name="timeout">How long the build may run.</param>
    /// <param name="artifact">The path of <c>Puck.World.dll</c> the build was asked to produce.</param>
    /// <param name="build">The captured build process, or <see langword="null"/> when it could not start. A caller
    /// that keeps build logs writes them from here whether or not the build succeeded.</param>
    /// <param name="error">One line saying why the artifact is unavailable, or empty on success.</param>
    /// <returns><see langword="true"/> when the build exited 0 inside its budget and the artifact exists.</returns>
    public static bool TryBuild(string verb, string repositoryRoot, string? outputDirectory, TimeSpan timeout, out string artifact, out CliProcessResult? build, out string error) {
        var worldProject = Path.Combine(
            path1: repositoryRoot,
            path2: "src",
            path3: "Puck.World",
            path4: "Puck.World.csproj"
        );

        artifact = ((outputDirectory is null)
            ? Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "bin", "Release", "net10.0", "Puck.World.dll"])
            : Path.Combine(
                path1: outputDirectory,
                path2: "Puck.World.dll"
            )
        );
        build = null;

        List<string> arguments = ["build", worldProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"];

        if (outputDirectory is not null) {
            arguments.Add(item: "--output");
            arguments.Add(item: outputDirectory);
        }

        Console.WriteLine(value: $"{verb}: building Puck.World once (Release).");

        try {
            build = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: arguments,
                input: string.Empty,
                timeout: timeout
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or Win32Exception)) {
            error = $"could not start the Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (build.TimedOut) {
            error = $"the Puck.World build exceeded its {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget.";

            return false;
        }
        if (build.ExitCode != 0) {
            error = $"the Puck.World build exited {build.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}.";

            return false;
        }
        if (!File.Exists(path: artifact)) {
            error = $"the Puck.World build exited 0 but did not produce the exact artifact {artifact}.";

            return false;
        }

        error = string.Empty;

        return true;
    }
}
