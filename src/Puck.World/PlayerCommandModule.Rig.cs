using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <content>
/// <c>body.rig</c> names authoritative input alongside a body-rooted creation look's live animation state.
/// Input answers headlessly too; animation reads the presentation's <see cref="WorldCreationInstances"/> pool.
/// </content>
internal sealed partial class PlayerCommandModule {
    private static readonly CommandForms BodyRigForms = CommandForms.Of(verb: "body.rig", RigForm);
    private static string Scalar(float value) => value.ToString(
        format: "0.00",
        provider: CultureInfo.InvariantCulture
    );
    private static string Point(Vector3 value) => $"({Scalar(value: value.X)}, {Scalar(value: value.Y)}, {Scalar(value: value.Z)})";
    private CommandResult RigHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "body.rig"
        )) {
            return tokenError!.Value;
        }

        if (instanceTarget.EffectiveCount > 2) {
            return BodyRigForms.Refuse();
        }
        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[body.rig: body index must be an integer 0..{(m_population.Capacity - 1)}]");
        }
        var shapeName = ((instanceTarget.EffectiveCount == 2) ? args[1].ToString() : null);
        // The input half is the routed authority's own read-back of the body, under the asking principal's Observe
        // grant: a named instance answers for itself, a seat for the authority it is routed to, any other body for
        // the boot world.
        if (instanceTarget.Instance is { } instance) {
            var instanceInput = WorldInputReadBack.Ask(
                caller: context.Principal,
                link: instance.SubmissionLink,
                query: new Puck.World.Protocol.WorldQuery.PlayerInput(Index: index)
            );

            return (instanceInput.Refused
                ? WorldInputReadBack.Result(answer: instanceInput)
                : new CommandResult(Output: $"[body.rig: body:{index} instance:{instance.Name} {instanceInput.Text} creation-rig=none]"));
        }
        var answer = ((index < PlayerRoster.MaxSlots)
            ? WorldInputReadBack.ForSeat(
                bootLink: m_link,
                caller: context.Principal,
                query: static entity => new Puck.World.Protocol.WorldQuery.PlayerInput(Index: entity),
                router: seatRouter,
                slot: index
            )
            : WorldInputReadBack.Ask(
                caller: context.Principal,
                link: m_link,
                query: new Puck.World.Protocol.WorldQuery.PlayerInput(Index: index)
            ));

        if (answer.Refused) {
            return WorldInputReadBack.Result(answer: answer);
        }

        var input = answer.Text;
        var pool = m_stamps;
        var bodyIndex = index;
        if ((index < PlayerRoster.MaxSlots) && (m_screens.Presenter is { } presenter) && presenter.TrySeatScene(index: out _, scene: out var scene, slot: index)) {
            pool = scene.BodyStamps;
            bodyIndex = seatRouter.Route(slot: index).EntityIndex;
        }

        if (
            !pool.TryBodyRig(
            bodyIndex: bodyIndex,
            shapeName: shapeName,
            state: out var state
        ) ||
            (state is null)
        ) {
            return new CommandResult(Output: $"[body.rig: body:{index} {input} creation-rig=none]");
        }

        var line = new StringBuilder();

        _ = line.Append(value: $"[body.rig: body:{index} {input} creation={state.Creation} speed={Scalar(value: state.Speed)} drivers={state.Drivers.Count} effectors={state.Effectors.Count}");

        foreach (var driver in state.Drivers) {
            _ = line.Append(value: $" driver:{driver.Name} phase={Scalar(value: driver.Phase)} weight={Scalar(value: driver.Weight)}");
        }

        foreach (var effector in state.Effectors) {
            _ = line.Append(value: $" effector:{effector.Name} weight={Scalar(value: effector.Weight)} planted={effector.Contact} target={((effector.Target is { } target)
                ? Point(value: target)
                : "none")}{((effector.Align is { } align)
                ? $" align={Scalar(value: align)} normal={Point(value: effector.Normal)}"
                : string.Empty)}");

            if (effector.Bones < Puck.World.Authoring.CreationEffectorDocument.MinChainBones) {
                _ = line.Append(value: $" bones={effector.Bones}");
            }
        }

        if (state.Shape is { } shape) {
            _ = line.Append(value: ((shape.Index < 0)
                ? $" shape:{shape.Name} none"
                : $" shape:{shape.Name} parent={(shape.Parent ?? "root")} delta={Point(value: shape.Delta)} turn={Scalar(value: shape.TurnRadians)}"));
        }

        _ = line.Append(value: ']');

        return new CommandResult(Output: line.ToString());
    }
}
