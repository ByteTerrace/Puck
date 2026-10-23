using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Compiles authored cartridge sources and resolves their imported modules.</summary>
public static class CartridgeSource {
    /// <summary>Recognizes cartridge source and document paths.</summary>
    /// <param name="path">The content path.</param>
    /// <returns>Whether the content needs cartridge compilation.</returns>
    public static bool Recognizes(string path) => (path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck") || path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".cartridge.json"));
    /// <summary>Lowers source bytes with imports resolved beside their source path.</summary>
    /// <param name="content">The authored source bytes.</param>
    /// <param name="sourcePath">The resolved source path.</param>
    /// <returns>The lowered document.</returns>
    /// <exception cref="MachineContentException">The source or an import cannot be compiled.</exception>
    public static JsonObject Lower(ReadOnlyMemory<byte> content, string sourcePath) {
        var diagnostics = new DiagnosticBag();
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source: Encoding.UTF8.GetString(bytes: content.Span), diagnostics: diagnostics).Value;
        var lowered = ((parsed is null) ? null : CartridgeDocumentEmitter.LowerSource(parsed, sourcePath, diagnostics).Value);

        if (diagnostics.HasErrors || (lowered is null)) {
            throw new MachineContentException(string.Join(separator: "; ", values: diagnostics.Select(selector: d => $"{d.Code}: {d.Message}")));
        }
        return lowered;
    }
    /// <summary>Reads and compiles a cartridge source to its validated document.</summary>
    /// <param name="sourcePath">The resolved source path.</param>
    /// <returns>The validated cartridge document.</returns>
    public static CartridgeDocument Read(string sourcePath) => CartridgeDocuments.Parse(utf8: CanonicalJsonDocument.Serialize(node: Lower(content: File.ReadAllBytes(path: sourcePath), sourcePath: sourcePath)));
    /// <summary>Prepares source or JSON through the native cartridge compiler.</summary>
    /// <param name="compiler">The native compiler.</param>
    /// <param name="content">The pinned input bytes.</param>
    /// <param name="contentPath">The resolved input path.</param>
    /// <returns>The executable cartridge and symbols.</returns>
    public static PreparedMachineContent Prepare(ICartridgeCompiler compiler, ReadOnlyMemory<byte> content, string contentPath) =>
        CartridgeContentProvider.Prepare(compiler: compiler, content: (contentPath.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck")
            ? CanonicalJsonDocument.Serialize(node: Lower(content: content, sourcePath: contentPath)) : content));
}
