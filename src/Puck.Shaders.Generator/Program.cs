using Puck.SdfVm;
using Puck.Shaders.Generator;

// The kernel builds' build-time host. With the repository root as its one argument it runs the declaration generator:
// the one list `puck shaders generate` writes and checks (ShaderDeclarations), writing only the files whose text the
// model has changed, printing one line for each, and exiting 1 naming every include it cannot own. With `compile` or
// `check` it runs one project's shader build (ShaderBuild) for build/Shaders.targets.
if (args is ["compile" or "check", ..]) {
    return await ShaderBuildCommand.RunAsync(arguments: args).ConfigureAwait(continueOnCapturedContext: false);
}
if (args.Length != 1) {
    Console.Error.WriteLine(value: "Puck.Shaders.Generator: expected the repository root as the one argument, or 'compile' or 'check' and a shader build's options.");

    return 2;
}
var written = new List<string>();
var problems = new List<string>();
ShaderDeclarations.Reconcile(problems: problems, repositoryRoot: Path.GetFullPath(path: args[0]), written: written);
foreach (var path in written) {
    Console.Out.WriteLine(value: $"Puck.Shaders.Generator: wrote {path}.");
}
foreach (var problem in problems) {
    Console.Error.WriteLine(value: $"Puck.Shaders.Generator: {problem}.");
}
return ((problems.Count == 0) ? 0 : 1);
