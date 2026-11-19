using System.Text;
using Puck.Assets;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Composition;

/// <summary>One world a composition source declares, as a compile left it.</summary>
/// <param name="Name">The world's declared name.</param>
/// <param name="Entry">Whether the source declares it the world a boot starts in.</param>
/// <param name="Json">The world's canonical document, UTF-8.</param>
public sealed record WorldCompiledWorld(string Name, bool Entry, byte[] Json);
/// <summary>One world a <c>test</c> block generated, as a compile left it.</summary>
/// <param name="Name">The generated world's name.</param>
/// <param name="Test">The test's own name, as written.</param>
/// <param name="Subject">The module instantiation the test stands up, or <see langword="null"/> for the enclosing
/// document.</param>
/// <param name="Json">The generated world's canonical document, UTF-8.</param>
/// <param name="Siblings">The other worlds of the composition the test boots beside it, in order; their
/// <see cref="WorldCompiledWorld.Entry"/> is <see langword="false"/>.</param>
/// <param name="Foreign">The expectations the test addresses to sibling documents, which are lowered into those
/// documents once they are composed.</param>
public sealed record WorldCompiledTest(string Name, string Test, string? Subject, byte[] Json, IReadOnlyList<WorldCompiledWorld> Siblings, IReadOnlyList<WorldTestForeign> Foreign);
/// <summary>What one successful compile of a <c>.puck</c> world source produced, and every file fact it rests on.</summary>
/// <param name="Document">The one document the source lowers to, UTF-8, exactly the bytes
/// <c>WorldCompilation.RequireJson().ToJsonString()</c> encodes; <see langword="null"/> when the source declares
/// several worlds.</param>
/// <param name="EmitsDocument">Whether the source emits a document and so carries a document name
/// (<see cref="WorldCompilation.EmitsDocument"/>); <see langword="false"/> for a module library, whose
/// <paramref name="Document"/> is the empty document its top level lowers to.</param>
/// <param name="Worlds">The worlds the source declares, in declaration order; empty for a single-document source.</param>
/// <param name="Schema">The schema the source declares, or <see langword="null"/> for a module that declares none.</param>
/// <param name="Tests">The worlds the source's <c>test</c> blocks generate, in written order.</param>
/// <param name="Inputs">Every file fact the compile read, the source first.</param>
public sealed record WorldCompiledSource(byte[]? Document, bool EmitsDocument, IReadOnlyList<WorldCompiledWorld> Worlds, string? Schema, IReadOnlyList<WorldCompiledTest> Tests, IReadOnlyList<CompileInput> Inputs) {
    /// <summary>Snapshots one successful compilation's output bytes and the input facts that produced them.</summary>
    /// <param name="compilation">The successful compilation.</param>
    /// <returns>The emitted documents and their provenance, suitable for composition without recompiling.</returns>
    /// <exception cref="ArgumentNullException">The compilation is null.</exception>
    /// <exception cref="ArgumentException">The compilation did not succeed.</exception>
    public static WorldCompiledSource From(WorldCompilation compilation) {
        ArgumentNullException.ThrowIfNull(argument: compilation);
        if (!compilation.Success) {
            throw new ArgumentException(message: "A successful compilation is required.", paramName: nameof(compilation));
        }

        return new WorldCompiledSource(
            Document: ((compilation.Json is { } document) ? Encoding.UTF8.GetBytes(s: document.ToJsonString()) : null),
            EmitsDocument: compilation.EmitsDocument,
            Inputs: compilation.Inputs,
            Schema: compilation.Document?.Schema,
            Tests: [
                .. compilation.TestWorlds.Select(selector: static test => new WorldCompiledTest(
                    Foreign: test.Foreign,
                    Json: Encoding.UTF8.GetBytes(s: test.Json.ToJsonString()),
                    Name: test.Name,
                    Siblings: [
                        .. test.Siblings.Select(selector: static sibling => new WorldCompiledWorld(
                            Entry: false,
                            Json: Encoding.UTF8.GetBytes(s: sibling.Json.ToJsonString()),
                            Name: sibling.Name
                        )),
                    ],
                    Subject: test.Subject,
                    Test: test.Test
                )),
            ],
            Worlds: [
                .. compilation.Worlds.Select(selector: static world => new WorldCompiledWorld(
                    Entry: world.Entry,
                    Json: Encoding.UTF8.GetBytes(s: world.Json.ToJsonString()),
                    Name: world.Name
                )),
            ]
        );
    }
    /// <summary>Returns the document names the source emits, relative to its directory
    /// (<see cref="WorldCompilation.EmittedNames"/>).</summary>
    /// <param name="sourcePath">The source's path, spelled as the file system spells its file name.</param>
    /// <returns>The emitted document names, in declaration order.</returns>
    public IReadOnlyList<string> DocumentNames(string sourcePath) => WorldCompilation.EmittedNames(
        declaredWorlds: Worlds.Select(selector: static world => world.Name),
        emitsDocument: EmitsDocument,
        sourcePath: sourcePath
    );
    /// <summary>Returns the document the source emits under <paramref name="name"/>, one of its
    /// <see cref="DocumentNames"/>: the world a composition declares under exactly that name, or the one document an
    /// ordinary source lowers to.</summary>
    /// <param name="name">An emitted document name.</param>
    /// <returns>The document's UTF-8 JSON, or <see langword="null"/> when a composition declares no world of that
    /// name.</returns>
    public byte[]? DocumentNamed(string name) => ((Worlds.Count > 0)
        ? Worlds.FirstOrDefault(predicate: world => string.Equals(
            a: world.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ))?.Json
        : Document
    );
}
