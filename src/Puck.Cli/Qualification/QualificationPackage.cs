using System.Reflection.PortableExecutable;

namespace Puck.Cli.Qualification;

/// <summary>
/// The package side of qualification: proving a directory is the producer-built package the profile describes,
/// installing a clean copy of it for one run, and the search path a matrix leg's World process gets.
/// </summary>
internal static class QualificationPackage {
    private const string PathVariable = "PATH";

    /// <summary>Checks that a directory is a package in the profile's publish mode: its entry assembly exists and, for
    /// <see cref="ReleasePublishMode.ReadyToRun"/>, carries a ReadyToRun header, which a plain build's IL-only output
    /// never does.</summary>
    /// <param name="directory">The package directory.</param>
    /// <param name="publish">The profile's publish section.</param>
    /// <param name="entry">The entry assembly's full path, or empty when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the directory is not the package the profile describes, or empty.</param>
    /// <returns><see langword="true"/> when the directory is such a package.</returns>
    public static bool TryVerify(string directory, ReleasePublish publish, out string entry, out string reason) {
        entry = string.Empty;

        if (!Directory.Exists(path: directory)) {
            reason = "no such directory";

            return false;
        }

        var path = Path.Combine(
            path1: directory,
            path2: publish.EntryAssembly
        );

        if (!File.Exists(path: path)) {
            reason = $"the package has no entry assembly {publish.EntryAssembly}";

            return false;
        }

        try {
            using var stream = File.OpenRead(path: path);
            using var reader = new PEReader(peStream: stream);
            var header = reader.PEHeaders.CorHeader;

            if (header is null) {
                reason = $"{publish.EntryAssembly} is not a managed assembly";

                return false;
            }
            if (header.ManagedNativeHeaderDirectory.Size == 0) {
                reason = $"{publish.EntryAssembly} carries no ReadyToRun header, so the directory is not a {publish.Mode} publish (a plain build's IL-only output, perhaps)";

                return false;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or BadImageFormatException)) {
            reason = $"{publish.EntryAssembly} cannot be read as a portable executable: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        entry = Path.GetFullPath(path: path);
        reason = string.Empty;

        return true;
    }
    /// <summary>Installs a clean copy of a package: every file, byte for byte, in the same layout.</summary>
    /// <param name="package">The package directory.</param>
    /// <param name="install">The empty or missing directory the copy goes into.</param>
    /// <returns>The number of files copied.</returns>
    public static int Install(string package, string install) {
        var source = Path.GetFullPath(path: package);
        var files = 0;

        foreach (var file in Directory.EnumerateFiles(
            path: source,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            var target = Path.Combine(
                path1: install,
                path2: Path.GetRelativePath(
                    path: file,
                    relativeTo: source
                )
            );

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(
                destFileName: target,
                overwrite: false,
                sourceFileName: file
            );
            files++;
        }

        return files;
    }
    /// <summary>Returns the arguments that give a World on <paramref name="backend"/> the validation layer the profile
    /// asks for: <c>--debug-layers</c> when the profile lists the backend under <c>debugLayers</c>, and nothing
    /// otherwise.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="backend">The backend the World runs on.</param>
    /// <returns>The arguments to append to the World's command line.</returns>
    public static string[] DebugLayerArguments(ReleaseProfile profile, string backend) => (profile.DebugLayers.Contains(value: backend)
        ? [WorldOffscreenLeg.DebugLayersFlag]
        : []
    );
    /// <summary>Returns the environment a matrix leg's World gets over the run's own: under
    /// <see cref="ReleaseCompilerDiscovery.None"/>, the search path with every directory holding <c>dxc</c> removed, so a
    /// world that asks for a compile finds no compiler; under <see cref="ReleaseCompilerDiscovery.Path"/>, nothing
    /// changed.</summary>
    /// <param name="compiler">The profile's compiler discovery.</param>
    /// <param name="searchPath">The run's own search path.</param>
    /// <param name="holdsCompiler">Whether a search-path directory holds the compiler.</param>
    /// <returns>The entries to apply; empty when nothing changes.</returns>
    public static IReadOnlyDictionary<string, string?> LegEnvironment(ReleaseCompilerDiscovery compiler, string? searchPath, Func<string, bool> holdsCompiler) {
        if (compiler == ReleaseCompilerDiscovery.Path) {
            return new Dictionary<string, string?>(comparer: StringComparer.Ordinal);
        }

        var kept = (searchPath ?? string.Empty).Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: Path.PathSeparator
        ).Where(predicate: directory => !holdsCompiler(directory.Trim(trimChar: '"')));

        return new Dictionary<string, string?>(comparer: StringComparer.Ordinal) {
            [PathVariable] = string.Join(
                separator: Path.PathSeparator,
                values: kept
            ),
        };
    }
    /// <summary>Indicates whether a directory holds the shader compiler a World run would find on its search path.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns><see langword="true"/> when <c>dxc</c> or <c>dxc.exe</c> is in it.</returns>
    public static bool HoldsCompiler(string directory) {
        try {
            var tool = Path.Combine(
                path1: directory,
                path2: Puck.Shaders.ShaderCompiler.DxcTool
            );

            return (File.Exists(path: tool) || File.Exists(path: $"{tool}.exe"));
        } catch (ArgumentException) {
            return false;
        }
    }
}
