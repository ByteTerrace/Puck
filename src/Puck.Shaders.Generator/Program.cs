using Puck.SdfVm;
using Puck.Shaders;
using Puck.Shaders.Generator;

// The kernel builds' build-time host. With the repository root as its argument it runs the declaration generator:
// the one list `puck shaders generate` writes and checks (ShaderDeclarations), writing only the files whose text the
// model has changed, printing one line for each, and exiting 1 naming every include it cannot own. A second argument
// names the stamp the build keys the next run on: removed first, and written only on success with every file the run
// read or owns, one full forward-slashed path per line, so an edited, deleted or restored file reruns it. With
// `compile` or `check` it runs one project's shader build (ShaderBuild) for build/Shaders.targets.
if (args is ["compile" or "check", ..]) {
    return await ShaderBuildCommand.RunAsync(arguments: args).ConfigureAwait(continueOnCapturedContext: false);
}
if (args.Length is not (1 or 2)) {
    Console.Error.WriteLine(value: "Puck.Shaders.Generator: expected the repository root and an optional stamp as the arguments, or 'compile' or 'check' and a shader build's options.");

    return 2;
}
var root = Path.GetFullPath(path: args[0]);
var stamp = ((args.Length == 2) ? Path.GetFullPath(path: args[1]) : null);

if (stamp is not null) {
    File.Delete(path: stamp);
}
var written = new List<string>();
var problems = new List<string>();
ShaderDeclarations.Reconcile(problems: problems, repositoryRoot: root, written: written);
foreach (var path in written) {
    Console.Out.WriteLine(value: $"Puck.Shaders.Generator: wrote {path}.");
}
foreach (var problem in problems) {
    Console.Error.WriteLine(value: $"Puck.Shaders.Generator: {problem}.");
}
if (problems.Count != 0) {
    return 1;
}
if (stamp is not null) {
    var interfaces = ShaderDeclarations.InterfaceFiles(repositoryRoot: root);
    var owned = ShaderDeclarations.Of(files: interfaces, packages: RenderGraphPackageCatalog.Engine, problems: []).Select(selector: static declaration => declaration.Path);

    Directory.CreateDirectory(path: Path.GetDirectoryName(path: stamp)!);
    File.WriteAllLines(
        contents: interfaces.Concat(second: owned).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).Select(selector: path => Path.Combine(path1: root, path2: path).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar)),
        path: stamp
    );
}
return 0;
