using System.CommandLine;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>One HLSL include the C# model owns: its repository-relative path and the generator that writes its
/// text.</summary>
/// <param name="Path">The include's repository-relative path, with forward slashes.</param>
/// <param name="Generate">Regenerates the include's text from the live types.</param>
internal sealed record GeneratedInclude(string Path, Func<string> Generate);
/// <summary><c>puck shaders generate</c>: writes every HLSL include the C# model owns, each regenerated from the live
/// types, or under <c>--check</c> regenerates them in memory and compares, the drift shape <c>puck schema --check</c>
/// and <c>puck registry --check</c> share. The includes are <c>sdf-isa.hlsli</c> and every generated shader
/// interface (<see cref="ShaderInterfaceHlsl"/>) the model declares, in one list: each engine package's with pass-group
/// members, found by its interface's file name, and the SDF engine kernels' (<see cref="SdfWorldInterfaces.Includes"/>)
/// at the paths they name. A checked-in
/// <c>*.interface.hlsli</c> no generator owns, and a package whose include cannot be found, fail both modes by name.
/// Exit 0 wrote or matched, 1 check found drift or an include is unowned or missing, 2 missing repository
/// root.</summary>
internal static class GenerateCommand {
    private const string InterfaceSuffix = ".interface.hlsli";
    private const string Verb = "shaders generate";

    /// <summary>Finds every include the model owns among a tree's files, and every problem that keeps one from being
    /// generated.</summary>
    /// <param name="files">The tree's files, repository-relative with forward slashes: every <c>*.interface.hlsli</c> is
    /// read from these.</param>
    /// <param name="packages">The engine packages whose declared interfaces are owned includes.</param>
    /// <param name="problems">Receives one line per include no generator owns and per package whose include is missing or
    /// named twice.</param>
    /// <returns>The owned includes, <c>sdf-isa.hlsli</c> first, then in path order.</returns>
    internal static IReadOnlyList<GeneratedInclude> Includes(IReadOnlyList<string> files, RenderGraphPackageCatalog packages, List<string> problems) {
        var owned = new SortedDictionary<string, GeneratedInclude>(comparer: StringComparer.Ordinal);
        var interfaceFiles = files.Where(predicate: static file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: InterfaceSuffix)).ToArray();

        // Every interface the model declares, in one list: each engine package's with pass-group members, found by its
        // include's file name, and the SDF engine kernels' (SdfWorldInterfaces), at the paths they name.
        IEnumerable<(string Owner, ShaderInterface Interface, string? Path)> Declared() {
            foreach (var package in packages.Packages.Where(predicate: static package => (package.Members.Count > 0))) {
                yield return (package.Id, ShaderPipelineParameterLayout.ForPackage(
                    config: package.Config,
                    members: package.Members,
                    package: package.Id,
                    pushesIndex: package.PushesIndex
                ).Interface, null);
            }
            foreach (var (path, shaderInterface) in SdfWorldInterfaces.Includes) {
                yield return (shaderInterface.Name, shaderInterface, path);
            }
        }

        foreach (var (owner, shaderInterface, path) in Declared()) {
            var fileName = ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name);
            var found = ((path is null)
                ? interfaceFiles.Where(predicate: file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: ("/" + fileName))).ToArray()
                : [path]);

            // A fixed path is always owned, and a missing file there is drift the check reports; a package's include is found
            // by its name, once.
            if (found.Length != 1) {
                problems.Add(item: ((found.Length == 0)
                    ? $"package '{owner}' declares an interface, but no {fileName} is checked in; write it with `puck shaders interface <directory> --package {owner} --write`"
                    : $"package '{owner}' declares an interface, and {found.Length} files are named {fileName}: {string.Join(separator: ", ", values: found)}"));

                continue;
            }

            owned[found[0]] = new GeneratedInclude(
                Generate: () => ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface),
                Path: found[0]
            );
        }
        foreach (var file in interfaceFiles.Where(predicate: file => !owned.ContainsKey(key: file))) {
            problems.Add(item: $"{file} is named as a generated interface, but no engine package or engine kernel owns it");
        }

        return [new GeneratedInclude(Generate: SdfIsaHlsl.Generate, Path: $"src/Puck.SdfVm/Assets/Shaders/Sdf/{SdfIsaHlsl.FileName}"), .. owned.Values];
    }
    /// <summary>Writes or checks every include the model owns in a tree.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="files">The tree's files, repository-relative with forward slashes.</param>
    /// <param name="packages">The engine packages whose declared interfaces are owned includes.</param>
    /// <param name="check">Whether to compare rather than write.</param>
    /// <returns>0 when every include was written or matched and none is unowned or missing, otherwise 1.</returns>
    internal static int Run(string repositoryRoot, IReadOnlyList<string> files, RenderGraphPackageCatalog packages, bool check) {
        var problems = new List<string>();
        var matched = true;

        foreach (var include in Includes(files: files, packages: packages, problems: problems)) {
            matched &= CliGeneratedFile.WriteOrCheck(
                check: check,
                detail: "",
                relativePath: include.Path,
                repositoryRoot: repositoryRoot,
                source: "the model",
                text: include.Generate(),
                verb: Verb
            );
        }
        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"{Verb}: {problem}.");
        }

        return ((matched && (problems.Count == 0)) ? 0 : 1);
    }

    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        // Tracked and untracked files git does not ignore, so a new include is checked before it is committed and build
        // output never is.
        var listed = CliGit.Run(repositoryRoot, "ls-files", "--cached", "--others", "--exclude-standard", "--", $"*{InterfaceSuffix}");

        return Run(
            check: check,
            files: [.. listed.Stdout.Split(separator: '\n').Select(selector: static line => line.TrimEnd(trimChar: '\r')).Where(predicate: static line => (line.Length > 0)).Distinct(comparer: StringComparer.Ordinal)],
            packages: RenderGraphPackageCatalog.Engine,
            repositoryRoot: repositoryRoot
        );
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Regenerate every include in memory and compare against the checked-in file; write nothing, and exit 1 naming each file that differs and its first differing line, and each interface include no generator owns.",
        description: """
        The HLSL includes the C# model owns, generated and checked.

        sdf-isa.hlsli (src/Puck.SdfVm/Assets/Shaders/Sdf) is generated by Puck.SdfVm.SdfIsaHlsl
        from the SDF instruction set in Puck.SignedDistance: the version handshake, every enum an
        instruction word carries, and the packed-layout constants the interpreter decodes words
        with. A kernel build reads the checked-in file, so a C# change to the instruction set is
        regenerated here and rebuilt.

        Every generated shader interface (<name>.interface.hlsli) the model declares is owned too,
        in one list: an engine package's with pass-group members (such as overlay, place and
        sdf.film-grain), found by its file name, and the SDF engine kernels' (sdf-world and
        sdf-brick-bake, declared by Puck.SdfVm.SdfWorldInterfaces) at their fixed paths. A
        checked-in interface include no package or engine kernel owns, or a declared interface
        whose include is missing, fails by name.
        """,
        name: "generate",
        run: Run
    );
}
