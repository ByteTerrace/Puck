using Puck.Abstractions.Presentation;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateShadowPolicies(WorldRenderDefaults render, List<string> errors) {
        ValidateShadowPolicy(render.ShadowLights, render.ShadowFadeSlots, render.ShadowFadeTicks, render.ShadowOverflow, "render", errors);
        ValidateShadowPreset(render.LowRaw, "render.low", errors);
        ValidateShadowPreset(render.MediumRaw, "render.medium", errors);
        ValidateShadowPreset(render.HighRaw, "render.high", errors);
        ValidateDeviceProfiles(render, errors);
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
            if (render.Preset(tier: profile.Quality) is null) {
                var tier = QualityTiers.Name(tier: profile.Quality);

                errors.Add(item: $"{path}.quality names the {tier} table, which render.{tier} does not author.");
            }
            if (profile.DeviceMemoryMiB is <= 0) {
                errors.Add(item: $"{path}.deviceMemoryMiB must be positive.");
            }
            if (profile.HostMemoryMiB is <= 0) {
                errors.Add(item: $"{path}.hostMemoryMiB must be positive.");
            }
        }
    }
    private static void ValidateShadowPreset(WorldQualityPreset? preset, string path, List<string> errors) {
        if (preset is { } value) {
            ValidateShadowPolicy(value.ShadowLights, value.ShadowFadeSlots, value.ShadowFadeTicks, value.ShadowOverflow, path, errors);
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
