using Puck.SdfVm;

// The kernel builds' host of the declaration generator: the one list `puck shaders generate` writes and checks
// (ShaderDeclarations), run over the checkout named by the one argument. It writes only the files whose text the model
// has changed, prints one line for each, and exits 1 naming every include it cannot own, as the verb's check does.
if (args.Length != 1) {
    Console.Error.WriteLine(value: "Puck.Shaders.Generator: expected the repository root as the one argument.");

    return 2;
}
var written = new List<string>();
var problems = new List<string>();
ShaderDeclarations.WriteChanged(problems: problems, repositoryRoot: Path.GetFullPath(path: args[0]), written: written);
foreach (var path in written) {
    Console.Out.WriteLine(value: $"Puck.Shaders.Generator: wrote {path}.");
}
foreach (var problem in problems) {
    Console.Error.WriteLine(value: $"Puck.Shaders.Generator: {problem}.");
}
return ((problems.Count == 0) ? 0 : 1);
