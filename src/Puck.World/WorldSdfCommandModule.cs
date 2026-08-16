using Puck.Commands;
using Puck.World.Client;
using Puck.World.Client.Sdf;

namespace Puck.World;

/// <summary>
/// The <c>puck.sdf.v1</c> geometry-document console surface: <c>world.sdf.load</c> reads a document FILE, decodes and
/// dry-validates it, and — only on success — composes it into the live scene through <see cref="WorldSdfDocumentEmitter"/>.
/// Client-local presentation state (like <c>world.screenshot</c>), never a simulation mutation, so it stays Immediate.
/// </summary>
internal sealed class WorldSdfCommandModule(WorldSdfDocumentEmitter documents) : ICommandModule {
    private readonly WorldSdfDocumentEmitter m_documents = documents;

    private CommandResult LoadHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[world.sdf.load: expected <path>]");
        }

        var path = args[0].ToString();

        if (!File.Exists(path: path)) {
            return CommandResult.Error(output: $"[world.sdf.load: no file at {path}]");
        }

        try {
            // A length check BEFORE the read, not read-then-measure — a multi-gigabyte file must never
            // reach File.ReadAllBytes, the decoder's hash pass, or a full JsonDocument parse.
            var length = new FileInfo(fileName: path).Length;

            if (length > SdfDocumentDecoder.MaxDocumentBytes) {
                return CommandResult.Error(output: $"[world.sdf.load: '{path}' is {length} byte(s), more than the {SdfDocumentDecoder.MaxDocumentBytes}-byte ceiling a puck.sdf.v1 document may declare]");
            }

            var bytes = File.ReadAllBytes(path: path);

            var (ops, materials, hash) = m_documents.Load(utf8Json: bytes);

            return new CommandResult(Output: $"[world.sdf.load: '{path}' — {ops} op(s), {materials} material(s), fnv1a {hash:x16} — composed]");
        } catch (Exception exception) when ((exception is SdfDocumentException or IOException or UnauthorizedAccessException)) {
            return CommandResult.Error(output: $"[world.sdf.load: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.sdf.load",
            description: $"Loads a puck.sdf.v1 geometry document FILE and composes it into the live scene: world.sdf.load <path>. The file is refused outright past {SdfDocumentDecoder.MaxDocumentBytes} byte(s) (checked before it is read). The whole document is then decoded and dry-validated (schema tag, no unknown or omitted members — 'materials'/'ops' must be present, an explicit [] is the way to author/load an empty one — no duplicate keys, every number finite, every op/enum name recognized, every material reference inside the document's own palette, every builder call it replays into a throwaway builder first) before it replaces what is currently composed; an invalid document is refused whole and the previously loaded one (if any) keeps rendering unchanged. Echoes the op/material counts and the FNV-1a hash of the file's raw bytes (identity is over the received bytes, computed before decoding).",
            handler: LoadHandler
        );
    }

}
