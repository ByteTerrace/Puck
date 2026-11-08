using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class WorldPlacementCommandModule {
    private static readonly CommandForms WorldReflowPreviewForms = CommandForms.Of(verb: "world.reflow.preview", "<template|request-json>");

    private CommandResult ReflowQuery(WorldQuery query, Principal principal) {
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
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.reflow.preview",
            description: $"Starts bounded background planning: {WorldReflowPreviewForms.Spelling}. A request can include seed edits and an explicit placement group. Review status before committing; previews expire after five minutes.",
            forms: WorldReflowPreviewForms,
            handler: (context, args) => {
                if (args.Count == 0) { return WorldReflowPreviewForms.Refuse(args: in args); }
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
                    if (args.Count != 1) { return WorldReflowPreviewForms.Refuse(args: in args); }
                    request = new WorldPlacementReflowRequest(TemplateId: text);
                }
                return ReflowQuery(
                    new WorldQuery.ReflowPreview(Request: request),
                    context.Principal
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.reflow.status",
            description: "Reviews proposed positions, growth, price, and constraints.",
            handler: (context, args) => (CommandResult.RequireNoArguments(args: in args, verb: "world.reflow.status")
                ?? ReflowQuery(
                    new WorldQuery.ReflowStatus(),
                    context.Principal
                ))
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.reflow.cancel",
            description: "Discards the cached plan without changing the world or paying.",
            handler: (context, args) => (CommandResult.RequireNoArguments(args: in args, verb: "world.reflow.cancel")
                ?? ReflowQuery(
                    new WorldQuery.ReflowCancel(),
                    context.Principal
                ))
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "world.reflow.commit",
            routing: CommandRouting.Simulation,
            description: "Submits the reviewed growth, layout, and payment as one guarded batch.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args: in args, verb: "world.reflow.commit") is { } refusal) { return refusal; }
                if (!server.TryTakeReviewedReflow(
                    principal: context.Principal,
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
