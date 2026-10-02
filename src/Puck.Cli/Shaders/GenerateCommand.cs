using System.CommandLine;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary><c>puck shaders generate</c>: writes every file the C# model owns, each regenerated from the live types, or
/// under <c>--check</c> regenerates them in memory and compares, the drift shape <c>puck schema --check</c> and
/// <c>puck registry --check</c> share. The files are the HLSL declarations <see cref="ShaderDeclarations"/> lists, the one
/// list the kernel builds generate before their kernels compile, and the build's shader recipe
/// (<see cref="ShaderCompiler.BuildRecipePath"/>), which <c>build/Shaders.targets</c> imports when a build is evaluated
/// and so is generated here alone. A checked-in <c>*.interface.hlsli</c> no generator owns, and a package whose include
/// cannot be found, fail both modes by name. A check also fails on a file that matches the model only in the working
/// tree while its staged copy differs or is missing, since a kernel build writes every declaration the model changed: CI runs the check
/// after its candidate CLI is built, and the commit, not the build, is what it judges. Exit 0 wrote or matched, 1 check
/// found drift or an include is unowned or missing, 2 missing repository root.</summary>
internal static class GenerateCommand {
    private const string Verb = "shaders generate";

    /// <summary>Finds every file the model owns among a tree's files, and every problem that keeps one from being
    /// generated.</summary>
    /// <param name="files">The tree's files, repository-relative with forward slashes: every <c>*.interface.hlsli</c> is
    /// read from these.</param>
    /// <param name="packages">The engine packages whose declared interfaces are owned includes.</param>
    /// <param name="problems">Receives one line per include no generator owns and per package whose include is missing or
    /// named twice.</param>
    /// <returns>The declarations in <see cref="ShaderDeclarations.Of"/>'s order, then the build's shader recipe.</returns>
    internal static IReadOnlyList<ShaderDeclaration> Includes(IReadOnlyList<string> files, RenderGraphPackageCatalog packages, List<string> problems) => [
        .. ShaderDeclarations.Of(files: files, packages: packages, problems: problems),
        new ShaderDeclaration(Generate: ShaderCompiler.GenerateBuildRecipe, Path: ShaderCompiler.BuildRecipePath),
    ];
    /// <summary>Writes or checks every include the model owns in a tree.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="files">The tree's files, repository-relative with forward slashes.</param>
    /// <param name="packages">The engine packages whose declared interfaces are owned includes.</param>
    /// <param name="check">Whether to compare rather than write.</param>
    /// <returns>0 when every include was written or matched and none is unowned or missing, otherwise 1.</returns>
    internal static int Run(string repositoryRoot, IReadOnlyList<string> files, RenderGraphPackageCatalog packages, bool check) {
        var problems = new List<string>();
        var matched = true;
        var current = new List<string>();

        foreach (var include in Includes(files: files, packages: packages, problems: problems)) {
            if (CliGeneratedFile.WriteOrCheck(
                check: check,
                detail: "",
                relativePath: include.Path,
                repositoryRoot: repositoryRoot,
                source: "the model",
                text: include.Generate(),
                verb: Verb
            )) {
                current.Add(item: include.Path);
            } else {
                matched = false;
            }
        }
        // A kernel build writes every declaration the model has changed, so a tree a build has run over matches the model
        // whatever the change committed. The check therefore also holds each file to what is staged: one that matches the
        // model only in the working tree is drift the change has not committed.
        if (check) {
            foreach (var path in Unstaged(paths: current, repositoryRoot: repositoryRoot)) {
                problems.Add(item: $"{path} matches the model only in the working tree, and its staged copy differs or is missing; a build or `puck {Verb}` wrote it, so stage and commit it");
            }
        }
        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"{Verb}: {problem}.");
        }

        return ((matched && (problems.Count == 0)) ? 0 : 1);
    }

    // The paths whose working-tree text differs from or is absent from the index, when the root is a git work tree's top.
    private static IReadOnlyList<string> Unstaged(string repositoryRoot, IReadOnlyList<string> paths) {
        if (paths.Count == 0) {
            return [];
        }

        var top = CliGit.Run(repositoryRoot, "rev-parse", "--show-toplevel");

        if ((top.ExitCode != 0) || !string.Equals(
            a: Path.GetFullPath(path: top.Stdout.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b: Path.GetFullPath(path: repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            return [];
        }

        var staged = CliGit.Run(repositoryRoot, ["ls-files", "--cached", "-z", "--", .. paths]);
        var differing = CliGit.Run(repositoryRoot, ["diff", "--name-only", "-z", "--no-ext-diff", "--no-textconv", "--", .. paths]);

        if ((staged.ExitCode != 0) || (differing.ExitCode != 0)) {
            throw new InvalidOperationException(message: $"Cannot check generated shader declarations against the git index: {staged.Stderr}{differing.Stderr}");
        }

        var indexed = staged.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').ToHashSet(comparer: StringComparer.Ordinal);

        return [.. paths.Where(predicate: path => !indexed.Contains(item: path))
            .Concat(second: differing.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0'))
            .Distinct(comparer: StringComparer.Ordinal)];
    }
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        // Tracked and untracked files git does not ignore, so a new include is checked before it is committed and build
        // output never is.
        var listed = CliGit.Run(repositoryRoot, "ls-files", "--cached", "--others", "--exclude-standard", "--", $"*{ShaderDeclarations.InterfaceSuffix}");

        return Run(
            check: check,
            files: [.. listed.Stdout.Split(separator: '\n').Select(selector: static line => line.TrimEnd(trimChar: '\r')).Where(predicate: static line => (line.Length > 0)).Distinct(comparer: StringComparer.Ordinal)],
            packages: RenderGraphPackageCatalog.Engine,
            repositoryRoot: repositoryRoot
        );
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Regenerate every include in memory and compare against the working file and, in a git work tree, its staged copy; write nothing, and exit 1 naming each missing or differing file and each interface include no generator owns.",
        description: """
        The files the C# model owns, generated and checked.

        sdf-isa.hlsli (src/Puck.SdfVm/Assets/Shaders/Sdf/isa) is generated by Puck.SdfVm.SdfIsaHlsl
        from the SDF instruction set in Puck.SignedDistance: every enum an instruction word
        carries, and the packed-layout constants the interpreter decodes words with. The
        instruction set's fingerprint, over that file and the encoding SdfEncodingProbe
        describes, is recorded in src/Puck.SdfVm/SdfIsaFingerprint.cs for the host, and stamps
        the SDF kernels' interfaces.

        Every generated shader interface (<name>.interface.hlsli) the model declares is owned too,
        in one list: an engine package's with pass-group members (such as overlay, place and
        sdf.film-grain), found by its file name, and the SDF engine kernels' (sdf-world,
        sdf-bricks, sdf-mesh and sdf-resolve, declared by Puck.SdfVm.SdfKernelInterfaces) at
        their fixed paths. A checked-in interface include no package or engine kernel owns, or a
        declared interface whose include is missing, fails by name.

        The model lives in Puck.Shaders.Model and Puck.SdfVm.Model, which compile no shader. Every
        project whose kernels include these files references Puck.Shaders.Generator, whose build
        writes each one whose text the model has changed before any kernel compiles, so a kernel
        reading a declaration the model has just gained builds in one pass.

        The build's shader recipe, build/ShaderRecipe.targets, is generated from
        Puck.Shaders.ShaderCompiler.StepsOf: the DXC options build/Shaders.targets compiles every
        stage source with, the options the runtime shader compiler runs. A build reads it when it
        is evaluated, so only this verb writes it.
        """,
        name: "generate",
        run: Run
    );
}
