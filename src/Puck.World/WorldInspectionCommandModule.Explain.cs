using System.Globalization;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldInspectionCommandModule {
    private readonly WorldIndirectPickText m_indirectText = new();

    private CommandResult Explain(CommandContext context, WireArgs args) {
        if (args.Count != 0) { return CommandResult.Usage(verb: "world.explain", form: ""); }
        if (cursor is null || gpu is null) { return CommandResult.Error("[world.explain: requires GPU presentation]"); }
        return cursor.Explain(context.Slot, DescribeExplanation);
    }

    private CommandResult DescribeExplanation(SdfPickResult answer) {
        var reference = answer.Indirect is { } indirect ? WorldIndirectReference.Evaluate(indirect) : null;
        cursor!.RetainReference(reference);
        return new CommandResult(string.Create(CultureInfo.InvariantCulture,
            $"[world.explain: request={answer.Request} pixel={answer.X},{answer.Y}/{answer.Width},{answer.Height} material={answer.MaterialName ?? "unavailable"}\n{m_indirectText.Read(answer.Indirect, reference)}]"));
    }
}
