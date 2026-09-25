using System.Globalization;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>gpu.faults</c> verb: the operator's arming of the host's <see cref="GpuCreationFaults"/>, the creation faults
/// every device's services pass through. <c>arm &lt;kind&gt; [&lt;n&gt;]</c> fails the nth creation of a kind counted
/// from now (the next when n is absent), <c>disarm</c> clears every armed fault and every count, and <c>list</c>
/// prints them. Every form answers with one line: <c>[gpu.faults: armed=&lt;kind&gt;:&lt;n&gt;,… &lt;kind&gt;=&lt;seen&gt; …]</c>,
/// where <c>armed</c> lists each armed kind with the number, counted from now, of the creation that fails (or
/// <c>none</c>), followed by one field per kind counting its creations since the last disarm, failed ones included.
/// The verb answers the operator alone (<see cref="CommandAudience.Operator"/>), so no seat, binding, schedule step,
/// addon, peer or world document can reach it.
/// </summary>
/// <param name="faults">The host's creation faults.</param>
public sealed class GpuFaultsCommandModule(GpuCreationFaults faults) : ICommandModule {
    private const string Usage = "[gpu.faults: expected gpu.faults arm <kind> [<n>] | disarm | list; kinds: pipeline, buffer, image, render-pass, framebuffer, shader-module, command-pool, bindings-pool]";
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

        if (!any) {
            _ = text.Append(value: "none");
        }

        foreach (var kind in GpuCreationFaults.Kinds) {
            _ = text.Append(value: ' ').Append(value: GpuCreationFaults.NameOf(kind: kind)).Append(value: '=').Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{faults.SeenOf(kind: kind)}"
            );
        }

        return new CommandResult(Output: text.Append(value: ']').ToString());
    }
    private CommandResult Handle(WireArgs args) {
        if (args.Is(
            index: 0,
            value: "arm"
        )) {
            return Arm(args: args);
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
            description: $"Arms, clears and lists the creation faults every GPU device's services pass through (operator only): gpu.faults arm <kind> [<n>] | disarm | list. arm fails the nth creation of a kind counted from now (default 1, the next), replacing that kind's armed fault; the failed call throws {GpuCreationFaults.RefusalCode} before it reaches the device, so nothing is created, and the fault fires once. Kinds: pipeline, buffer, image, render-pass, framebuffer, shader-module, command-pool, bindings-pool. disarm clears every armed fault and every count. Each form prints armed=<kind>:<n>,… (the creation, counted from now, that fails) or armed=none, then <kind>=<seen> per kind: the creations since the last disarm, failed ones included. Counts are creation calls in arrival order: no randomness and no clock.",
            handler: (_, args) => Handle(args: args),
            name: Verb
        );
    }
}
