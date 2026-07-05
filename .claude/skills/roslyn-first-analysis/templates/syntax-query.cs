// Syntax-level query over C# files: no MSBuild, no compilation — parses each file and walks the tree.
// This example lists every type declaration (kind, name, base list, file:line) under a root, filtered by
// an optional name fragment. Adapt the Walker for other structure questions (members, attributes, usings).
// Usage: dotnet run syntax-query.cs -- <root-dir> [name-fragment]
#:package Microsoft.CodeAnalysis.CSharp@*
#:property PublishAot=false

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1) {
    Console.Error.WriteLine("usage: dotnet run syntax-query.cs -- <root-dir> [name-fragment]");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var fragment = args.Length > 1 ? args[1] : null;
var count = 0;

foreach (var file in Directory.EnumerateFiles(root, "*.cs", new EnumerationOptions { RecurseSubdirectories = true })) {
    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

    var tree = CSharpSyntaxTree.ParseText(text: File.ReadAllText(file), path: file);
    foreach (var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
        var name = node.Identifier.Text;
        if (fragment is not null && !name.Contains(fragment, StringComparison.Ordinal)) continue;

        var line = node.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var bases = (node as TypeDeclarationSyntax)?.BaseList?.Types.ToString() ?? node.BaseList?.Types.ToString() ?? "";
        Console.WriteLine($"{node.Kind(),-28} {name,-40} {file}:{line}{(bases.Length > 0 ? $"  : {bases}" : "")}");
        count++;
    }
}

Console.WriteLine($"\n{count} declaration(s).");
return 0;
