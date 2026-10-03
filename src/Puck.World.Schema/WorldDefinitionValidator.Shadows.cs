namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateShadowPolicies(WorldRenderDefaults render, List<string> errors) {
        ValidateShadowPolicy(render.ShadowLights, render.ShadowFadeSlots, render.ShadowFadeTicks, render.ShadowOverflow, "render", errors);
        ValidateShadowPreset(render.LowRaw, "render.low", errors);
        ValidateShadowPreset(render.MediumRaw, "render.medium", errors);
        ValidateShadowPreset(render.HighRaw, "render.high", errors);
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
