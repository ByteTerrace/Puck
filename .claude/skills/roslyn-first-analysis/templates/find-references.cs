// Semantic find-references over the whole solution via Roslyn + MSBuildWorkspace.
// Usage: dotnet run find-references.cs -- <solution-or-project-path> <symbol-name> [--containing <namespace-or-type-fragment>]
// Prints each matching declared symbol and every reference location (file:line).
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@*
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@*
#:property PublishAot=false

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length < 2) {
    Console.Error.WriteLine("usage: dotnet run find-references.cs -- <sln|slnx|csproj> <symbol-name> [--containing <fragment>]");
    return 2;
}

var path = Path.GetFullPath(args[0]);
var symbolName = args[1];
var containing = Array.IndexOf(args, "--containing") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

using var workspace = MSBuildWorkspace.Create();
workspace.WorkspaceFailed += (_, e) => {
    if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) Console.Error.WriteLine($"[workspace] {e.Diagnostic.Message}");
};

var solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
    ? (await workspace.OpenProjectAsync(path)).Solution
    : await workspace.OpenSolutionAsync(path);

var totalRefs = 0;
var declarations = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
foreach (var project in solution.Projects) {
    foreach (var symbol in await SymbolFinder.FindDeclarationsAsync(project, symbolName, ignoreCase: false)) {
        var display = symbol.ToDisplayString();
        if (containing is not null && !display.Contains(containing, StringComparison.Ordinal)) continue;
        if (!declarations.Add(symbol)) continue;

        Console.WriteLine($"\n== {symbol.Kind}: {display} (declared in {project.Name})");
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution)) {
            foreach (var location in referenced.Locations) {
                var pos = location.Location.GetLineSpan();
                Console.WriteLine($"   {pos.Path}:{pos.StartLinePosition.Line + 1}");
                totalRefs++;
            }
        }
    }
}

Console.WriteLine($"\n{declarations.Count} declaration(s), {totalRefs} reference location(s).");
return declarations.Count == 0 ? 1 : 0;
