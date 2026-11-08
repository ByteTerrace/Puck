using Puck.Abstractions;
using Puck.World.Embeddings;

namespace Puck.Cli.Worlds.Tests;

internal static class InstalledExtensions {
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Install() => CliExtensions.Install(extensions: PuckExtensionSet.Compose(extensions: [new WorldEmbeddingsExtension()]));
}
