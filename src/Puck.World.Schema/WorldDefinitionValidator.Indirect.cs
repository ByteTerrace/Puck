using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateIndirect(WorldDefinition definition, List<string> errors) {
        foreach (var tier in Enum.GetValues<Puck.Abstractions.Presentation.QualityTier>()) {
            if ((definition.Render.Preset(tier: tier)?.Indirect is { } chosen) && !Enum.IsDefined(value: chosen)) {
                errors.Add(item: $"render.{tier.ToString().ToLowerInvariant()}.indirect must be off, medium or high.");
            }
        }
        if (definition.Render.Indirect is not { } indirect) { return; }
        if (!Enum.IsDefined(value: indirect.Tier)) { errors.Add(item: "render.indirect.tier must be off, medium or high."); }
        if (!Enum.IsDefined(value: indirect.Bodies)) { errors.Add(item: "render.indirect.bodies must be default, cast, receive or off."); }
        if ((indirect.Bounces is { } bounces) && ((bounces < 0) || (bounces > SdfIndirectLayout.MaximumBounces))) {
            errors.Add(item: $"render.indirect.bounces must be an integer from 0 to {SdfIndirectLayout.MaximumBounces}; the selected tier caps its actual depth.");
        }
        var sources = indirect.Sources;

        JudgeScalar(WorldValueFields.IndirectLights, sources?.Lights, definition, "render.indirect.sources.lights", errors);
        JudgeScalar(WorldValueFields.IndirectEmission, sources?.Emission, definition, "render.indirect.sources.emission", errors);
        JudgeScalar(WorldValueFields.IndirectScreens, sources?.Screens, definition, "render.indirect.sources.screens", errors);
        JudgeScalar(WorldValueFields.IndirectSky, sources?.Sky, definition, "render.indirect.sources.sky", errors);
        JudgeScalar(WorldValueFields.IndirectFeedback, sources?.Feedback, definition, "render.indirect.sources.feedback", errors);
        JudgeScalar(WorldValueFields.IndirectIntensity, indirect.Apply?.Intensity, definition, "render.indirect.apply.intensity", errors);
        JudgeScalar(WorldValueFields.IndirectContact, indirect.Apply?.Contact, definition, "render.indirect.apply.contact", errors);
        JudgeColor(indirect.Apply?.Tint, definition, "render.indirect.apply.tint", errors);
    }
}
