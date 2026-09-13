using Puck.Assets.Documents;
using Puck.Commands;
using Puck.GamingBricks.Forge;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Local document authoring and native cartridge compilation, available in every boot shape.</summary>
internal sealed class ForgeCommandModule(WorldServer server, IServerLink link, IEnumerable<ICartridgeCompiler>? compilers = null) : ICommandModule {
    private readonly Dictionary<WorldPrincipal, CartridgeDraft> m_drafts = [];
    private readonly IEnumerable<ICartridgeCompiler>? m_compilers = compilers;

    private CommandDefinition Command(string name, string grammar, string detail) => CommandDefinition.WithWireArgs(
        bindability: CommandBindability.Unbindable,
        name: ("forge." + name),
        description: $"forge.{name} {grammar} — {detail} Drafts belong to the acting local console or seat; file paths are explicit and relative to the host working directory.",
        handler: (context, args) => Execute(
            args: args,
            context: context,
            grammar: grammar,
            name: name
        )
    );
    private CommandResult Execute(string name, string grammar, CommandContext context, WireArgs args) {
        var principal = context.ActingPrincipal();

        if (principal.Kind is not (PrincipalKind.Console or PrincipalKind.Seat)) {
            return CommandResult.Error(output: "[forge: local authoring requires a console or local seat]");
        }
        try {
            var count = name switch { "new" or "set" or "play" => 2, "open" or "save" or "export" or "remove" => 1, _ => 0 };

            if (
                (args.Count < count) ||
                ((name is "undo" or "check" or "build") && (args.Count != 0)) ||
                ((name is "show" or "remove") && (args.Count > 1))
            ) {
                return CommandResult.Error(output: $"[forge.{name}: expected {grammar}]");
            }
            if (name == "new") {
                m_drafts[key: principal] = new CartridgeDraft(document: CartridgeDocuments.Create(
                    target: args[0].ToString(),
                    title: WorldCommandArguments.RawAfter(
                        context: context,
                        args: in args,
                        tokens: 2
                    )
                ));
                return new CommandResult(Output: "[forge.new: blank draft ready]");
            }
            if (name == "open") {
                var path = Path.GetFullPath(path: WorldCommandArguments.Raw(
                    args: in args,
                    context: context
                ));
                using var stream = File.OpenRead(path: path);

                if (stream.Length > CartridgeDocuments.MaximumSourceBytes) { throw new ArgumentException(message: "Cartridge source exceeds the source size limit."); }
                var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(buffer: bytes);
                m_drafts[key: principal] = new CartridgeDraft(document: CartridgeDocuments.Parse(utf8: bytes));
                return new CommandResult(Output: $"[forge.open: {path}]");
            }
            if (!m_drafts.TryGetValue(
                key: principal,
                value: out var draft
            )) { return CommandResult.Error(output: "[forge: create or open a draft first]"); }
            switch (name) {
                case "show": return new CommandResult(Output: draft.Show(pointer: ((args.Count == 0)
                    ? ""
                    : args[0].ToString())));
                case "set": draft.Set(
                    pointer: args[0].ToString(),
                    json: WorldCommandArguments.RawAfter(
                        args: in args,
                        context: context,
                        preserveQuotes: true,
                        tokens: 2
                    )
                ); break;
                case "remove": draft.Remove(pointer: args[0].ToString()); break;
                case "undo": draft.Undo(); break;
                case "check": {
                        var canonical = CartridgeDocuments.Canonicalize(document: draft.Check());

                        return new CommandResult(Output: $"[forge.check: valid {canonical.Document.Target}; source={canonical.Hash}]");
                    }
                case "save": {
                        var canonical = CartridgeDocuments.Canonicalize(document: draft.Check());
                        var path = Write(
                            path: WorldCommandArguments.Raw(
                                args: in args,
                                context: context
                            ),
                            bytes: canonical.Bytes
                        );

                        return new CommandResult(Output: $"[forge.save: {path}; source={canonical.Hash}]");
                    }
                default: {
                        var index = -1;

                        if (name == "play") {
                            if (
                                !args.TryInt(
                                index: 0,
                                value: out index
                            ) ||
                                (index < 0)
                            ) { return CommandResult.Error(output: "[forge.play: screen index must be a nonnegative integer]"); }
                            if (!server.Grants.Allows(
                                principal: principal,
                                capability: WorldCapability.Control,
                                subject: GrantSubject.Screen(index: index)
                            )) { return CommandResult.Error(output: "[forge.play: acting principal lacks Control over the screen]"); }
                        }
                        var document = draft.Check();
                        var compiler = ResolveCompiler(target: document.Target);
                        var result = compiler.Compile(document: document);
                        string? path = null;

                        if (name != "build") {
                            var outputPath = WorldCommandArguments.RawAfter(
                                context: context,
                                args: in args,
                                tokens: ((name == "play")
                                ? 2
                                : 1)
                            );

                            if (
                                (name == "play") &&
                                !((Puck.Abstractions.Machines.IMachineContentProvider)compiler).Recognizes(contentPath: outputPath)
                            ) {
                                return CommandResult.Error(output: "[forge.play: source path must end in .cartridge.json]");
                            }
                            path = Write(
                                path: outputPath,
                                bytes: ((name == "play")
                                ? CartridgeDocuments.Canonicalize(document: document).Bytes
                                : result.Rom)
                            );
                        }
                        if (
                            (name == "play") &&
                            (server.Definition.Screens.FirstOrDefault(predicate: screen => (screen.Index == index))?.Source is WorldScreenSource.Machine named)
                        ) {
                            if (server.Machines.InstanceState(name: named.Instance) is not { } state) {
                                return CommandResult.Error(output: $"[forge.play: named machine '{named.Instance}' is unavailable; source saved to {path}]");
                            }
                            if (state.Engine != compiler.EngineId) {
                                return CommandResult.Error(output: $"[forge.play: '{named.Instance}' uses '{state.Engine}', but this cartridge requires '{compiler.EngineId}'; source saved to {path}]");
                            }
                            return WorldMachineCommandModule.InsertContent(
                                link,
                                server.Machines,
                                principal,
                                named.Instance,
                                path!,
                                verb: "forge.play"
                            );
                        }
                        if (name == "play") {
                            link.SubmitScreenOp(
                                op: new WorldScreenOp.Insert(
                                    Index: index,
                                    ContentPath: path!,
                                    EngineId: compiler.EngineId,
                                    Options: ((result.Target == "agb")
                                ? null
                                : "cgb")
                                ),
                                principal: principal
                            );
                        }
                        var symbols = string.Join(
                            separator: ", ",
                            values: result.Variables.Select(selector: pair => $"{pair.Key}=0x{pair.Value:X8}")
                        );

                        return new CommandResult(Output: $"[forge.{name}: {result.Target} {result.Rom.Length} bytes; source={result.SourceHash}; variables={symbols}{((path is null)
                            ? ""
                            : $"; file={path}")}{((name == "play")
                            ? "; insert submitted (server reports acceptance)"
                            : "")}]");
                    }
            }
            return new CommandResult(Output: $"[forge.{name}: draft updated; forge.check validates it]");
        } catch (Exception exception) when ((WorldJsonPayload.IsParseFailure(exception: exception) || (exception is IOException or UnauthorizedAccessException or DocumentValidationException or OverflowException))) {
            return CommandResult.Error(output: $"[forge.{name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
        }
    }
    private ICartridgeCompiler ResolveCompiler(string target) {
        if (m_compilers is not null) {
            foreach (var compiler in m_compilers) {
                if (string.Equals(
                    a: compiler.Target,
                    b: target,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    return compiler;
                }
            }
        }

        throw new InvalidOperationException(message: $"No cartridge compiler registered for target '{target}'.");
    }
    private static string Write(string path, byte[] bytes) {
        path = Path.GetFullPath(path: path);
        var temporary = (((path + ".") + Guid.NewGuid().ToString(format: "N")) + ".tmp");

        try { File.WriteAllBytes(
            bytes: bytes,
            path: temporary
        ); File.Move(
            destFileName: path,
            overwrite: true,
            sourceFileName: temporary
        ); } finally { File.Delete(path: temporary); }
        return path;
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Command(
            detail: "Creates a blank cartridge document.",
            grammar: "<cgb|agb> <title>",
            name: "new"
        );
        yield return Command(
            detail: "Loads validated cartridge JSON.",
            grammar: "<source-path>",
            name: "open"
        );
        yield return Command(
            detail: "Prints draft JSON; omit the pointer for the whole source.",
            grammar: "[json-pointer]",
            name: "show"
        );
        yield return Command(
            detail: "Edits source data; use /- to append to an array. JSON strings require quotes.",
            grammar: "<json-pointer> <json>",
            name: "set"
        );
        yield return Command(
            detail: "Removes an existing source field or array entry.",
            grammar: "<json-pointer>",
            name: "remove"
        );
        yield return Command(
            detail: "Swaps the draft with its previous edit (undo/redo).",
            grammar: "",
            name: "undo"
        );
        yield return Command(
            detail: "Validates source paths, types, references and target limits.",
            grammar: "",
            name: "check"
        );
        yield return Command(
            detail: "Compiles native ROM bytes and prints the source hash and variable addresses.",
            grammar: "",
            name: "build"
        );
        yield return Command(
            detail: "Writes canonical validated source JSON atomically.",
            grammar: "<source-path>",
            name: "save"
        );
        yield return Command(
            detail: "Compiles and atomically writes a standalone cartridge ROM.",
            grammar: "<rom-path>",
            name: "export"
        );
        yield return Command(
            detail: "Saves canonical source and inserts it into the screen's named machine through its versioned provider operation. Requires matching engines and Control over the screen and machine; retains the authored machine configuration.",
            grammar: "<screen-index> <source.cartridge.json>",
            name: "play"
        );
    }
}
