using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <content>
/// <c>body.rig</c> names authoritative input alongside a body-rooted creation look's live animation state.
/// Input and the seat's guess readings answer headlessly too; animation reads the presentation's
/// <see cref="WorldCreationInstances"/> pool.
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
            max: (Population.Capacity - 1),
            fallback: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[body.rig: body index must be an integer 0..{(Population.Capacity - 1)}]");
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

        var input = (answer.Text + ((index < PlayerRoster.MaxSlots) ? GuessReadings(slot: index) : string.Empty));
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
            _ = line.Append(value: $" driver:{driver.Name} phase={Scalar(value: driver.Phase)} weight={Scalar(value: driver.Weight)}{((driver.Source is { } source) ? $" source={source}" : string.Empty)}");
        }

        foreach (var effector in state.Effectors) {
            _ = line.Append(value: $" effector:{effector.Name} weight={Scalar(value: effector.Weight)} planted={effector.Contact} target={((effector.Target is { } target)
                ? Point(value: target)
                : "none")}{((effector.Align is { } align)
                ? $" align={Scalar(value: align)} normal={Point(value: effector.Normal)}"
                : string.Empty)} applied={Scalar(value: effector.Applied)} error={((effector.Error is { } error) ? Scalar(value: error) : "none")}");

            if (effector.Bones < Puck.World.Authoring.CreationEffectorDocument.MinChainBones) {
                _ = line.Append(value: $" bones={effector.Bones}");
            }
        }

        if (state.Shape is { } shape) {
            _ = line.Append(value: ((shape.Index < 0)
                ? $" shape:{shape.Name} none"
                : $" shape:{shape.Name} parent={(shape.Parent ?? "root")} delta={Point(value: shape.Delta)} turn={Scalar(value: shape.TurnRadians)}"));
        }

        // The seat's guess layer, the pose its own view alone draws; the drivers above are the shared pose every other
        // reader sees. The read-back above already passed the seat's own input disclosure.
        if (pool.TryGuessRig(bodyIndex: bodyIndex, slot: out _, state: out var guessed) && (guessed is not null)) {
            foreach (var driver in guessed.Drivers) {
                if (driver.Source is { } source) { _ = line.Append(value: $" layer:{driver.Name} phase={Scalar(value: driver.Phase)} source={source}"); }
            }
        }

        _ = line.Append(value: ']');

        return new CommandResult(Output: line.ToString());
    }
    // One reading for each led driver the world's creations author: whether this client offers the seat a guess and, when
    // it does, what the guess is and the figures it was decided from. The reading is the client's, so it answers headlessly,
    // where no creation is posed.
    private string GuessReadings(int slot) {
        var readings = new StringBuilder();

        foreach (var creation in m_client.Definition.Creations) {
            foreach (var driver in (creation.Document.Drivers ?? [])) {
                if (driver.Lead is not { } channel) { continue; }

                _ = readings.Append(value: $" guess:{creation.Id}.{driver.Name}[{channel}]={m_client.DescribeGuess(slot: slot, channel: channel)}");
            }
        }

        return readings.ToString();
    }
}
