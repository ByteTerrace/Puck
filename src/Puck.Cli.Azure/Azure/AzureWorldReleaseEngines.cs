using Puck.Assets;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>Installs a deployment's host release on this runner through the installer and signed manifest the
    /// deployment retained, exactly as its machine installs it, so qualification runs the engine a machine runs. The
    /// channel keeps every release's objects and manifests, so a retained release stays installable for rollback without
    /// any retention step.</summary>
    /// <param name="deployment">The deployment whose engine is installed.</param>
    /// <param name="token">Cancels the install.</param>
    /// <returns>The install root holding the engine.</returns>
    private static async Task<string> InstallWorldReleaseEngineAsync(AzureWorldReleaseDeployment deployment, CancellationToken token) {
        var root = Path.GetFullPath(path: Path.Combine(path1: "artifacts/world-engines", path2: ContentPin.Parse(text: deployment.Engine).Hex));

        _ = await deployment.Configuration.Install.InstallAsync(cancellationToken: token, installRoot: root).ConfigureAwait(continueOnCapturedContext: false);
        return root;
    }
}
