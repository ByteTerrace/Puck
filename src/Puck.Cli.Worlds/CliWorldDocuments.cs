using Puck.World;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli;

/// <summary>What the offline world verbs (<c>mesh</c> and <c>creation</c>) share: loading a world file the way a boot
/// would draw it, the shader extension a backend names, and where a bake store lives.</summary>
internal static class CliWorldDocuments {
    /// <summary>Loads a world file for inspection, reporting a failure on standard error.</summary>
    /// <param name="verb">The verb as it prefixes its messages, such as <c>creation stats</c>.</param>
    /// <param name="path">The world file.</param>
    /// <param name="definition">The loaded definition, when the load succeeded.</param>
    /// <param name="fullPath">The file's full path.</param>
    /// <returns><see langword="true"/> when the file loaded.</returns>
    public static bool TryLoad(string verb, string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string fullPath) {
        fullPath = Path.GetFullPath(path: path);
        definition = null;

        // A world's document parses masks and bindings through the Schema hooks, which the CLI installs per invocation.
        _ = CliWorldVocabulary.EnsureInstalled();

        // The document a boot would run is drawn before it is read; a cross-document claim needs its neighbours, which an
        // offline inspection has none of.
        if (!WorldDefinitionLoader.TryLoadFileForAdmission(admission: out var admission, contentHash: out _, documents: PuckDocumentComposer.Instance, path: fullPath, proveNeighbours: false, reason: out var reason)) {
            Console.Error.WriteLine(value: $"{verb}: '{fullPath}' failed to load — {reason}");

            return false;
        }

        definition = admission!.Definition;

        return true;
    }
    /// <summary>Names the compiled-shader extension of a backend, which identifies the build's bake.</summary>
    /// <param name="verb">The verb as it prefixes its messages.</param>
    /// <param name="backend">The <c>--backend</c> value.</param>
    /// <param name="extension">The extension, when the backend is known.</param>
    /// <returns><see langword="true"/> when the backend is <c>directx</c> or <c>vulkan</c>.</returns>
    public static bool TryExtension(string verb, string backend, out string extension) {
        extension = (backend switch {
            "directx" => ".dxil",
            "vulkan" => ".spv",
            _ => string.Empty,
        });

        if (extension.Length == 0) {
            Console.Error.WriteLine(value: $"{verb}: unknown --backend '{backend}'; expected directx or vulkan.");

            return false;
        }

        return true;
    }
    /// <summary>Resolves the directory of the baked-mesh store.</summary>
    /// <param name="cacheRoot">The <c>--cache-root</c> value, or <see langword="null"/> for the per-user store.</param>
    /// <returns>The store's full path.</returns>
    public static string MeshStore(string? cacheRoot) => Path.GetFullPath(path: ((cacheRoot is null)
        ? Puck.Abstractions.PuckUserDirectory.Resolve(name: "meshes")
        : Path.Join(path1: cacheRoot, path2: "meshes")));
}
