using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Testing;

/// <summary>A named source file in a harness compilation.</summary>
/// <param name="Name">The file name; a <c>.g.cs</c> suffix is what makes Roslyn treat the tree as generated.</param>
/// <param name="Text">The C# source.</param>
internal readonly record struct SourceFile(string Name, string Text);
/// <summary>Compiles a fixture as an external assembly against the references of its test process.</summary>
internal static class FixtureCompiler {
    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    /// <summary>The metadata references every harness compilation gets: the running runtime's own reference set.</summary>
    public static ImmutableArray<MetadataReference> RuntimeReferences =>
        References;

    private static ImmutableArray<MetadataReference> CreateReferences() {
        var trusted = ((AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty);

        return trusted
            .Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: System.IO.Path.PathSeparator
        )
            .Where(predicate: path => path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".dll"
        ))
            .Select(selector: path => ((MetadataReference)MetadataReference.CreateFromFile(path: path)))
            .ToImmutableArray();
    }
    private static SyntaxTree Parse(SourceFile file) =>
        CSharpSyntaxTree.ParseText(
            text: SourceText.From(
                text: file.Text,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            ),
            options: new CSharpParseOptions(languageVersion: LanguageVersion.Latest),
            path: file.Name
        );

    /// <summary>Builds a compilation from <paramref name="sources"/> plus the global usings the SDK would supply.</summary>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    /// <param name="sources">The source files to compile.</param>
    /// <returns>The compilation.</returns>
    public static CSharpCompilation Compile(string assemblyName, params SourceFile[] sources) {
        var trees = new List<SyntaxTree> {
            Parse(file: new SourceFile(
            Name: "GlobalUsings.cs",
            Text: "global using System;\r\nglobal using System.Collections.Generic;\r\nglobal using System.Linq;\r\nglobal using System.Threading;\r\nglobal using System.Threading.Tasks;\r\n"
        )),
        };

        trees.AddRange(collection: sources.Select(selector: Parse));

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: trees,
            references: References,
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }
}
