using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldRenderLeverCommandModule {
    private string SkyQualityName() => ((settings.SkyQuality is { } tier) ? QualityTiers.Name(tier: tier) : "auto");
    private CommandDefinition SkyQualityCommand() => CommandDefinition.WithWireArgs(
        name: "world.sky.quality",
        description: "Sets live sky quality: world.sky.quality [low|medium|high|auto]. Auto resumes each world's authored quality. Bare echoes the override; this inspection lever is excluded from save and replay.",
        bindability: CommandBindability.Unbindable,
        handler: (context, args) => {
            if (args.Count == 0) { return new CommandResult(Output: $"[world.sky.quality: {SkyQualityName()}]"); }
            if (args.Count != 1) { return CommandResult.Usage(form: "[low|medium|high|auto]", verb: "world.sky.quality"); }
            var tier = QualityTiers.Parse(name: args[0].ToString());

            if ((tier is null) && !args.Is(index: 0, value: "auto")) {
                return CommandResult.Error(output: $"[world.sky.quality: unknown '{args[0]}' — low|medium|high|auto]");
            }
            return SubmitLever(
                link: link,
                principal: context.Principal,
                name: WorldSessionLevers.SkyQuality,
                a: ((tier is { } value) ? (double)value : WorldSessionLevers.SkyQualityAuto),
                formatEcho: () => new CommandResult(Output: $"[world.sky.quality: {SkyQualityName()}]")
            );
        }
    );
}
