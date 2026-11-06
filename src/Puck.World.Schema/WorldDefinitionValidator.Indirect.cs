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
        if (indirect.LightingGrid is { } grid) {
            if (grid.DirectionDegrees is { } direction && (!float.IsFinite(direction) || direction < 0 || direction > 180)) { errors.Add("render.indirect.lightingGrid.directionDegrees must be finite and in [0, 180]."); }
            if (grid.Radiance is { } radiance && (!float.IsFinite(radiance) || radiance < 0 || radiance > 1)) { errors.Add("render.indirect.lightingGrid.radiance must be finite and in [0, 1]."); }
        }
        if (!Enum.IsDefined(value: indirect.Tier)) { errors.Add(item: "render.indirect.tier must be off, medium or high."); }
        if ((indirect.Bounces is { } bounces) && ((bounces < 0) || (bounces > SdfIndirectGains.MaximumBounces))) {
            errors.Add(item: $"render.indirect.bounces must be an integer from 0 to {SdfIndirectGains.MaximumBounces}; the selected tier caps its actual depth.");
        }
        if (indirect.Budget is { } budget) {
            if (budget.Relight is < 1) { errors.Add(item: "render.indirect.budget.relight must be at least one probe a frame; omit it to leave relighting unbounded."); }
            if (budget.Trace is < 1) { errors.Add(item: "render.indirect.budget.trace must be at least one probe a frame; omit it to leave tracing unbounded."); }
            if ((budget.Age is { } age) && (!float.IsFinite(f: age) || (age <= 0f))) { errors.Add(item: "render.indirect.budget.age must be a positive number of seconds."); }
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
