using System.Globalization;
using Puck.Commands;
using Puck.SdfVm;

namespace Puck.World;

internal sealed partial class WorldInspectionCommandModule {
    /// <summary>Gets or sets the host's late-result fan-out, including named cancellation refusals.</summary>
    public Action<CommandResult>? Report { get; set; }

    private CommandResult Explain(CommandContext context, WireArgs args) {
        if (args.Count != 0) { return CommandResult.Usage(form: "", verb: "world.explain"); }
        if ((cursor is null) || (gpu is null)) { return CommandResult.Error(output: "[world.explain: requires GPU presentation]"); }
        var result = cursor.Explain(context.Slot, DescribeExplanation);

        if (result.Settlement is not { } settlement) { return result; }
        context.TextSession?.HoldWhile(hold: () => !settlement.IsSettled);
        return CommandResult.Settling(settlement, late: verdict => Report?.Invoke(verdict));
    }
    private static CommandResult DescribeExplanation(SdfPickResult answer) => new(string.Create(CultureInfo.InvariantCulture,
        $"[world.explain: request={answer.Request} pixel={answer.X},{answer.Y}/{answer.Width},{answer.Height} material={(answer.MaterialName ?? "unavailable")}]"));
}
