using Puck.Transpiler.Ast;

namespace Puck.World.Transpiler;

/// <summary>What a <c>.puck</c> world source declares about the documents it emits, read from its parsed tree alone:
/// whether it emits a document at all, and the worlds it declares by name. Nothing is expanded, lowered or composed to
/// read it, so a reader can know what every source in a directory emits without compiling any of them, which is how a
/// document name resolves to the source that emits it whatever that file's stem
/// (<see cref="Composition.WorldSourceIndex"/>). A compile reads the same declaration from the same tree
/// (<see cref="WorldCompilation.DocumentNames"/>), and the lowering admits exactly the worlds read here, so the two
/// never disagree.</summary>
/// <param name="EmitsDocument">Whether the source emits a document. A module library does not: it names no schema and
/// no basis, declares no world, and every statement at its top level is a <c>let</c>, a <c>template</c> or
/// <c>module</c>, an import of another <c>.puck</c> source, or a <c>test</c>, none of which reaches the document it is
/// written in; the sources importing it carry what it defines. Every other source emits a document: each world it
/// declares, or else the one document its top level lowers to.</param>
/// <param name="Worlds">The names of the worlds the source declares at its top level, in declaration order.</param>
public sealed record WorldSourceDeclaration(bool EmitsDocument, IReadOnlyList<string> Worlds) {
    /// <summary>Reads what a parsed source declares.</summary>
    /// <param name="document">The source's parsed tree.</param>
    /// <returns>The declaration.</returns>
    public static WorldSourceDeclaration Of(DocumentNode document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var emits = ((document.Schema is not null) || (document.Basis is not null));
        var worlds = new List<string>();

        foreach (var statement in document.Statements) {
            switch (statement) {
                case WorldDeclarationNode world: {
                        emits = true;

                        if (WorldName(declaration: world) is { } name) {
                            worlds.Add(item: name);
                        }

                        break;
                    }
                case ImportNode import when ImportsModule(import: import):
                case LetNode or TemplateNode or TestDeclarationNode or ErrorStatementNode:
                    break;
                default:
                    emits = true;
                    break;
            }
        }

        return new WorldSourceDeclaration(
            EmitsDocument: emits,
            Worlds: worlds
        );
    }
    /// <summary>Reads the name a world declaration gives its world, as written: a bare or backquoted name, or a plain
    /// string. A world's name is its document's name, which a reader resolves without compiling the source, so it is
    /// never computed: an interpolated string, a <c>let</c> or any other expression names no world.</summary>
    /// <param name="declaration">The declaration.</param>
    /// <returns>The written name, or <see langword="null"/> when the declaration computes it.</returns>
    public static string? WorldName(WorldDeclarationNode declaration) {
        ArgumentNullException.ThrowIfNull(argument: declaration);

        return declaration.Name switch {
            IdentifierExpressionNode identifier => identifier.Name,
            LiteralExpressionNode { Value: string text, Unit: null } => text,
            _ => null,
        };
    }
    /// <summary>Returns whether an import names another <c>.puck</c> source, whose compile-time declarations it brings
    /// in, rather than a document the composed world imports.</summary>
    /// <param name="import">The import.</param>
    /// <returns><see langword="true"/> when the import's path is a <c>.puck</c> source.</returns>
    public static bool ImportsModule(ImportNode import) {
        ArgumentNullException.ThrowIfNull(argument: import);

        return WorldDocumentName.IsSourceFile(path: import.Path);
    }
    /// <summary>Returns the document names the source emits (<see cref="WorldCompilation.EmittedNames"/>).</summary>
    /// <param name="sourcePath">The source's path, spelled as the file system spells its file name.</param>
    /// <returns>The emitted document names, in declaration order.</returns>
    public IReadOnlyList<string> Names(string sourcePath) => WorldCompilation.EmittedNames(
        declaredWorlds: Worlds,
        emitsDocument: EmitsDocument,
        sourcePath: sourcePath
    );
}
