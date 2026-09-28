using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Decompiler;

namespace Puck.World.Transpiler;

/// <summary>Prepares and atomically writes a live snapshot into source after proving its composed document.</summary>
public static class WorldSourceSave {
    /// <summary>Saves a snapshot through the source printer, retaining unrelated authored text in an existing file.</summary>
    /// <param name="definition">The live snapshot to save.</param>
    /// <param name="path">The source file to update or create.</param>
    /// <param name="bytesWritten">The written byte count, or zero on refusal.</param>
    /// <param name="reason">The refusal, or empty after a verified write.</param>
    /// <param name="catalogFingerprint">The machine metadata fingerprint used during composition.</param>
    /// <param name="catalog">The host's machine validation catalog.</param>
    /// <returns>Whether the source was verified and written.</returns>
    public static bool TrySave(WorldDefinition definition, string path, out long bytesWritten, out string reason, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        bytesWritten = 0;
        var targetBytes = WorldDefinitionSerialization.SerializeBeside(definition: definition, path: path);
        var target = JsonNode.Parse(utf8Json: targetBytes)!.AsObject();
        var keepBom = false;
        string candidate;

        if (File.Exists(path: path)) {
            var originalBytes = File.ReadAllBytes(path: path);

            keepBom = originalBytes.AsSpan().StartsWith(value: Encoding.UTF8.Preamble);
            var source = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes: originalBytes.AsSpan(start: (keepBom ? Encoding.UTF8.Preamble.Length : 0)));
            var original = WorldCompiler.Compile(source: source, sourcePath: path);

            if (!original.Success || (original.Json is null)) {
                reason = ("source cannot be saved because it does not compile to one world: " + original.Diagnostics.FormatReport());
                return false;
            }
            if (!WorldSourceLoader.TryReadAuthored(path: path, document: Encoding.UTF8.GetBytes(s: original.Json.ToJsonString()), authored: out var baseline, reason: out reason, catalogFingerprint: catalogFingerprint, catalog: catalog)) {
                return false;
            }
            var canonical = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: baseline, path: path))!.AsObject();
            var delta = WorldDocumentBasis.Diff(basis: canonical, target: target);

            if (!WorldDocumentBasis.TryMerge(basis: original.Json, overlay: delta, composed: out var lowered, reason: out reason) ||
                !WorldSourceEdits.TryRewrite(reason: out reason, rewritten: out candidate, source: source, sourcePath: path, target: lowered!)) {
                return false;
            }
        } else {
            try { candidate = WorldDecompiler.Decompile(root: target); } catch (WorldDecompileRefusedException exception) {
                reason = $"{exception.Message}; rename the generated row before saving source, or save a JSON delta";
                return false;
            }
        }
        var compilation = WorldCompiler.Compile(source: candidate, sourcePath: path);

        if (!compilation.Success || (compilation.Json is null)) {
            reason = ("rewritten source does not compile: " + compilation.Diagnostics.FormatReport());
            return false;
        }
        if (!WorldSourceLoader.TryReadAuthored(path: path, document: Encoding.UTF8.GetBytes(s: compilation.Json.ToJsonString()), authored: out var proof, reason: out reason, catalogFingerprint: catalogFingerprint, catalog: catalog)) {
            return false;
        }
        // Producer settings retain authored JSON member order; equality is the lowered document's value.
        if (!JsonNode.DeepEquals(node1: JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: proof, path: path)), node2: target)) {
            reason = "rewritten source does not reproduce the live snapshot; nothing written";
            return false;
        }
        var written = Encoding.UTF8.GetBytes(s: candidate);

        if (keepBom) { written = [.. Encoding.UTF8.Preamble, .. written]; }
        Puck.Assets.AtomicFile.WriteAllBytes(path: path, bytes: written);
        bytesWritten = written.LongLength;
        reason = string.Empty;
        return true;
    }
}
