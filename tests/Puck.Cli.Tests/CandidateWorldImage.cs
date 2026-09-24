namespace Puck.Cli.Tests;

/// <summary>The locally built world-silo image the Docker release laws run against: <see cref="Tag"/>, built from the
/// checkout with <c>docker build --file src/Puck.World.Silo/Dockerfile --tag puck/world-silo:candidate .</c>. A law
/// that needs it skips by name when Docker or the image is absent.</summary>
internal static class CandidateWorldImage {
    /// <summary>The image tag the laws look for.</summary>
    public const string Tag = "puck/world-silo:candidate";

    /// <summary>Returns the candidate image's id, or <see langword="null"/> when Docker is absent or holds no image
    /// tagged <see cref="Tag"/>.</summary>
    /// <param name="cancellationToken">The test's cancellation.</param>
    /// <returns>The image id (<c>sha256:…</c>), or <see langword="null"/>.</returns>
    public static async Task<string?> TryResolveIdAsync(CancellationToken cancellationToken) {
        try {
            return (await CliProcess.RunCheckedAsync(
                arguments: ["image", "inspect", "--format", "{{.Id}}", Tag],
                cancellationToken: cancellationToken,
                capture: true,
                fileName: "docker",
                workingDirectory: Environment.CurrentDirectory
            )).Trim();
        } catch (Exception exception) when ((exception is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)) {
            return null;
        }
    }
}
