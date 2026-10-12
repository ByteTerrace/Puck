using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the <c>puck.render.graph.v1</c> document: a graph of shader and package passes validates and plans through
/// the one pipeline planner, a fragment package's passes spliced in place of the pass naming it, each package refusal is
/// named, a planner refusal passes through with its own code, a view
/// reading its own output goes through the planner's history resource, a package pass, which only a graph's packages
/// member declares, plans as its own kind over its compute shape, and every checked-in graph document plans alike through a
/// pipeline host, which offers no package, and through the engine's catalog.
/// </summary>
public sealed class RenderGraphDocumentLawTests {
    // A checked-in document may run a shipped package's kernel as a shader pass of its own, compiled from the kernel's
    // source through the interface the pass's config generates. That config restates the package's pass block, so it
    // must restate all of it: a field the package has and the document lacks leaves the kernel reading a member the pass
    // block does not declare, and the pass fails to compile. Every such pass, its kernel's file named for its package,
    // declares exactly the package's config and, as config of its own type, each pass-block value the package's recorder
    // writes (every value but the work counter row, which a document pass has no counters for).
    [Fact]
    public void EveryCheckedInPassCompilingAShippedPackageKernelDeclaresThatPackagesConfig() {
        var root = RepositoryPaths.RequireRoot();
        var kernels = Path.Combine(
            path1: root,
            path2: "src"
        );
        var matched = 0;

        foreach (var path in Directory.EnumerateFiles(
            path: Path.Combine(
                path1: root,
                path2: "tests"
            ),
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.graph.json"
        ).Order(comparer: StringComparer.Ordinal)) {
            var definition = ShaderPipelineLoader.ParseDefinition(
                name: Path.GetFileNameWithoutExtension(path: path),
                path: path,
                text: File.ReadAllText(path: path)
            );

            foreach (var pass in (definition.Passes ?? [])) {
                var source = Path.GetFullPath(path: Path.Combine(
                    path1: Path.GetDirectoryName(path: path)!,
                    path2: pass.Source
                ));
                var stem = Path.GetFileName(path: source).Split(separator: '.')[0];

                if (
                    !source.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: kernels) ||
                    !RenderGraphPackageCatalog.Engine.TryGet(
                        id: stem,
                        package: out var package
                    )
                ) {
                    continue;
                }

                matched++;

                var expected = (package.Config ?? new Dictionary<string, ShaderConfigField>());
                var values = package.Members.Where(predicate: static member => (
                    (member.Kind == ShaderInterfaceMemberKind.Value) &&
                    (member.Group == ShaderInterfaceGroup.Pass) &&
                    !ShaderWorkCounters.Members.Contains(value: member)
                )).ToArray();
                var actual = (pass.Config ?? new Dictionary<string, ShaderConfigField>());
                var where = $"{Path.GetRelativePath(path: path, relativeTo: root)} pass '{pass.Name}' ({package.Id})";
                var names = expected.Keys.Concat(second: values.Select(selector: static value => value.Name)).Order(comparer: StringComparer.Ordinal).ToArray();

                Assert.True(
                    condition: names.SequenceEqual(second: actual.Keys.Order(comparer: StringComparer.Ordinal)),
                    userMessage: $"{where} declares [{string.Join(separator: ", ", values: actual.Keys.Order(comparer: StringComparer.Ordinal))}] where the package declares [{string.Join(separator: ", ", values: names)}]"
                );

                foreach (var value in values) {
                    Assert.True(
                        condition: ((actual[value.Name].Type == value.Type) && (actual[value.Name].Length == value.Length)),
                        userMessage: $"{where} field '{value.Name}' is not the package's pass-block value"
                    );
                }

                foreach (var (name, field) in expected) {
                    var declared = actual[name];

                    Assert.True(
                        condition: (
                            (declared.Type == field.Type) &&
                            (declared.Length == field.Length) &&
                            (declared.Min == field.Min) &&
                            (declared.Max == field.Max) &&
                            (declared.Default.HasValue == field.Default.HasValue) &&
                            (!field.Default.HasValue || JsonElement.DeepEquals(element1: declared.Default!.Value, element2: field.Default.Value))
                        ),
                        userMessage: $"{where} field '{name}' differs from the package's"
                    );
                }
            }
        }

        Assert.True(condition: (matched > 0), userMessage: "no checked-in document runs a shipped package kernel");
    }
}
