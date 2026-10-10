using System.CommandLine;
using System.Xml.Linq;

namespace Puck.Cli.Shaders;

/// <summary>One SPIR-V module that differs between two trees.</summary>
/// <param name="Path">The file, relative to both roots, with forward slashes.</param>
/// <param name="Detail">How it differs: absent from one tree, or its first differing byte and both lengths.</param>
internal sealed record ShaderBytecodeDifference(string Path, string Detail);

/// <summary><c>puck shaders compare</c>: compares every compiled SPIR-V module in one tree with the same file in another,
/// byte for byte, so the DXC build on one host can be held to the build of the same commit on another. DXIL is no part
/// of it: Direct3D 12 alone reads DXIL, so only a Windows build compiles it (<c>build/Shaders.targets</c>), and a
/// <c>.dxil</c> file in either tree is ignored. With <c>--build</c> it first compiles the shaders of the checkout it runs
/// in, through the build's own <c>CompileShaders</c> target in every project that declares DXC shader items, into a
/// shader cache that starts empty, so both hosts really compile, with the same arguments. Exit 0 when every module
/// matches and both trees hold the same modules, 1 when any differs, is missing, or the build fails, 2 when a tree holds
/// no SPIR-V or cannot be read. <c>puck shaders collect</c> copies a checkout's SPIR-V into one tree at its repository
/// paths, which is how one host hands its build to another.</summary>
public static class CompareCommand {
    // The shader items build/Shaders.targets compiles with DXC; a Direct3D 11 kernel is compiled for cs_5_0 on Windows
    // alone, so it is no part of a cross-host comparison.
    private static readonly string[] DxcItems = ["VertexShaderSource", "FragmentShaderSource", "ComputeShaderSource"];

    // The one target every host builds and the comparison holds across hosts.
    private const string Extension = ".spv";

    // Directories that hold build output copies, published payloads, tooling or history rather than the bytecode the
    // build writes beside its sources.
    private static readonly string[] Skipped = ["artifacts", "bin", "obj", ".git", ".tmp", "node_modules"];

    private const string Verb = "shaders compare";

    private static bool IsSpirv(string path) => path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: Extension);

    /// <summary>Lists every compiled SPIR-V module under a root, skipping build output copies and tooling.</summary>
    /// <param name="root">The tree's root.</param>
    /// <returns>Each file's path relative to the root, with forward slashes, mapped to its full path.</returns>
    public static SortedDictionary<string, string> Spirv(string root) {
        var files = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var pending = new Stack<string>(collection: [Path.GetFullPath(path: root)]);

        while (pending.TryPop(result: out var directory)) {
            foreach (var child in Directory.EnumerateDirectories(path: directory)) {
                if (!Skipped.Contains(value: Path.GetFileName(path: child), comparer: StringComparer.OrdinalIgnoreCase)) {
                    pending.Push(item: child);
                }
            }
            foreach (var file in Directory.EnumerateFiles(path: directory).Where(predicate: IsSpirv)) {
                files[Path.GetRelativePath(path: file, relativeTo: root).Replace(newChar: '/', oldChar: '\\')] = file;
            }
        }

        return files;
    }

    /// <summary>Compares two trees' SPIR-V modules byte for byte.</summary>
    /// <param name="expected">The reference tree, such as another host's build.</param>
    /// <param name="actual">The tree held to it.</param>
    /// <returns>Every file that is absent from one tree or differs, in path order.</returns>
    internal static IReadOnlyList<ShaderBytecodeDifference> Compare(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual) {
        var differences = new List<ShaderBytecodeDifference>();

        foreach (var path in expected.Keys.Union(second: actual.Keys, comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)) {
            if (!actual.TryGetValue(key: path, value: out var actualPath)) {
                differences.Add(item: new ShaderBytecodeDifference(Detail: "is in the expected tree only", Path: path));

                continue;
            }
            if (!expected.TryGetValue(key: path, value: out var expectedPath)) {
                differences.Add(item: new ShaderBytecodeDifference(Detail: "is in the actual tree only", Path: path));

                continue;
            }

            var left = File.ReadAllBytes(path: expectedPath);
            var right = File.ReadAllBytes(path: actualPath);
            var common = left.AsSpan().CommonPrefixLength(other: right);

            if (
                (common != left.Length) ||
                (left.Length != right.Length)
            ) {
                differences.Add(item: new ShaderBytecodeDifference(
                    Detail: $"differs from byte {common} ({left.Length} bytes expected, {right.Length} actual)",
                    Path: path
                ));
            }
        }

        return differences;
    }

    /// <summary>Lists the projects whose build compiles shaders with DXC: every tracked project outside
    /// <c>experimental/</c> that declares a vertex, fragment or compute shader item.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="projects">The tracked project files, repository-relative with forward slashes.</param>
    /// <returns>The shader projects, repository-relative, in ordinal order.</returns>
    public static IReadOnlyList<string> ShaderProjects(string repositoryRoot, IEnumerable<string> projects) => [.. projects
        .Where(predicate: static project => !project.StartsWith(comparisonType: StringComparison.Ordinal, value: "experimental/"))
        .Where(predicate: project => XDocument.Load(uri: Path.Combine(path1: repositoryRoot, path2: project)).Descendants().Any(predicate: static element => DxcItems.Contains(value: element.Name.LocalName, comparer: StringComparer.Ordinal)))
        .Order(comparer: StringComparer.Ordinal)];

    // The build-only host every shader project's CompileShaders target runs.
    private const string ShaderBuildTool = "src/Puck.Shaders.Generator/Puck.Shaders.Generator.csproj";

    /// <summary>Refuses a directory <c>--build</c> would compile into unless it is absent or empty: a cache another run
    /// filled (on this host or, restored, on another) would answer with bytes this host's DXC never produced, and the
    /// comparison would prove nothing.</summary>
    /// <param name="cache">The directory.</param>
    /// <returns><see langword="null"/> when the directory may be compiled into, otherwise why not.</returns>
    public static string? RefuseCache(string cache) => ((Directory.Exists(path: cache) && Directory.EnumerateFileSystemEntries(path: cache).Any())
        ? "it is not empty, so it would answer compiles with bytes this host's DXC may never have produced; name an absent or empty directory."
        : (File.Exists(path: cache) ? "it is a file." : null));

    // Compiles the checkout's shaders through each shader project's own CompileShaders target, restoring first as
    // dotnet build does, since a fresh checkout on another host has restored nothing. Every output compiles into a cache
    // directory that starts empty (RefuseCache): a temporary one removed afterwards, or the caller's, kept so CI can save
    // this host's compiles once the comparison passes.
    private static bool TryBuild(string repositoryRoot, string? keptCache) {
        var listed = CliGit.Run(repositoryRoot, "ls-files", "--", "*.csproj");
        var projects = ShaderProjects(
            projects: listed.Stdout.Split(separator: '\n').Select(selector: static line => line.TrimEnd(trimChar: '\r')).Where(predicate: static line => (line.Length > 0)),
            repositoryRoot: repositoryRoot
        );
        var cache = (keptCache ?? Directory.CreateTempSubdirectory(prefix: "puck-shaders-compare-").FullName);

        try {
            foreach (var (project, target) in projects.Select(selector: static project => (project, "CompileShaders")).Prepend(element: (ShaderBuildTool, "Build"))) {
                Console.Out.WriteLine(value: ((target == "Build") ? $"{Verb}: building {project}" : $"{Verb}: compiling {project}"));

                var build = CliProcess.RunCaptured(
                    arguments: ["msbuild", project, "--disable-build-servers", "-restore", $"-t:{target}", "-p:Configuration=Release", $"-p:PuckShaderCacheDirectory={cache}", "-nologo", "-v:minimal"],
                    fileName: "dotnet",
                    input: string.Empty,
                    timeout: TimeSpan.FromMinutes(minutes: 30),
                    workingDirectory: repositoryRoot
                );

                if (build.ExitCode != 0) {
                    Console.Error.WriteLine(value: $"{Verb}: {project}'s {target} exited {build.ExitCode}.{Environment.NewLine}{build.Stdout}{build.Stderr}".TrimEnd());

                    return false;
                }
            }

            return true;
        } finally {
            try {
                if (keptCache is null) {
                    Directory.Delete(path: cache, recursive: true);
                }
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"{Verb}: could not remove the comparison's shader cache {cache}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }
        }
    }

    /// <summary>Compares two trees and reports every difference, one line each.</summary>
    /// <param name="expectedRoot">The reference tree.</param>
    /// <param name="actualRoot">The tree held to it.</param>
    /// <returns>0 when both hold the same SPIR-V, 1 when any module differs or is missing, 2 when either holds none or
    /// cannot be read.</returns>
    public static int Run(string expectedRoot, string actualRoot) {
        SortedDictionary<string, string> expected, actual;

        try {
            expected = Spirv(root: expectedRoot);
            actual = Spirv(root: actualRoot);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return CliExit.Refuse(
                verb: Verb,
                what: $"{expectedRoot} and {actualRoot}",
                why: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        }

        foreach (var (name, tree) in ((ReadOnlySpan<(string, SortedDictionary<string, string>)>)[("expected", expected), ("actual", actual)])) {
            if (tree.Count == 0) {
                return CliExit.Refuse(
                    verb: Verb,
                    what: $"the {name} tree",
                    why: "it holds no .spv file, so a comparison would prove nothing."
                );
            }
        }

        var differences = Compare(
            actual: actual,
            expected: expected
        );

        foreach (var difference in differences) {
            Console.Error.WriteLine(value: $"{Verb}: {difference.Path} {difference.Detail}.");
        }

        Console.Out.WriteLine(value: ((differences.Count == 0)
            ? $"{Verb}: all {expected.Count} SPIR-V modules match byte for byte."
            : $"{Verb}: {differences.Count} of {expected.Keys.Union(second: actual.Keys, comparer: StringComparer.Ordinal).Count()} SPIR-V modules differ or are missing."));

        return ((differences.Count == 0)
            ? CliExit.Success
            : CliExit.Failed);
    }
    /// <summary>Copies every compiled SPIR-V module under a root into a directory, each at its path relative to the root,
    /// so a host can hand its build's SPIR-V to another as one tree.</summary>
    /// <param name="root">The tree to collect from, such as the repository root after a build.</param>
    /// <param name="destination">The directory to copy into; created when absent.</param>
    /// <returns>0 when at least one file was copied, 2 when the tree holds no SPIR-V or cannot be read or
    /// written.</returns>
    public static int Collect(string root, string destination) {
        try {
            var files = Spirv(root: root);

            if (files.Count == 0) {
                return CliExit.Refuse(
                    verb: "shaders collect",
                    what: root,
                    why: "it holds no .spv file; build the shaders first."
                );
            }

            foreach (var (path, full) in files) {
                var target = Path.Combine(path1: destination, path2: path);

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
                File.Copy(destFileName: target, overwrite: true, sourceFileName: full);
            }

            Console.Out.WriteLine(value: $"shaders collect: copied {files.Count} SPIR-V modules into {destination}.");

            return CliExit.Success;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return CliExit.Refuse(
                verb: "shaders collect",
                what: destination,
                why: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        }
    }
    /// <summary>Creates <c>puck shaders collect</c>, which copies the checkout's SPIR-V into one tree that
    /// <c>puck shaders compare</c> reads on another host.</summary>
    /// <returns>The command.</returns>
    public static Command CreateCollect() {
        var destination = new Argument<string>(name: "directory") { Description = "The directory to copy into, outside the repository's walk (such as under artifacts/)." };
        var command = new Command(
            description: """
            Copy every compiled SPIR-V module into one directory at its repository path.

            Walks the checkout as puck shaders compare does, skipping artifacts, bin, obj, .git, .tmp
            and node_modules, so the collected tree lines up path for path with another host's checkout.
            DXIL is not collected: only a Windows build compiles it, so no other host compares it.
            CI collects the Windows build's SPIR-V into an artifact this way. Exit 0 copied, 2 the
            checkout holds no SPIR-V or the directory cannot be written.
            """,
            name: "collect"
        ) { destination };

        command.SetAction(action: result => (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)
            ? Collect(
                destination: Path.GetFullPath(path: result.GetRequiredValue(argument: destination)),
                root: repositoryRoot
            )
            : CliExit.Refused));

        return command;
    }
    public static Command Create() {
        var expected = new Argument<string>(name: "expected") { Description = "The reference tree, such as another host's build of the same commit." };
        var actual = new Argument<string?>(name: "actual") { Arity = ArgumentArity.ZeroOrOne, Description = "The tree held to it; the repository root when omitted." };
        var build = new Option<bool>("--build") { Description = "First compile the checkout's shaders through each shader project's CompileShaders target, into an empty shader cache." };
        var cache = new Option<string?>("--cache") { Description = "With --build, compile into this absent or empty directory and keep it, rather than a temporary cache removed afterwards." };
        var command = new Command(
            description: """
            Compare every compiled SPIR-V module with another tree's byte for byte.

            Walks both trees for .spv files, skipping artifacts, bin, obj, .git, .tmp and node_modules,
            and matches them by relative path: a module in one tree only, or one whose bytes differ,
            fails by name with its first differing byte. DXIL is no part of it: Direct3D 12 alone
            reads DXIL, so only a Windows build compiles it, and a .dxil file in either tree is ignored.
            With --build it first builds the shader build's host, then runs the build's own
            CompileShaders target (build/Shaders.targets) in every
            tracked project outside experimental/ that declares a vertex, fragment or compute shader
            item, with the dxc on the path and an empty shader cache, so a second host really compiles
            every output with exactly the arguments the first did. CI runs it on Linux against
            the Windows build's SPIR-V artifact, the P7 gate's cross-host leg. --cache names that
            cache, which must be absent or empty, and keeps it: CI saves it once the comparison
            passes, as the Linux shader cache the container builds restore.

            Exit 0 every module matches, 1 any differs or is missing or the build fails, 2 a tree holds
            no SPIR-V or cannot be read, or --cache is not empty or is given without --build.
            """,
            name: "compare"
        ) { expected, actual, build, cache };

        command.SetAction(action: result => {
            if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
                return CliExit.Refused;
            }

            var keptCache = ((result.GetValue(option: cache) is { } named) ? Path.GetFullPath(path: named) : null);

            if (keptCache is not null) {
                if (!result.GetValue(option: build)) {
                    return CliExit.Refuse(verb: Verb, what: "--cache", why: "it names the cache --build compiles into; add --build.");
                }
                if (RefuseCache(cache: keptCache) is { } why) {
                    return CliExit.Refuse(verb: Verb, what: keptCache, why: why);
                }
            }
            if (
                result.GetValue(option: build) &&
                !TryBuild(
                    keptCache: keptCache,
                    repositoryRoot: repositoryRoot
                )
            ) {
                return CliExit.Failed;
            }

            return Run(
                actualRoot: Path.GetFullPath(path: (result.GetValue(argument: actual) ?? repositoryRoot)),
                expectedRoot: Path.GetFullPath(path: result.GetRequiredValue(argument: expected))
            );
        });

        return command;
    }
}
