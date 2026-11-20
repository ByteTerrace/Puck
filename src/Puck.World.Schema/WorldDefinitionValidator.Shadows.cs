using Puck.Abstractions.Presentation;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateShadowPolicies(WorldRenderDefaults render, List<string> errors) {
        ValidateShadowPolicy(render.ShadowLights, render.ShadowFadeSlots, render.ShadowFadeTicks, render.ShadowOverflow, "render", errors);
        foreach (var tier in Enum.GetValues<QualityTier>()) {
            if (render.Preset(tier: tier) is { } preset) { ValidatePreset(preset: preset, path: $"render.{QualityTiers.Name(tier: tier)}", errors: errors); }
        }
        ValidateDeviceProfiles(render, errors);
    }
    // Every quality table, a tier's preset or a device's own, is judged by this one rule set.
    private static void ValidatePreset(WorldQualityPreset preset, string path, List<string> errors) {
        ValidateShadowPolicy(preset.ShadowLights, preset.ShadowFadeSlots, preset.ShadowFadeTicks, preset.ShadowOverflow, path, errors);
        RequireRange(preset.RenderScale, 0.125f, 1f, $"{path}.renderScale", errors);
        ValidateSkyFieldScale(preset.SkyFieldScale, $"{path}.skyFieldScale", errors);
        if (preset.DistantShadows?.Refusal(path: $"{path}.distantShadows") is { } distant) { errors.Add(item: distant); }
        if (preset.FarShadows?.Refusal(path: $"{path}.farShadows") is { } far) { errors.Add(item: far); }
        if ((preset.Indirect is { } chosen) && !Enum.IsDefined(value: chosen)) { errors.Add(item: $"{path}.indirect must be off, medium or high."); }
    }
    private static void ValidateDeviceProfiles(WorldRenderDefaults render, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var profile in render.Devices ?? []) {
            var path = $"render.devices[{profile.Name}]";

            if (string.IsNullOrWhiteSpace(value: profile.Name) || !names.Add(item: profile.Name)) {
                errors.Add(item: $"{path}: a device profile needs a name no other profile in the world carries.");
            }
            if (string.IsNullOrWhiteSpace(value: profile.Match)) {
                errors.Add(item: $"{path}.match must name text the adapter's name contains.");
            }
            if (profile.Table is { } table) {
                ValidatePreset(preset: table, path: $"{path}.table", errors: errors);
            } else if (render.Preset(tier: profile.Quality) is null) {
                var tier = QualityTiers.Name(tier: profile.Quality);

                errors.Add(item: $"{path}.quality names the {tier} table, which render.{tier} does not author and the profile authors no table of its own.");
            }
            if (profile.DeviceMemoryMiB is <= 0) {
                errors.Add(item: $"{path}.deviceMemoryMiB must be positive.");
            }
            if (profile.HostMemoryMiB is <= 0) {
                errors.Add(item: $"{path}.hostMemoryMiB must be positive.");
            }
        }
    }
    private static void ValidateShadowPolicy(int slots, int fadeSlots, uint fadeTicks, WorldShadowOverflow overflow, string path, List<string> errors) {
        if (slots is < 0 or > 4) {
            errors.Add(item: $"{path}.shadowLights must be between 0 and 4.");
        }
        if (fadeSlots is < 0 or > 2) {
            errors.Add(item: $"{path}.shadowFadeSlots must be between 0 and 2.");
        }
        if ((slots > 0) && (fadeSlots == 0) && (fadeTicks > 0)) {
            errors.Add(item: $"{path}: shadowLights {slots} with shadowFadeSlots 0 and shadowFadeTicks {fadeTicks} never fades; give it a fade slot (shadowFadeSlots 1 or 2) or set shadowFadeTicks to 0 for instant handoffs.");
        }
        if ((fadeSlots > 0) && (fadeTicks == 0)) {
            errors.Add(item: $"{path}: shadowFadeSlots {fadeSlots} with shadowFadeTicks 0 reserves fade slots that never fade; set shadowFadeTicks above 0, or shadowFadeSlots to 0 for instant handoffs.");
        }
        if ((slots > 0) && (overflow == WorldShadowOverflow.Queue) && ((fadeSlots == 0) || (fadeTicks == 0))) {
            errors.Add(item: $"{path}: shadowOverflow queue with shadowLights {slots} never progresses while shadowFadeSlots is {fadeSlots} and shadowFadeTicks is {fadeTicks}; give it a fade slot and a positive shadowFadeTicks, or set shadowOverflow to instant.");
        }
    }
}
