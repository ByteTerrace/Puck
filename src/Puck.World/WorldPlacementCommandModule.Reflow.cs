using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class WorldPlacementCommandModule {
    private CommandResult ReflowQuery(WorldQuery query, WorldPrincipal principal) {
        if (link is not IPrincipalServerLink attributed) {
            return CommandResult.Error(output: "[world.reflow: this link cannot attribute authoring queries]");
        }
        var result = default(CommandResult);

        attributed.Query(
            query,
            principal,
            answer => result = new CommandResult(answer.Text) { IsError = answer.Refused }
        );
        return result;
    }
    private IEnumerable<CommandDefinition> ReflowCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.reflow.preview",
            description: "Starts bounded background planning. Usage: world.reflow.preview <template|request-json>. A request can include seed edits and an explicit placement group. Review status before committing; previews expire after five minutes.",
            handler: (context, args) => {
                if (args.Count == 0) { return CommandResult.Error(output: "usage: world.reflow.preview <template|request-json>"); }
                WorldPlacementReflowRequest request;
                var text = WorldCommandArguments.Raw(
                    args: args,
                    context: context
                );

                if (text.StartsWith(value: '{')) {
                    try {
                        request = (JsonSerializer.Deserialize<WorldPlacementReflowRequest>(
                            json: text,
                            options: WorldJsonContext.Default.Options
                        )
                            ?? throw new JsonException(message: "a request is required"));
                    } catch (JsonException exception) {
                        return CommandResult.Error(output: $"[world.reflow: {exception.Message}]");
                    }
                } else {
                    if (args.Count != 1) { return CommandResult.Error(output: "usage: world.reflow.preview <template|request-json>"); }
                    request = new WorldPlacementReflowRequest(TemplateId: text);
                }
                return ReflowQuery(
                    new WorldQuery.ReflowPreview(Request: request),
                    context.ActingPrincipal()
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.reflow.status",
            description: "Reviews proposed positions, growth, price, and constraints. Usage: world.reflow.status.",
            handler: (context, args) => ((args.Count == 0)
            ? ReflowQuery(
                    new WorldQuery.ReflowStatus(),
                    context.ActingPrincipal()
                )
            : CommandResult.Error(output: "usage: world.reflow.status"))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.reflow.cancel",
            description: "Discards the cached plan without changing the world or paying. Usage: world.reflow.cancel.",
            handler: (context, args) => ((args.Count == 0)
            ? ReflowQuery(
                    new WorldQuery.ReflowCancel(),
                    context.ActingPrincipal()
                )
            : CommandResult.Error(output: "usage: world.reflow.cancel"))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.reflow.commit",
            routing: CommandRouting.Simulation,
            description: "Submits the reviewed growth, layout, and payment as one guarded batch. Usage: world.reflow.commit.",
            handler: (context, args) => {
                if (args.Count != 0) { return CommandResult.Error(output: "usage: world.reflow.commit"); }
                if (!server.TryTakeReviewedReflow(
                    principal: context.ActingPrincipal(),
                    proposal: out var proposal,
                    reason: out var reason
                )) {
                    return CommandResult.Error(output: $"[world.reflow: {reason}]");
                }
                return link.Submit(
                    proposal!.Mutation,
                    echoes,
                    "world.reflow.commit"
                );
            }
        );
    }
}
