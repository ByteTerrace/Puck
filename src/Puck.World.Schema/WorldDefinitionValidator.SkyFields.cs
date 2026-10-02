namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void SkyColor(BindableColor? value, WorldDefinition definition, string path, List<string> errors) {
        if (value is { } color) { RequireBindableColor(color, definition, path, errors); }
    }
    private static void SkyOctaves(int? value, string path, List<string> errors) {
        if (value is < 1 or > 8) { errors.Add($"{path} must be an integer from one through eight; the count is structural."); }
    }
    private static void ValidateSkyNoise(WorldRenderSkyLayer.Noise noise, WorldDefinition definition, string path, List<string> errors) {
        SkyColor(noise.ColorLow, definition, path + ".colorLow", errors);
        SkyColor(noise.ColorHigh, definition, path + ".colorHigh", errors);
        RenderScalar(noise.Intensity, definition, path + ".intensity", errors, 0f);
        RenderScalar(noise.Scale, definition, path + ".scale", errors, 0f, positive: true);
        RenderVector(noise.Offset, definition, path + ".offset", errors);
        RenderScalar(noise.Contrast, definition, path + ".contrast", errors);
        RenderScalar(noise.Bias, definition, path + ".bias", errors);
        SkyOctaves(noise.Octaves, path + ".octaves", errors);
        ValidateSkyTuple(WorldSkyNumeric.Noise(noise), definition, path, errors);
    }
    private static void ValidateSkyPattern(WorldRenderSkyLayer.Pattern pattern, WorldDefinition definition, string path, List<string> errors) {
        if (pattern.Checker?.Cells is < 1) { errors.Add($"{path}.checker.cells must be a positive integer."); }
        if (pattern.Colors is { } colors) {
            if (colors.Count != 2) { errors.Add($"{path}.colors requires exactly two colors."); }
            for (var index = 0; index < colors.Count; index++) { SkyColor(colors[index], definition, $"{path}.colors[{index}]", errors); }
        }
        RenderScalar(pattern.Intensity, definition, path + ".intensity", errors, 0f);
        RenderPair(pattern.Offset, definition, path + ".offset", errors);
    }
    private static void ValidateSkyAurora(WorldRenderSkyLayer.Aurora aurora, WorldDefinition definition, string path, List<string> errors) {
        SkyColor(aurora.Color, definition, path + ".color", errors);
        RenderScalar(aurora.Intensity, definition, path + ".intensity", errors, 0f);
        RenderVector(aurora.Offset, definition, path + ".offset", errors);
        RenderScalar(aurora.Scale, definition, path + ".scale", errors, 0f, positive: true);
        RenderScalar(aurora.Width, definition, path + ".width", errors, 0f, positive: true);
        RenderScalar(aurora.Sharpness, definition, path + ".sharpness", errors, 0f, positive: true);
        RenderScalar(aurora.HeightScale, definition, path + ".heightScale", errors, 0f);
        RenderScalar(aurora.Bias, definition, path + ".bias", errors, -1f, 1f);
        SkyOctaves(aurora.Octaves, path + ".octaves", errors);
        ValidateSkyTuple(WorldSkyNumeric.Aurora(aurora), definition, path, errors);
    }

    private static void ValidateSkyTuple(WorldValueTuple tuple, WorldDefinition definition, string path, List<string> errors) {
        foreach (var operand in tuple.Operands) {
            if (operand.Value is { } value && operand.Predicate is { } predicate) {
                RequireBindableDomain(value, definition, path + "." + operand.Name, operand.Domain, errors,
                    predicate: predicate, requirement: operand.Requirement);
            }
        }
        Span<float> initial = stackalloc float[tuple.Operands.Length];
        tuple.Initial(new WorldValueResolver(definition, default), initial);
        for (var index = 0; index < initial.Length; index++) {
            if (!tuple.Operands[index].Domain.Contains(initial[index])) { return; }
        }
        if (tuple.Predicate(initial)) { return; }
        var fields = new string[initial.Length];
        for (var index = 0; index < initial.Length; index++) {
            var operand = tuple.Operands[index];
            fields[index] = FormattableString.Invariant($"{operand.Name}={initial[index]} from {WorldValueDomain.SourceOf(operand.Value ?? new BindableScalar(operand.Default))}");
        }
        errors.Add($"{path}.{tuple.Name} resolves initially to {string.Join(", ", fields)}; {tuple.Requirement}.");
    }

    private static void ValidateSkyPanorama(WorldRenderSkyLayer.Panorama panorama, WorldDefinition definition, string path, List<string> errors) {
        SkyColor(panorama.Tint, definition, path + ".tint", errors);
        RenderScalar(panorama.Intensity, definition, path + ".intensity", errors, 0f);
        if (panorama.Filter is { } filter && !Enum.IsDefined(filter)) { errors.Add($"{path}.filter must be nearest or linear."); }
        var cameras = definition.Cameras.Where(static camera => camera is not null)
            .Select(static camera => camera.Name).ToHashSet(StringComparer.Ordinal);
        ValidateFrameSource(definition, panorama.Source, path + ".source", cameras, errors);
    }
}
