using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <content>
/// <c>body.rig</c> — the read-back for a body-rooted creation look's live animation state. Everything it echoes is
/// presentation-side and lives only on <see cref="WorldStampPool"/>, so this reads the pool directly rather than
/// routing a query: there is no server-side answer to route to, and a routed one would have to invent the values.
/// </content>
internal sealed partial class PlayerCommandModule {
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

        // No instance form: a spawned instance runs its own simulation but no client mirrors it, so there is no
        // stamp pool holding its rig — saying so is the honest answer rather than echoing this world's values under
        // its name.
        if (instanceTarget.Instance is { } instance) {
            return CommandResult.Error(output: $"[body.rig: instance '{instance.Name}' has no client presentation, so it stamps no creation look — body.rig reads this world only]");
        }
        if (instanceTarget.EffectiveCount > 1) {
            return CommandResult.Error(output: "[body.rig: expected at most 1 value — an optional body index]");
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
        if (!m_stamps.TryBodyRig(
            bodyIndex: index,
            state: out var state
        ) || (state is null)) {
            return new CommandResult(Output: $"[body.rig: body:{index} no creation look — this body draws through the procedural catalog rig, which carries no drivers or effectors]");
        }

        var line = new StringBuilder();

        _ = line.Append(value: $"[body.rig: body:{index} creation={state.Creation} speed={Scalar(value: state.Speed)} drivers={state.Drivers.Count} effectors={state.Effectors.Count}");

        foreach (var driver in state.Drivers) {
            _ = line.Append(value: $" driver:{driver.Name} phase={Scalar(value: driver.Phase)} weight={Scalar(value: driver.Weight)}");
        }

        foreach (var effector in state.Effectors) {
            _ = line.Append(value: $" effector:{effector.Name} weight={Scalar(value: effector.Weight)} planted={(effector.Planted ? "yes" : "no")} target={((effector.Target is { } target)
                ? Point(value: target)
                : "none")}");

            if (effector.Bones < Puck.World.Authoring.CreationEffectorDocument.MinChainBones) {
                _ = line.Append(value: $" bones={effector.Bones}");
            }
        }

        _ = line.Append(value: ']');

        return new CommandResult(Output: line.ToString());
    }
}
