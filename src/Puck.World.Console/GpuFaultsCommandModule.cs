using System.Globalization;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>gpu.faults</c> verb: the operator's arming of the host's <see cref="GpuCreationFaults"/>, the creation faults
/// every device's services pass through. <c>arm &lt;kind&gt; [&lt;n&gt;]</c> fails the nth creation of a kind counted
/// from now (the next when n is absent), <c>lose [&lt;n&gt;]</c> loses the device on the nth frame a GPU host produces
/// from now, <c>disarm</c> clears every armed fault, the armed loss and every count, and <c>list</c> prints them.
/// Every form answers with one line:
/// <c>[gpu.faults: armed=&lt;kind&gt;:&lt;n&gt;,…,lose:&lt;n&gt; &lt;kind&gt;=&lt;seen&gt; … frames=&lt;seen&gt;]</c>, where
/// <c>armed</c> lists each armed kind with the number, counted from now, of the creation that fails, then the armed
/// loss with the number of the frame that loses the device (or <c>none</c>), followed by one field per kind counting
/// its creations since the last disarm, failed ones included, and the frames counted since then.
/// The verb answers the operator alone (<see cref="CommandAudience.Operator"/>), so no seat, binding, schedule step,
/// addon, peer or world document can reach it.
/// </summary>
/// <param name="faults">The host's creation faults.</param>
public sealed class GpuFaultsCommandModule(GpuCreationFaults faults) : ICommandModule {
    private const string Usage = "[gpu.faults: expected gpu.faults arm <kind> [<n>] | lose [<n>] | disarm | list; kinds: pipeline, buffer, image, render-pass, framebuffer, shader-module, command-pool, bindings-pool]";
    private const string Verb = "gpu.faults";

    private CommandResult Arm(WireArgs args) {
        if (
            (args.Count is < 2 or > 3) ||
            !GpuCreationFaults.TryParseKind(
                kind: out var kind,
                name: args[1]
            )
        ) {
            return CommandResult.Error(output: Usage);
        }

        var nth = 1;

        if (
            (args.Count == 3) &&
            (!args.TryInt(
                index: 2,
                value: out nth
            ) || (nth < 1))
        ) {
            return CommandResult.Error(output: Usage);
        }

        faults.Arm(
            kind: kind,
            nth: nth
        );

        return Describe();
    }
    private CommandResult Describe() {
        var text = new StringBuilder(value: $"[{Verb}: armed=");
        var any = false;

        foreach (var kind in GpuCreationFaults.Kinds) {
            if (faults.TryGetArmed(
                kind: kind,
                remaining: out var remaining
            )) {
                _ = text.Append(value: (any ? "," : string.Empty)).Append(value: GpuCreationFaults.NameOf(kind: kind)).Append(value: ':').Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{remaining}"
                );
                any = true;
            }
        }

        if (faults.TryGetArmedLoss(remaining: out var frames)) {
            _ = text.Append(value: (any ? "," : string.Empty)).Append(value: "lose:").Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{frames}"
            );
            any = true;
        }

        if (!any) {
            _ = text.Append(value: "none");
        }

        foreach (var kind in GpuCreationFaults.Kinds) {
            _ = text.Append(value: ' ').Append(value: GpuCreationFaults.NameOf(kind: kind)).Append(value: '=').Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{faults.SeenOf(kind: kind)}"
            );
        }

        _ = text.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" frames={faults.FramesSeen}"
        );

        return new CommandResult(Output: text.Append(value: ']').ToString());
    }
    private CommandResult Lose(WireArgs args) {
        var nth = 1;

        if (
            (args.Count > 2) ||
            ((args.Count == 2) && (!args.TryInt(
                index: 1,
                value: out nth
            ) || (nth < 1)))
        ) {
            return CommandResult.Error(output: Usage);
        }

        faults.ArmLoss(nth: nth);

        return Describe();
    }
    private CommandResult Handle(WireArgs args) {
        if (args.Is(
            index: 0,
            value: "arm"
        )) {
            return Arm(args: args);
        }

        if (args.Is(
            index: 0,
            value: "lose"
        )) {
            return Lose(args: args);
        }

        if (args.Count != 1) {
            return CommandResult.Error(output: Usage);
        }

        if (args.Is(
            index: 0,
            value: "disarm"
        )) {
            faults.Disarm();

            return Describe();
        }

        return (args.Is(
            index: 0,
            value: "list"
        )
            ? Describe()
            : CommandResult.Error(output: Usage)
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Arms, clears and lists the creation faults every GPU device's services pass through, and the device loss GPU hosts count their frames against (operator only): gpu.faults arm <kind> [<n>] | lose [<n>] | disarm | list. arm fails the nth creation of a kind counted from now (default 1, the next), replacing that kind's armed fault; the failed call throws {GpuCreationFaults.RefusalCode} before it reaches the device, so nothing is created, and the fault fires once. Kinds: pipeline, buffer, image, render-pass, framebuffer, shader-module, command-pool, bindings-pool. lose loses the device on the nth frame a GPU host produces from now (default 1, the next): the frame throws {GpuCreationFaults.LossRefusalCode} as a device loss on a healthy device, and the host recovers through its device-loss policy, refusing every capture armed at the loss; the loss fires once. disarm clears every armed fault, the armed loss and every count. Each form prints armed=<kind>:<n>,…,lose:<n> (the creation or frame, counted from now, that fails) or armed=none, then <kind>=<seen> per kind: the creations since the last disarm, failed ones included, then frames=<seen>, the frames counted since then. Counts are creation calls and frames in arrival order: no randomness and no clock.",
            handler: (_, args) => Handle(args: args),
            name: Verb
        );
    }
}
