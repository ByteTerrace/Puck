using Puck.Commands;
using Puck.World.Client;
using Puck.World.Client.Sdf;

namespace Puck.World;

/// <summary>
/// The <c>puck.sdf.v1</c> geometry-document console surface: <c>world.sdf.load</c> reads a document FILE, decodes and
/// dry-validates it, and — only on success — composes it into the live scene through <see cref="WorldSdfDocumentEmitter"/>.
/// <c>world.sdf.dump</c> exports the live packed GPU program for inspection.
/// Client-local presentation state (like <c>world.screenshot</c>), never a simulation mutation, so it stays Immediate.
/// </summary>
internal sealed class WorldSdfCommandModule(WorldSdfDocumentEmitter documents, WorldRenderProbe renderProbe, WorldCreationInstances creations) : ICommandModule {
    private static readonly System.Text.Json.JsonSerializerOptions RigJson = new() { IncludeFields = true };
    private readonly WorldSdfDocumentEmitter m_documents = documents;
    private readonly WorldRenderProbe m_renderProbe = renderProbe;
    private static readonly CommandForms WorldSdfDumpForms = CommandForms.Of(verb: "world.sdf.dump", "<path>");
    private static readonly CommandForms WorldSdfLoadForms = CommandForms.Of(verb: "world.sdf.load", "<path>");
    private static readonly CommandForms WorldSdfRigForms = CommandForms.Of(verb: "world.sdf.rig", "[registration] [shape]");

    private CommandResult DumpHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return WorldSdfDumpForms.Refuse();
        }

        var words = m_renderProbe.Residency?.CopyLiveProgramWords();

        if (words is not { Length: > 0 }) {
            return CommandResult.Error(output: "[world.sdf.dump: no live render program]");
        }

        var path = args[0].ToString();

        try {
            using var stream = File.Create(path: path);
            using var writer = new BinaryWriter(output: stream);

            foreach (var word in words) {
                writer.Write(value: word);
            }

            return new CommandResult(Output: $"[world.sdf.dump: '{path}' — {words.Length} little-endian uint32 word(s); transforms and frame grid are separate]");
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)) {
            return CommandResult.Error(output: $"[world.sdf.dump: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }
    }
    private CommandResult LoadHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return WorldSdfLoadForms.Refuse();
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
    private CommandResult RigHandler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            var rows = new List<string>();

            for (var index = 0; (index < creations.RegistrationCount); index++) {
                if (creations.TryRegistrationRig(registration: index, state: out var state) && (state is not null)) {
                    rows.Add(item: $"{index}: {state.Creation}");
                }
            }
            return new CommandResult(Output: string.Join(separator: "\n", values: rows));
        }
        if ((args.Count > 2) || !int.TryParse(args[0].ToString(), out var registration) ||
            !creations.TryRegistrationRig(registration: registration, state: out var rig, shapeName: ((args.Count == 2) ? args[1].ToString() : null))) {
            return WorldSdfRigForms.Refuse();
        }
        return new CommandResult(Output: System.Text.Json.JsonSerializer.Serialize(value: rig, options: RigJson));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(audience: CommandAudience.Anyone, bindability: CommandBindability.Unbindable, name: "world.sdf.rig", description: $"Lists live creation registrations, or reads one placement/body rig including driver phase, weight and continuation: {WorldSdfRigForms.Spelling}.", forms: WorldSdfRigForms, handler: RigHandler);
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.sdf.dump",
            description: $"Writes the live packed GPU program as little-endian uint32 words: {WorldSdfDumpForms.Spelling}. Replaces the destination file. Excludes reserved capacity and dynamic transforms. Diagnostic output follows the current SdfProgram layout, not a loadable or durable asset format. Requires an initialized renderer; does not mutate the world.",
            forms: WorldSdfDumpForms,
            handler: DumpHandler
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.sdf.load",
            description: $"Loads a puck.sdf.v1 geometry document FILE and composes it into the live scene: {WorldSdfLoadForms.Spelling}. The file is refused outright past {SdfDocumentDecoder.MaxDocumentBytes} byte(s) (checked before it is read). The whole document is then decoded and dry-validated (schema tag, no unknown or omitted members — 'materials'/'ops' must be present, an explicit [] is the way to author/load an empty one — no duplicate keys, every number finite, every op/enum name recognized, every material reference inside the document's own palette, every builder call it replays into a throwaway builder first) before it replaces what is currently composed; an invalid document is refused whole and the previously loaded one (if any) keeps rendering unchanged. Echoes the op/material counts and the FNV-1a hash of the file's raw bytes (identity is over the received bytes, computed before decoding).",
            forms: WorldSdfLoadForms,
            handler: LoadHandler
        );
    }

}
