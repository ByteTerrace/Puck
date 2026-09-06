using Puck.AdvancedGamingBrick.Forge;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Local document authoring and native cartridge compilation, available in every boot shape.</summary>
internal sealed class ForgeCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    private readonly Dictionary<WorldPrincipal, CartridgeDraft> m_drafts = [];

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Command(name: "new", grammar: "<cgb|agb> <title>", detail: "Creates a blank cartridge document.");
        yield return Command(name: "open", grammar: "<source-path>", detail: "Loads validated cartridge JSON.");
        yield return Command(name: "show", grammar: "[json-pointer]", detail: "Prints draft JSON; omit the pointer for the whole source.");
        yield return Command(name: "set", grammar: "<json-pointer> <json>", detail: "Edits source data; use /- to append to an array. JSON strings require quotes.");
        yield return Command(name: "remove", grammar: "<json-pointer>", detail: "Removes an existing source field or array entry.");
        yield return Command(name: "undo", grammar: "", detail: "Swaps the draft with its previous edit (undo/redo).");
        yield return Command(name: "check", grammar: "", detail: "Validates source paths, types, references and target limits.");
        yield return Command(name: "build", grammar: "", detail: "Compiles native ROM bytes and prints the source hash and variable addresses.");
        yield return Command(name: "save", grammar: "<source-path>", detail: "Writes canonical validated source JSON atomically.");
        yield return Command(name: "export", grammar: "<rom-path>", detail: "Compiles and atomically writes a standalone cartridge ROM.");
        yield return Command(name: "play", grammar: "<screen-index> <rom-path>", detail: "Exports a build and submits an authoritative screen insert. AGB uses explicit stub direct boot; no BIOS is bundled.");
    }

    private CommandDefinition Command(string name, string grammar, string detail) => CommandDefinition.WithWireArgs(
        bindability: CommandBindability.Unbindable, name: "forge." + name,
        description: $"forge.{name} {grammar} — {detail} Drafts belong to the acting local console or seat; file paths are explicit and relative to the host working directory.",
        handler: (context, args) => Execute(name: name, grammar: grammar, context: context, args: args));

    private CommandResult Execute(string name, string grammar, CommandContext context, WireArgs args) {
        var principal = context.ActingPrincipal();
        if (principal.Kind is not (PrincipalKind.Console or PrincipalKind.Seat)) {
            return CommandResult.Error(output: "[forge: local authoring requires a console or local seat]");
        }
        try {
            var count = name switch { "new" or "set" or "play" => 2, "open" or "save" or "export" or "remove" => 1, _ => 0 };
            if (args.Count < count || (name is "undo" or "check" or "build" && args.Count != 0) || (name is "show" or "remove" && args.Count > 1)) {
                return CommandResult.Error(output: $"[forge.{name}: expected {grammar}]");
            }
            if (name == "new") {
                m_drafts[key: principal] = new CartridgeDraft(document: CartridgeDocuments.Create(target: args[0].ToString(), title: WorldCommandArguments.RawAfter(context: context, args: in args, tokens: 2)));
                return new CommandResult(Output: "[forge.new: blank draft ready]");
            }
            if (name == "open") {
                var path = Path.GetFullPath(path: WorldCommandArguments.Raw(context: context, args: in args));
                using var stream = File.OpenRead(path: path);
                if (stream.Length > CartridgeDocuments.MaximumSourceBytes) { throw new ArgumentException(message: "Cartridge source exceeds the source size limit."); }
                var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(buffer: bytes);
                m_drafts[key: principal] = new CartridgeDraft(document: CartridgeDocuments.Parse(utf8: bytes));
                return new CommandResult(Output: $"[forge.open: {path}]");
            }
            if (!m_drafts.TryGetValue(key: principal, value: out var draft)) { return CommandResult.Error(output: "[forge: create or open a draft first]"); }
            switch (name) {
                case "show": return new CommandResult(Output: draft.Show(pointer: args.Count == 0 ? "" : args[0].ToString()));
                case "set": draft.Set(pointer: args[0].ToString(), json: WorldCommandArguments.RawAfter(context: context, args: in args, tokens: 2, preserveQuotes: true)); break;
                case "remove": draft.Remove(pointer: args[0].ToString()); break;
                case "undo": draft.Undo(); break;
                case "check": {
                    var canonical = CartridgeDocuments.Canonicalize(document: draft.Check());
                    return new CommandResult(Output: $"[forge.check: valid {canonical.Document.Target}; source={canonical.Hash}]");
                }
                case "save": {
                    var canonical = CartridgeDocuments.Canonicalize(document: draft.Check());
                    var path = Write(path: WorldCommandArguments.Raw(context: context, args: in args), bytes: canonical.Bytes);
                    return new CommandResult(Output: $"[forge.save: {path}; source={canonical.Hash}]");
                }
                default: {
                    var index = -1;
                    if (name == "play") {
                        if (!args.TryInt(index: 0, value: out index) || index < 0) { return CommandResult.Error(output: "[forge.play: screen index must be a nonnegative integer]"); }
                        if (!server.Grants.Allows(principal: principal, capability: WorldCapability.Control, subject: GrantSubject.Screen(index: index))) { return CommandResult.Error(output: "[forge.play: acting principal lacks Control over the screen]"); }
                    }
                    var document = draft.Check();
                    ICartridgeCompiler compiler = document.Target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
                    var result = compiler.Compile(document: document);
                    var path = name == "build" ? null : Write(path: WorldCommandArguments.RawAfter(context: context, args: in args, tokens: name == "play" ? 2 : 1), bytes: result.Rom);
                    if (name == "play") {
                        link.SubmitScreenOp(op: new WorldScreenOp.Insert(Index: index, ContentPath: path!, EngineId: result.Target == "agb" ? "advanced-gaming-brick" : "gaming-brick", Options: result.Target == "agb" ? "stub" : "cgb"), principal: principal);
                    }
                    var symbols = string.Join(separator: ", ", values: result.Variables.Select(selector: pair => $"{pair.Key}=0x{pair.Value:X8}"));
                    return new CommandResult(Output: $"[forge.{name}: {result.Target} {result.Rom.Length} bytes; source={result.SourceHash}; variables={symbols}{(path is null ? "" : $"; file={path}")}{(name == "play" ? "; insert submitted (server reports acceptance)" : "")}]");
                }
            }
            return new CommandResult(Output: $"[forge.{name}: draft updated; forge.check validates it]");
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception) || exception is IOException or UnauthorizedAccessException or DocumentValidationException or OverflowException) {
            return CommandResult.Error(output: $"[forge.{name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }
    }

    private static string Write(string path, byte[] bytes) {
        path = Path.GetFullPath(path: path);
        var temporary = path + "." + Guid.NewGuid().ToString(format: "N") + ".tmp";
        try { File.WriteAllBytes(path: temporary, bytes: bytes); File.Move(sourceFileName: temporary, destFileName: path, overwrite: true); }
        finally { File.Delete(path: temporary); }
        return path;
    }
}
