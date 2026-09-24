using System.Globalization;
using System.Text.Json;

namespace Puck.Cli.Automation;

/// <summary>An engine image as the container engine reports it: its immutable identifier and the registry digests it
/// is known by.</summary>
/// <param name="Id">The image identifier, such as <c>sha256:…</c>.</param>
/// <param name="RepoDigests">The <c>repository@sha256:…</c> references the image is known by.</param>
internal sealed record WorldReleaseQualificationImage(string Id, IReadOnlyList<string> RepoDigests);
/// <summary>The container engine a qualification leg's packaged engine runs in. An implementation throws when the
/// engine itself cannot inspect or start an image, which is a refusal; the exit code of the packaged command inside a
/// started container is returned, because a nonzero one is a leg that failed.</summary>
internal interface IWorldReleaseQualificationContainers {
    /// <summary>Runs <c>world release exercise</c> from <paramref name="image"/> over <paramref name="fixture"/> in an
    /// isolated container named <paramref name="container"/>.</summary>
    /// <param name="container">The container's unique name.</param>
    /// <param name="image">The immutable image identifier.</param>
    /// <param name="fixture">The leg's fixture directory, mounted read-write as the only host path.</param>
    /// <param name="steps">The exact continuation steps the exercise runs.</param>
    /// <param name="cancellationToken">The leg's deadline.</param>
    /// <returns>The exit code of the packaged command.</returns>
    Task<int> ExerciseAsync(string container, string image, string fixture, int steps, CancellationToken cancellationToken);
    /// <summary>Reads an image's identity.</summary>
    /// <param name="reference">The image reference.</param>
    /// <param name="cancellationToken">The run's cancellation.</param>
    /// <returns>The image's identity.</returns>
    Task<WorldReleaseQualificationImage> InspectAsync(string reference, CancellationToken cancellationToken);
    /// <summary>Removes <paramref name="container"/> if it still exists; best effort, and never throws for a
    /// container that is already gone.</summary>
    /// <param name="container">The container's unique name.</param>
    /// <returns>A task that completes when removal finished or was abandoned.</returns>
    Task RemoveAsync(string container);
}
/// <summary>Runs qualification legs in Docker: no network, a read-only root, no capabilities, bounded processes,
/// memory, and CPU, and one bind mount holding the leg's fixture copy.</summary>
/// <param name="clock">The clock the container removal bound runs on.</param>
internal sealed class WorldReleaseQualificationDocker(TimeProvider clock) : IWorldReleaseQualificationContainers {
    /// <summary>Gets the bound, on the runner's clock, on removing a leg's container.</summary>
    public static TimeSpan CleanupTimeout { get; } = TimeSpan.FromSeconds(seconds: 30);

    // docker run's own failures: the daemon refused (125), or the entrypoint could not be invoked (126) or found (127).
    // Every other code is the packaged command's.
    private static bool IsEngineFailure(int exitCode) => (exitCode is 125 or 126 or 127);

    public async Task<int> ExerciseAsync(string container, string image, string fixture, int steps, CancellationToken cancellationToken) {
        var run = await CliProcess.RunAsync(
            arguments: [
                "run", "--name", container, "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges", "--pids-limit", "256", "--memory", "4g", "--cpus", "2",
                "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m", "--mount", $"type=bind,source={fixture},target=/fixture",
                "--entrypoint", "dotnet", image, "/puck-cli/Puck.Cli.dll", "world", "release", "exercise", "/fixture",
                "--steps", steps.ToString(provider: CultureInfo.InvariantCulture),
            ],
            cancellationToken: cancellationToken,
            fileName: "docker",
            workingDirectory: Environment.CurrentDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (IsEngineFailure(exitCode: run.ExitCode)) {
            throw new InvalidOperationException(message: $"docker could not run the qualification container (exit code {run.ExitCode}). {run.Stderr}".TrimEnd());
        }

        return run.ExitCode;
    }
    public async Task<WorldReleaseQualificationImage> InspectAsync(string reference, CancellationToken cancellationToken) {
        var json = await CliProcess.RunCheckedAsync(
            arguments: ["image", "inspect", reference],
            cancellationToken: cancellationToken,
            capture: true,
            fileName: "docker",
            workingDirectory: Environment.CurrentDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);
        using var document = JsonDocument.Parse(json: json);
        var image = document.RootElement.EnumerateArray().Single();

        return new(
            Id: image.GetProperty(propertyName: "Id").GetString()!,
            RepoDigests: [.. image.GetProperty(propertyName: "RepoDigests").EnumerateArray().Select(selector: static digest => (digest.GetString() ?? string.Empty))]
        );
    }
    // Killing a Docker client does not stop its container, so removal runs after every leg, cancellation included.
    // Its exit code is not checked, and a removal that outlives CleanupTimeout is killed rather than holding the leg
    // open.
    public Task RemoveAsync(string container) => CliProcess.RunAsync(
        arguments: ["rm", "--force", container],
        clock: clock,
        fileName: "docker",
        timeout: CleanupTimeout
    );
}
