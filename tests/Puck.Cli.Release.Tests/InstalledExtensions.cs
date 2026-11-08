using Puck.Abstractions;
using Puck.Cli.Azure;

namespace Puck.Cli.Release.Tests;

internal static class InstalledExtensions {
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Install() => CliExtensions.Install(extensions: PuckExtensionSet.Compose(extensions: [new AzureCliExtension()]));
}
