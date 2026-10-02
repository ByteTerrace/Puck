using Puck.Abstractions.Presentation;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateSkyLayerCommon(WorldRenderSkyLayer layer, WorldDefinition definition, string path, List<string> errors) {
        RenderScalar(layer.Opacity, definition, path + ".opacity", errors, 0f, 1f);
        if ((layer.Blend is { } blend) && !Enum.IsDefined(blend)) { errors.Add($"{path}.blend names an undeclared sky blend."); }
        if ((layer.Visibility is { } visibility) && !Enum.IsDefined(visibility)) { errors.Add($"{path}.visibility must be camera, lighting or both."); }
        if ((layer.Tier is { } tier) && !Enum.IsDefined(tier)) { errors.Add($"{path}.tier must be one of {string.Join(", ", QualityTiers.Names)}."); }
        if ((layer.Clock is { } clock) && (new WorldValueResolver(definition, default).Clock(clock) is null)) {
            errors.Add($"{path}.clock names undeclared clock '{clock}'.");
        }
        if (layer.Transform is { } transform) {
            RenderScalar(transform.Yaw?.Value, definition, path + ".transform.yaw", errors);
            RenderScalar(transform.Pitch?.Value, definition, path + ".transform.pitch", errors);
            RenderScalar(transform.Roll?.Value, definition, path + ".transform.roll", errors);
            RenderScalar(transform.Rate, definition, path + ".transform.rate", errors);
        }
        if (layer.Mask is not { } mask) { return; }
        if ((mask.Elevation is null) == (mask.Cone is null)) {
            errors.Add($"{path}.mask requires exactly one elevation band or cone.");
            return;
        }
        if (mask.Elevation is { } elevation) {
            if (elevation.Count != 2) { errors.Add($"{path}.mask.elevation requires exactly two angles."); return; }
            var before = errors.Count;
            RenderScalar(elevation[0].Value, definition, path + ".mask.elevation[0]", errors, -MathF.PI / 2f, MathF.PI / 2f);
            RenderScalar(elevation[1].Value, definition, path + ".mask.elevation[1]", errors, -MathF.PI / 2f, MathF.PI / 2f);
            if (before == errors.Count) {
                var values = new WorldValueResolver(definition, default);
                var low = (float)values.Angle(elevation[0], 0d);
                var high = (float)values.Angle(elevation[1], 0d);

                if (low > high) { errors.Add($"{path}.mask.elevation resolves initially to [{low}, {high}] radians; its lower end must not exceed its upper end."); }
            }
        }
        if (mask.Cone is { } cone) {
            RenderDirection(cone.Toward, definition, path + ".mask.cone.toward", errors);
            RenderScalar(cone.Radius.Value, definition, path + ".mask.cone.radius", errors, 0f, MathF.PI, positive: true);
        }
    }
    private static void ValidateStarsShape(WorldRenderSkyLayer.Stars stars, WorldDefinition definition, string path, List<string> errors) {
        RenderScalar(stars.Sparsity, definition, path + ".sparsity", errors, 0f, 1f);
        RenderScalar(stars.Inset, definition, path + ".inset", errors, 0f, .5f);
        RenderScalar(stars.RadiusFraction, definition, path + ".radiusFraction", errors, 0f, positive: true);
        RenderScalar(stars.LuminosityFloor, definition, path + ".luminosityFloor", errors, 0f, 1f, positive: true);
        RenderScalar(stars.RadiusFloor, definition, path + ".radiusFloor", errors, 0f, 1f);
        ValidateSkyTuple(WorldSkyNumeric.Stars(stars), definition, path, errors);
        if (stars.Spectrum is not { } spectrum) { return; }
        if (spectrum.Count != 7) { errors.Add($"{path}.spectrum requires exactly seven colors."); }
        for (var index = 0; index < spectrum.Count; index++) {
            RequireBindableColor(spectrum[index], definition, $"{path}.spectrum[{index}]", errors);
        }
    }
    private static void ValidateCloudShape(WorldRenderSkyLayer.Clouds clouds, WorldDefinition definition, string path, List<string> errors) {
        RenderScalar(clouds.DomeRadius, definition, path + ".domeRadius", errors, 0f);
        RenderScalar(clouds.Warp, definition, path + ".warp", errors, 0f);
        RenderScalar(clouds.Height, definition, path + ".height", errors, 0f);
        RenderScalar(clouds.NormalTap, definition, path + ".normalTap", errors, 0f, positive: true);
        RenderScalar(clouds.SelfShadow, definition, path + ".selfShadow", errors, 0f, 1f);
        RenderScalar(clouds.SilverLining, definition, path + ".silverLining", errors, 0f);
        RenderScalar(clouds.Extinction, definition, path + ".extinction", errors, 0f);
        RenderScalar(clouds.HorizonFade, definition, path + ".horizonFade", errors, 0f, 1f, positive: true);
        RenderScalar(clouds.AmbientFloor, definition, path + ".ambientFloor", errors, 0f, 1f);
        RenderScalar(clouds.SilverExponent, definition, path + ".silverExponent", errors, 0f);
        ValidateSkyTuple(WorldSkyNumeric.Clouds(clouds), definition, path, errors);
    }
}
