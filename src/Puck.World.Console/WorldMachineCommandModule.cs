using System.Globalization;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Named machine execution read-back and generic ordered provider operations.</summary>
/// <param name="authority">The console session's authority resolver.</param>
/// <param name="link">The ordered server submission link.</param>
public sealed class WorldMachineCommandModule(IWorldConsoleAuthority authority, IServerLink link) : ICommandModule {
    private CommandResult Operation(CommandContext context, WireArgs args) {
        if (args.Count < 4) {
            return CommandResult.Error("[machine.operation: expected <instance> <generation> <operationId> <json>]");
        }
        if (!authority.TryResolveServer(context, "machine.operation", out _, out var authorityError)) {
            return authorityError;
        }

        var instance = args[0].ToString();
        if (string.IsNullOrWhiteSpace(instance) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)) {
            return CommandResult.Error("[machine.operation: expected a nonblank instance and unsigned decimal generation]");
        }

        var operationId = args[2].ToString();
        var raw = WorldCommandArguments.RawAfter(context, in args, tokens: 4, preserveQuotes: true);
        var rawBytes = System.Text.Encoding.UTF8.GetByteCount(raw);
        if (string.IsNullOrWhiteSpace(operationId) || rawBytes == 0 ||
            rawBytes > WorldFrameCodec.MaxPayloadBytes(WorldSubmissionKind.Operation)) {
            return CommandResult.Error($"[machine.operation: operationId must be nonblank and JSON must be 1..{WorldFrameCodec.MaxPayloadBytes(WorldSubmissionKind.Operation)} UTF-8 bytes]");
        }

        JsonElement payload;
        try {
            using var document = JsonDocument.Parse(raw);
            payload = document.RootElement.Clone();
        } catch (JsonException error) {
            return CommandResult.Error($"[machine.operation: invalid JSON payload — {error.Message}]");
        }

        return SubmitOperation(link, context.ActingPrincipal(), new WorldMachineOperation(instance, generation, operationId, payload));
    }

    /// <summary>Inserts content into a live named instance using its provider's versioned operation descriptor.</summary>
    /// <param name="link">The ordered console submission link.</param>
    /// <param name="host">The current host, used to resolve generation and provider metadata.</param>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="instance">The named machine whose content changes.</param>
    /// <param name="contentPath">The content path to prepare at the host.</param>
    /// <param name="verb">The calling console verb.</param>
    /// <returns>The typed operation outcome or an unavailable-capability error.</returns>
    public static CommandResult InsertContent(IServerLink link, IWorldMachineHost host, WorldPrincipal principal,
        string instance, string contentPath, string verb) {
        if (host.InstanceState(instance) is not { } state ||
            !host.ValidationCatalog.TryDescriptor(state.Engine, out var descriptor)) {
            return CommandResult.Error($"[{verb}: named machine '{instance}' is unavailable]");
        }
        var operation = descriptor.Operations.FirstOrDefault(candidate => candidate.Id == "content.insert");
        if (operation is null) {
            return CommandResult.Error($"[{verb}: '{instance}' does not support content.insert]");
        }
        return SubmitOperation(link, principal,
            new WorldMachineOperation(instance, state.Generation, operation.Id,
                JsonSerializer.SerializeToElement(new { schema = operation.Payload.Id, content = new { path = contentPath } })),
            verb);
    }

    /// <summary>Submits a provider operation and returns its typed completion through the console transcript.</summary>
    /// <param name="link">The ordered console link, which completes these operations inline.</param>
    /// <param name="principal">The acting principal whose named-machine Control grant the server checks.</param>
    /// <param name="request">The named instance, expected generation, and detached provider payload.</param>
    /// <param name="verb">The calling console verb used in the response.</param>
    /// <returns>The provider outcome or an explicit transport or missing-completion error.</returns>
    public static CommandResult SubmitOperation(IServerLink link, WorldPrincipal principal, WorldMachineOperation request,
        string verb = "machine.operation") {
        var result = CommandResult.Error($"[{verb}: {request.Instance} outcome unavailable; execution status unknown]");
        _ = link.SubmitEnvelope(
            payload: new WorldSubmissionPayload.Operation(request),
            principal: principal,
            operationId: Guid.Empty,
            completion: completion => {
                result = completion switch {
                    WorldSubmissionResult.MachineOperation operation => new CommandResult(
                        CommandEcho.Open(verb).Head(request.Instance)
                            .Field("status", operation.Result.Status.ToString())
                            .Field("reason", operation.Result.Reason ?? "none")
                            .Field("value", operation.Result.Value?.GetRawText() ?? "none").Close()) {
                        IsError = operation.Result.Status != Puck.Abstractions.Machines.MachineOperationStatus.Applied
                    },
                    WorldSubmissionResult.Refusal refusal => CommandResult.Error(
                        $"[{verb}: {request.Instance} transport={refusal.Code} reason={refusal.Detail}]"),
                    _ => CommandResult.Error(
                        $"[{verb}: {request.Instance} outcome unavailable ({completion.GetType().Name}); execution status unknown]")
                };
            }
        );
        return result;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.state",
            description: "Reports named machine execution: machine.state [instance]. Includes generation, running state, hardware status, completed segments, backlog, and fault. Author declarations through world.row.set machines <json>.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error("[machine.state: expected an optional instance name]");
                }
                if (!authority.TryResolveServer(context, "machine.state", out var server, out var error)) {
                    return error;
                }
                var names = args.Count == 1 ? [args[0].ToString()] : server.Machines.InstanceNames;
                var echo = CommandEcho.Open("machine.state");
                var found = false;
                foreach (var name in names) {
                    if (server.Machines.InstanceState(name) is not { } state) {
                        return CommandResult.Error($"[machine.state: no machine '{name}']");
                    }
                    found = true;
                    echo = echo.Head(name).Field("generation", state.Generation).Field("engine", state.Engine)
                        .Field("running", state.Running).Field("status", state.Status.ToString())
                        .Field("segments", state.FramesStepped).Field("pending", state.PendingSteps)
                        .Field("fault", state.Fault ?? "none");
                    var declaration = server.Definition.Machines.First(row => row.Name == name);
                    foreach (var binding in declaration.Memory ?? []) {
                        var observed = server.MachineBindingState(name, binding.Name);
                        echo = echo.Segment().Head(name).Field("binding", binding.Name)
                            .Field("access", observed?.Status.ToString() ?? "Unavailable")
                            .Field("value", observed?.LastValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unobserved")
                            .Field("reason", observed?.Reason ?? "none");
                    }
                }
                return new CommandResult(Output: (found ? echo : echo.Head("none")).Close());
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "machine.operation",
            description: "Submits one generic provider operation through the ordered domain: machine.operation <instance> <generation> <operationId> <json>. The detached payload completes as Applied, Unsupported, Refused, or Faulted.",
            handler: Operation
        );
    }
}
