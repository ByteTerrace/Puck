using System.Globalization;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Named machine execution read-back and generic ordered provider operations.</summary>
/// <param name="authority">The console session's authority resolver.</param>
public sealed class WorldMachineCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    /// <summary>The value <c>machine.content</c>'s <c>mounted</c> field and <c>screen.state</c> give a machine that runs
    /// with no content.</summary>
    public const string NoCartridge = "no cartridge";

    private CommandResult Operation(CommandContext context, WireArgs args) {
        if (args.Count < 4) {
            return CommandResult.Error(output: "[machine.operation: expected <instance> <generation> <operationId> <json>]");
        }
        if (!authority.TryResolveLink(
            context: context,
            error: out var authorityError,
            link: out var link,
            verb: "machine.operation"
        )) {
            return authorityError;
        }

        var instance = args[0].ToString();

        if (
            string.IsNullOrWhiteSpace(value: instance) ||
            !ulong.TryParse(
            args[1],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var generation
        )
        ) {
            return CommandResult.Error(output: "[machine.operation: expected a nonblank instance and unsigned decimal generation]");
        }

        var operationId = args[2].ToString();
        var raw = WorldCommandArguments.RawAfter(
            context,
            in args,
            tokens: 4,
            preserveQuotes: true
        );
        var rawBytes = System.Text.Encoding.UTF8.GetByteCount(s: raw);

        if (
            string.IsNullOrWhiteSpace(value: operationId) ||
            (rawBytes == 0) ||
            (rawBytes > WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation))
        ) {
            return CommandResult.Error(output: $"[machine.operation: operationId must be nonblank and JSON must be 1..{WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation)} UTF-8 bytes]");
        }

        JsonElement payload;

        try {
            using var document = JsonDocument.Parse(raw);

            payload = document.RootElement.Clone();
        } catch (JsonException error) {
            return CommandResult.Error(output: $"[machine.operation: invalid JSON payload — {error.Message}]");
        }

        return SubmitOperation(
            link,
            context.Principal,
            new WorldMachineOperation(
                expectedGeneration: generation,
                instance: instance,
                operationId: operationId,
                payload: payload
            )
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.state",
            description: "Reports named machine execution: machine.state [instance]. Includes generation, running state, hardware status, completed segments, backlog, and fault. Author declarations through world.row.set machines <json>.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[machine.state: expected an optional instance name]");
                }
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "machine.state"
                )) {
                    return error;
                }
                var names = ((args.Count == 1)
                    ? [args[0].ToString()]
                    : server.Machines.InstanceNames
                );
                var echo = CommandEcho.Open(verb: "machine.state");
                var found = false;

                foreach (var name in names) {
                    if (server.Machines.InstanceState(name: name) is not { } state) {
                        return CommandResult.Error(output: $"[machine.state: no machine '{name}']");
                    }
                    found = true;
                    echo = echo.Head(head: name).Field(
                        key: "generation",
                        value: state.Generation
                    ).Field(
                        key: "engine",
                        value: state.Engine
                    )
                        .Field(
                        key: "running",
                        value: state.Running
                    ).Field(
                        key: "status",
                        value: state.Status.ToString()
                    )
                        .Field(
                        key: "segments",
                        value: state.FramesStepped
                    ).Field(
                        key: "pending",
                        value: state.PendingSteps
                    )
                        .Field(
                        key: "fault",
                        value: (state.Fault ?? "none")
                    );
                    var declaration = server.Definition.Machines.First(predicate: row => (row.Name == name));

                    foreach (var binding in (declaration.Memory ?? [])) {
                        var observed = server.MachineBindingState(
                            name,
                            binding.Name
                        );

                        echo = echo.Segment().Head(head: name).Field(
                            key: "binding",
                            value: binding.Name
                        )
                            .Field(
                            key: "access",
                            value: (observed?.Status.ToString() ?? "Unavailable")
                        )
                            .Field(
                            key: "value",
                            value: (observed?.LastValue?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved")
                        )
                            .Field(
                            key: "reason",
                            value: (observed?.Reason ?? "none")
                        );
                    }
                }
                return new CommandResult(Output: (found
                    ? echo
                    : echo.Head(head: "none")).Close());
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.operation",
            description: "Submits one generic provider operation through the ordered domain: machine.operation <instance> <generation> <operationId> <json>. The detached payload completes as Applied, Unsupported, Refused, or Faulted.",
            handler: Operation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.content",
            description: "Lists the content a named machine can swap to: machine.content <instance>. Echoes the content mounted now ('no cartridge' for none) and the mountable and refused file counts, one folder segment per authored contentFolders entry (its resolved path, whether it is present on this machine, and its file count; an absent folder is listed, never refused), and one segment per file in those folders naming its folder, whether it is mounted, and, for a file the machine's engine cannot mount, why. A query (always echoes).",
            handler: Content
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.swap",
            description: "Mounts a file from a named machine's contentFolders: machine.swap <instance> <name>, where <name> is a file name machine.content lists, its folder and file name (<folder>/<file>) when two folders hold the name, or the part of a mountable file's name before its first '.' when only one has it. A name no file answers, one several answer, or one naming a file the engine cannot mount is refused by name. Submits the provider's content.insert at the machine's current generation, requiring Control over the machine; the machine restarts on the new content and keeps its authored configuration otherwise.",
            handler: Swap
        );
    }
    private CommandResult Content(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[machine.content: expected <instance>]");
        }
        if (!authority.TryResolveServer(
            context: context,
            error: out var error,
            server: out var server,
            verb: "machine.content"
        )) {
            return error;
        }
        if (!TryListContent(
            entries: out var entries,
            failure: out var failure,
            folders: out var folders,
            instance: args[0].ToString(),
            machine: out var machine,
            server: server,
            verb: "machine.content"
        )) {
            return failure;
        }

        var mounted = server.Machines.InstanceState(name: machine.Name)?.Content;
        var echo = CommandEcho.Open(verb: "machine.content").Head(head: machine.Name)
            .Field(
            key: "engine",
            value: machine.Engine
        )
            .Field(
            key: "mounted",
            value: (mounted ?? NoCartridge)
        )
            .Field(
            key: "mountable",
            value: entries.Count(predicate: static entry => (entry.Refusal is null))
        )
            .Field(
            key: "refused",
            value: entries.Count(predicate: static entry => (entry.Refusal is not null))
        );

        foreach (var folder in folders) {
            echo = echo.Segment().Head(head: "folder").Field(
                key: "path",
                value: folder.Path
            )
                .Field(
                key: "resolved",
                value: folder.Resolved
            )
                .Field(
                key: "present",
                value: folder.Present
            )
                .Field(
                key: "files",
                value: entries.Count(predicate: entry => string.Equals(a: entry.Folder, b: folder.Path, comparisonType: StringComparison.Ordinal))
            );
        }
        foreach (var entry in entries) {
            echo = echo.Segment().Head(head: entry.Name).Field(
                key: "folder",
                value: entry.Folder
            )
                .Field(
                key: "mounted",
                value: string.Equals(
                    a: entry.ContentPath,
                    b: mounted,
                    comparisonType: StringComparison.Ordinal
                )
            );
            if (entry.Refusal is { } refusal) {
                echo = echo.Field(
                    key: "refused",
                    value: refusal
                );
            }
        }
        return new CommandResult(Output: echo.Close());
    }
    private CommandResult Swap(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[machine.swap: expected <instance> <name>]");
        }
        if (!authority.TryResolveServer(
            context: context,
            error: out var error,
            server: out var server,
            verb: "machine.swap"
        )) {
            return error;
        }
        if (!TryListContent(
            entries: out var entries,
            failure: out var failure,
            folders: out _,
            instance: args[0].ToString(),
            machine: out var machine,
            server: server,
            verb: "machine.swap"
        )) {
            return failure;
        }
        if (!WorldMachineContentFolder.TryFind(
            entries: entries,
            entry: out var entry,
            name: args[1].ToString(),
            reason: out var reason
        )) {
            return CommandResult.Error(output: $"[machine.swap: {machine.Name} {reason}]");
        }
        if (!authority.TryResolveLink(
            context: context,
            error: out var linkError,
            link: out var link,
            verb: "machine.swap"
        )) {
            return linkError;
        }
        return InsertContent(
            contentPath: entry.ContentPath,
            host: server.Machines,
            instance: machine.Name,
            link: link,
            principal: context.Principal,
            verb: "machine.swap"
        );
    }
    // Lists a live machine's folders as they stand now: the host's current declaration, so a swap lists against the
    // content it runs, and the disk as it is, so a folder that appeared, emptied or vanished since the last listing
    // shows it.
    private static bool TryListContent(WorldServer server, string instance, string verb, out WorldMachine machine,
        out IReadOnlyList<WorldMachineContentFolder.Folder> folders, out IReadOnlyList<WorldMachineContentFolder.Entry> entries,
        out CommandResult failure) {
        folders = [];
        entries = [];
        failure = CommandResult.None;

        if (server.Machines.CaptureInstances().FirstOrDefault(predicate: row => string.Equals(
            a: row.Name,
            b: instance,
            comparisonType: StringComparison.Ordinal
        )) is not { } live) {
            machine = null!;
            failure = CommandResult.Error(output: $"[{verb}: no machine '{instance}']");
            return false;
        }
        machine = live;
        if (!WorldMachineContentFolder.TryList(
            catalog: server.Machines.ValidationCatalog,
            documentDirectory: server.Definition.DocumentDirectory,
            entries: out entries,
            folders: out folders,
            machine: live,
            reason: out var reason
        )) {
            failure = CommandResult.Error(output: $"[{verb}: {reason}]");
            return false;
        }
        return true;
    }
    /// <summary>Inserts content into a live named instance using its provider's versioned operation descriptor.</summary>
    /// <param name="link">The ordered console submission link.</param>
    /// <param name="host">The current host, used to resolve generation and provider metadata.</param>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="instance">The named machine whose content changes.</param>
    /// <param name="contentPath">The content path to prepare at the host.</param>
    /// <param name="verb">The calling console verb.</param>
    /// <returns>The typed operation outcome or an unavailable-capability error.</returns>
    public static CommandResult InsertContent(IServerLink link, IWorldMachineHost host, Principal principal,
        string instance, string contentPath, string verb) {
        if (
            (host.InstanceState(name: instance) is not { } state) ||
            !host.ValidationCatalog.TryDescriptor(
            state.Engine,
            out var descriptor
        )
        ) {
            return CommandResult.Error(output: $"[{verb}: named machine '{instance}' is unavailable]");
        }
        var operation = descriptor.Operations.FirstOrDefault(predicate: candidate => (candidate.Id == "content.insert"));

        if (operation is null) {
            return CommandResult.Error(output: $"[{verb}: '{instance}' does not support content.insert]");
        }
        return SubmitOperation(
            link: link,
            principal: principal,
            request: new WorldMachineOperation(
                instance,
                state.Generation,
                operation.Id,
                JsonSerializer.SerializeToElement(new { schema = operation.Payload.Id, content = new { path = contentPath } })
            ),
            verb: verb
        );
    }
    /// <summary>Submits a provider operation and returns its typed completion through the console transcript.</summary>
    /// <param name="link">The ordered console link, which completes these operations inline.</param>
    /// <param name="principal">The acting principal whose named-machine Control grant the server checks.</param>
    /// <param name="request">The named instance, expected generation, and detached provider payload.</param>
    /// <param name="verb">The calling console verb used in the response.</param>
    /// <returns>The provider outcome or an explicit transport or missing-completion error.</returns>
    public static CommandResult SubmitOperation(IServerLink link, Principal principal, WorldMachineOperation request,
        string verb = "machine.operation") {
        var result = CommandResult.Error(output: $"[{verb}: {request.Instance} outcome unavailable; execution status unknown]");

        _ = link.SubmitEnvelope(
            payload: new WorldSubmissionPayload.Operation(Value: request),
            principal: principal,
            operationId: Guid.Empty,
            completion: completion => {
                result = completion switch {
                    WorldSubmissionResult.MachineOperation operation => new CommandResult(CommandEcho.Open(verb: verb).Head(head: request.Instance)
                            .Field(
                    key: "status",
                    value: operation.Result.Status.ToString()
                )
                            .Field(
                    key: "reason",
                    value: (operation.Result.Reason ?? "none")
                )
                            .Field(
                    key: "value",
                    value: (operation.Result.Value?.GetRawText() ?? "none")
                ).Close()) {
                        IsError = (operation.Result.Status != Puck.Abstractions.Machines.MachineOperationStatus.Applied),
                    },
                    WorldSubmissionResult.Refusal refusal => CommandResult.Error(output: $"[{verb}: {request.Instance} transport={refusal.Code} reason={refusal.Detail}]"),
                    _ => CommandResult.Error(output: $"[{verb}: {request.Instance} outcome unavailable ({completion.GetType().Name}); execution status unknown]")
                };
            }
        );
        return result;
    }
}
