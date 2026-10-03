using System.Text;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>One file the C# model owns: its repository-relative path and the generator that writes its text.</summary>
/// <param name="Path">The file's repository-relative path, with forward slashes.</param>
/// <param name="Generate">Regenerates the file's text, with LF line endings, from the live types.</param>
public sealed record ShaderDeclaration(string Path, Func<string> Generate);
/// <summary>
/// The one list of every HLSL declaration the C# model owns, and the generator the kernel builds and
/// <c>puck shaders generate</c> both run: <c>sdf-isa.hlsli</c> (<see cref="SdfIsaHlsl"/>), the sky's kind table
/// (<c>sdf-sky-kinds.hlsli</c> and <c>sky/sdf-sky-kind-table.hlsli</c>, <see cref="SdfSkyKindsHlsl"/>), the instruction
/// set's recorded
/// fingerprint (<see cref="SdfIsaHlsl.FingerprintSourcePath"/>), and every generated shader interface
/// (<see cref="ShaderInterfaceHlsl"/>) the model declares: each engine package's with pass-group members, found by its
/// interface's file name, and the SDF engine kernels' (<see cref="SdfKernelInterfaces"/>) at the paths they name.
/// <para>Every declaration is generated for the model's own instruction set (<see cref="SdfIsaHlsl.DescribeFingerprint"/>),
/// never the recorded one, so one run brings every file level.</para>
/// <para>The model compiles no shader, so a project whose kernels include these files generates them first: it
/// references <c>Puck.Shaders.Generator</c>, whose build runs <see cref="Reconcile"/> over the tree before any kernel
/// compiles. A file that already holds its text is left untouched, so an unchanged model recompiles no kernel.
/// <c>puck shaders generate --check</c> holds the same list to the model, and to what git has staged.</para>
/// </summary>
public static class ShaderDeclarations {
    /// <summary>The suffix of a generated interface include's file name.</summary>
    public const string InterfaceSuffix = ".interface.hlsli";

    private const string SourceDirectory = "src";

    /// <summary>Finds every declaration the model owns among a tree's files, and every problem that keeps one from being
    /// generated.</summary>
    /// <param name="files">The tree's files, repository-relative with forward slashes: every <c>*.interface.hlsli</c> is
    /// read from these.</param>
    /// <param name="packages">The engine packages whose declared interfaces are owned includes.</param>
    /// <param name="problems">Receives one line per include no generator owns and per package whose include is missing or
    /// named twice.</param>
    /// <returns>The owned files, <c>sdf-isa.hlsli</c>, the fingerprint's record and the sky's kind table first, then the
    /// includes in path order.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<ShaderDeclaration> Of(IReadOnlyList<string> files, RenderGraphPackageCatalog packages, List<string> problems) {
        ArgumentNullException.ThrowIfNull(argument: files);
        ArgumentNullException.ThrowIfNull(argument: packages);
        ArgumentNullException.ThrowIfNull(argument: problems);

        var owned = new SortedDictionary<string, ShaderDeclaration>(comparer: StringComparer.Ordinal);
        var interfaceFiles = files.Where(predicate: static file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: InterfaceSuffix)).ToArray();

        // Every interface the model declares, in one list: each engine package's with pass-group members, found by its
        // include's file name, and the SDF engine kernels', at the paths they name. A package whose interface a kernel
        // include already owns at its fixed path (sdf.world's) is that include, not a second owner. The model's own
        // instruction set, described afresh: the stamp its kernels' interfaces carry and the record the host reads, both
        // from this run's model rather than the recorded value, so one run brings every file level.
        var fingerprint = SdfIsaHlsl.DescribeFingerprint();
        var kernelInterfaces = new SdfKernelInterfaces(stamp: SdfIsaHlsl.StampOf(fingerprint: fingerprint)).Includes;
        var kernelIncludes = kernelInterfaces
            .Select(selector: static include => ShaderFrameInterface.IncludeFileName(interfaceName: include.Interface.Name))
            .ToHashSet(comparer: StringComparer.Ordinal);

        IEnumerable<(string Owner, ShaderInterface Interface, string? Path)> Declared() {
            foreach (var package in packages.Packages.Where(predicate: static package => (package.Members.Count > 0))) {
                var shaderInterface = ShaderPipelineParameterLayout.ForPackage(
                    config: package.Config,
                    members: package.Members,
                    package: package.Id,
                    pushesIndex: package.PushesIndex
                ).Interface;

                if (!kernelIncludes.Contains(item: ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name))) {
                    yield return (package.Id, shaderInterface, null);
                }
            }
            foreach (var (path, shaderInterface) in kernelInterfaces) {
                yield return (shaderInterface.Name, shaderInterface, path);
            }
        }

        foreach (var (owner, shaderInterface, path) in Declared()) {
            var fileName = ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name);
            var found = ((path is null)
                ? interfaceFiles.Where(predicate: file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: ("/" + fileName))).ToArray()
                : [path]);

            // A fixed path is always owned, and a missing file there is drift a check reports; a package's include is found
            // by its name, once.
            if (found.Length != 1) {
                problems.Add(item: ((found.Length == 0)
                    ? $"package '{owner}' declares an interface, but no {fileName} is checked in; write it with `puck shaders interface <directory> --package {owner} --write`"
                    : $"package '{owner}' declares an interface, and {found.Length} files are named {fileName}: {string.Join(separator: ", ", values: found)}"));

                continue;
            }

            owned[found[0]] = new ShaderDeclaration(
                Generate: () => ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface),
                Path: found[0]
            );
        }
        foreach (var file in interfaceFiles.Where(predicate: file => !owned.ContainsKey(key: file))) {
            problems.Add(item: $"{file} is named as a generated interface, but no engine package or engine kernel owns it");
        }

        return [
            new ShaderDeclaration(Generate: SdfIsaHlsl.Generate, Path: $"{SdfKernelInterfaces.KernelDirectory}/isa/{SdfIsaHlsl.FileName}"),
            new ShaderDeclaration(Generate: () => SdfIsaHlsl.GenerateFingerprintSource(fingerprint: fingerprint), Path: SdfIsaHlsl.FingerprintSourcePath),
            new ShaderDeclaration(Generate: SdfSkyKindsHlsl.Generate, Path: $"{SdfKernelInterfaces.KernelDirectory}/isa/{SdfSkyKindsHlsl.FileName}"),
            new ShaderDeclaration(Generate: SdfSkyKindsHlsl.GenerateTable, Path: $"{SdfKernelInterfaces.KernelDirectory}/sky/{SdfSkyKindsHlsl.TableFileName}"),
            .. owned.Values,
        ];
    }
    /// <summary>Lists the interface includes under a tree's <c>src</c> directory, as <see cref="Of"/> reads them: every
    /// <c>*.interface.hlsli</c>, repository-relative with forward slashes, outside a project's <c>bin</c> and <c>obj</c>
    /// build output.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The files, in ordinal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="repositoryRoot"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> InterfaceFiles(string repositoryRoot) {
        ArgumentNullException.ThrowIfNull(argument: repositoryRoot);

        var source = Path.Combine(path1: repositoryRoot, path2: SourceDirectory);

        if (!Directory.Exists(path: source)) {
            return [];
        }

        return [.. Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: ("*" + InterfaceSuffix))
            .Select(selector: file => Path.GetRelativePath(path: file, relativeTo: repositoryRoot).Replace(newChar: '/', oldChar: '\\'))
            .Where(predicate: static file => !file.Split(separator: '/').Any(predicate: static segment => (segment is "bin" or "obj")))
            .Order(comparer: StringComparer.Ordinal)];
    }
    /// <summary>Writes every declaration the model owns under a tree whose text differs from the file's, a missing file
    /// included, and leaves every other file untouched, so a build that runs it before its kernels recompiles only the
    /// kernels a changed declaration reaches. Line endings never count as a difference, and each write replaces its file
    /// whole.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="written">Receives the repository-relative path of each file written.</param>
    /// <param name="problems">Receives one line per include no generator owns and per package whose include is missing or
    /// named twice (<see cref="Of"/>).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static void Reconcile(string repositoryRoot, List<string> written, List<string> problems) {
        ArgumentNullException.ThrowIfNull(argument: repositoryRoot);
        ArgumentNullException.ThrowIfNull(argument: written);
        ArgumentNullException.ThrowIfNull(argument: problems);

        foreach (var declaration in Of(files: InterfaceFiles(repositoryRoot: repositoryRoot), packages: RenderGraphPackageCatalog.Engine, problems: problems)) {
            var path = Path.Combine(path1: repositoryRoot, path2: declaration.Path);
            var text = declaration.Generate();

            if (File.Exists(path: path) && string.Equals(
                a: File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n"),
                b: text,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            written.Add(item: declaration.Path);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";

            try {
                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                File.WriteAllText(
                    contents: text,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    path: temporary
                );
                File.Move(destFileName: path, overwrite: true, sourceFileName: temporary);
            } finally {
                File.Delete(path: temporary);
            }
        }
    }
}
