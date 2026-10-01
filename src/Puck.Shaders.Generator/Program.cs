using Puck.SdfVm;

// The kernel builds' host of the declaration generator: the one list `puck shaders generate` writes and checks
// (ShaderDeclarations), run over the checkout named by the first argument. It writes only the files whose text the model
// has changed and prints one line for each. With --check, which a continuous-integration build passes, it writes nothing
// and exits 1 naming each file that differs. Either way it exits 1 naming every include it cannot own.
var check = ((args.Length == 2) && (args[1] == "--check"));
if ((args.Length != 1) && !check) {
    Console.Error.WriteLine(value: "Puck.Shaders.Generator: expected the repository root, then optionally --check.");

    return 2;
}
var differing = new List<string>();
var problems = new List<string>();
ShaderDeclarations.Reconcile(differing: differing, problems: problems, repositoryRoot: Path.GetFullPath(path: args[0]), write: !check);
foreach (var path in differing) {
    if (check) {
        Console.Error.WriteLine(value: $"Puck.Shaders.Generator: {path} disagrees with the model; a continuous-integration build writes no generated file, so run `puck shaders generate` (or build locally) and commit it.");
    } else {
        Console.Out.WriteLine(value: $"Puck.Shaders.Generator: wrote {path}.");
    }
}
foreach (var problem in problems) {
    Console.Error.WriteLine(value: $"Puck.Shaders.Generator: {problem}.");
}
return ((((differing.Count == 0) || !check) && (problems.Count == 0)) ? 0 : 1);
