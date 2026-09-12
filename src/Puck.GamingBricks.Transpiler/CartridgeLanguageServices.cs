using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.GamingBricks.Forge;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.GamingBricks.Transpiler;

/// <summary>The cartridge diagnostics shared by command-line and editor hosts.</summary>
public static class CartridgeLanguageServices {
    /// <summary>Returns cartridge authoring completions, or null for another vocabulary.</summary>
    /// <param name="document">The current parsed buffer.</param>
    /// <returns>LSP completion items owned by this schema.</returns>
    public static JsonArray? Completions(DocumentNode document) {
        if (document.Schema != CartridgeVocabulary.Schema) { return null; }
        var items = new JsonArray();
        Add("schema", "schema: \"puck.cartridge.v1\"");
        Add("target", "target: \"${1|cgb,agb|}\"");
        Add("let", "let ${1:name} = ${2:value}");
        Add("template", "template ${1:name}(${2:parameters}) {\n    $0\n}");
        Add("for", "for ${1:item} in ${2:sequence} {\n    $0\n}");
        Add("variable", "variable \"${1:name}\" {\n    initial: ${2:0}\n    max: ${3:255}\n}");
        Add("array", "array \"${1:name}\" {\n    initial [${2:0}]\n}");
        Add("sprite", "sprite \"${1:name}\" {\n    tile: 0\n    x: 0\n    y: 0\n    visible: 1\n}");
        Add("rule", "rule \"${1:name}\" {\n    when ${2:state} == ${3:0}\n    $0\n}");
        Add("repeat", "repeat ${1:count} as ${2:index} {\n    $0\n}");
        Add("map", "map(row: ${1:0}, column: ${2:0}, tile: ${3:0})");
        foreach (var name in new[] { "if", "else", "break", "when", "and", "or", "not", "key", "play", "stop", "save", "load", "clock", "blit", "plot", "blend", "fade" }) { Add(name, name); }
        return items;

        void Add(string label, string insertText) => items.AppendNode(new JsonObject {
            ["label"] = label, ["kind"] = 14, ["insertText"] = insertText, ["insertTextFormat"] = 2,
            ["detail"] = "Cartridge authoring",
        });
    }

    /// <summary>Lowers and validates a cartridge document if its schema selects this vocabulary.</summary>
    /// <param name="document">The parsed source.</param>
    /// <param name="sourcePath">The optional source path; cartridges carry no imports.</param>
    /// <param name="diagnostics">The shared diagnostic bag.</param>
    /// <returns>Whether the document selected the cartridge vocabulary.</returns>
    public static bool Diagnose(DocumentNode document, string? sourcePath, DiagnosticBag diagnostics) {
        if (document.Schema != CartridgeVocabulary.Schema) { return false; }
        var sourceMap = new SourceMap();
        var lowered = CartridgeDocumentEmitter.LowerWithDiagnostics(document, diagnostics, sourceMap);
        if (lowered.Success) { Validate(lowered.Value!, sourceMap, diagnostics, document.Span); }
        return true;
    }

    /// <summary>Validates lowered JSON and maps forge paths back to authored rows and properties.</summary>
    /// <param name="document">The lowered cartridge.</param>
    /// <param name="sourceMap">The emitter's JSON pointer map.</param>
    /// <param name="diagnostics">The destination for diagnostics.</param>
    /// <param name="fallback">The document span when no more specific span exists.</param>
    public static void Validate(JsonObject document, SourceMap sourceMap, DiagnosticBag diagnostics, SourceSpan fallback) {
        foreach (var error in CartridgeDocuments.Validate(CanonicalJsonDocument.Serialize(document))) {
            var pointer = error.Path.TrimStart('$', '.').Replace("[", "/", StringComparison.Ordinal)
                .Replace("]", "", StringComparison.Ordinal).Replace('.', '/');
            var span = sourceMap.TryGetSpan(pointer, out var located) ? located : fallback;
            diagnostics.ReportError(PuckDiagnosticCodes.SemanticValidation, $"{error.Path}: {error.Message}", span);
        }
    }
}
