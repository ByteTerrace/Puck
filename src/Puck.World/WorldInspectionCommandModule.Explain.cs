using System.Globalization;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldInspectionCommandModule {
    private readonly WorldIndirectPickText m_indirectText = new();

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
    private CommandResult DescribeExplanation(SdfPickResult answer) {
        var reference = ((answer.Indirect is { } indirect) ? WorldIndirectReference.Evaluate(indirect) : null);

        cursor!.RetainReference(reference: reference);
        return new CommandResult(string.Create(CultureInfo.InvariantCulture,
            $"[world.explain: request={answer.Request} pixel={answer.X},{answer.Y}/{answer.Width},{answer.Height} material={(answer.MaterialName ?? "unavailable")}\n{m_indirectText.Read(pick: answer.Indirect, reference: reference)}]"));
    }
}
