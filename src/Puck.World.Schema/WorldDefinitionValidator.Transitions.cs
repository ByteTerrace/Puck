namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // A look's form effect (WorldFormEffect): positive sweep and noise wavelength, no negative time, length, light or
    // count, shares in [0, 1] and hex colours.
    private static void ValidateFormEffect(WorldFormEffect? effect, string path, List<string> errors) {
        if (effect is null) { return; }
        RequirePositive(value: effect.Seconds, name: new Subject(Member: ".seconds", Path: path), errors: errors);
        RequirePositive(value: effect.RoughnessScale, name: new Subject(Member: ".roughnessScale", Path: path), errors: errors);
        foreach (var (value, member) in (ReadOnlySpan<(float, string)>)[
            (effect.Delay, ".delay"), (effect.EdgeLuminance, ".edgeLuminance"), (effect.EdgeWidth, ".edgeWidth"), (effect.Roughness, ".roughness"),
            (effect.MoteLuminance, ".moteLuminance"), (effect.MoteSize, ".moteSize"), (effect.MoteSeconds, ".moteSeconds"), (effect.MoteSpeed, ".moteSpeed"),
            (effect.PieceSpeed, ".pieceSpeed"), (effect.PieceSpin, ".pieceSpin"), (effect.PieceGravity, ".pieceGravity"), (effect.PieceReach, ".pieceReach"),
            (effect.PieceSeconds, ".pieceSeconds"), (effect.PieceStagger, ".pieceStagger"),
        ]) {
            RequireNonNegative(value: value, name: new Subject(Member: member, Path: path), errors: errors);
        }
        if (!float.IsFinite(f: effect.MoteRise) || !float.IsFinite(f: effect.MoteSwirl)) { errors.Add(item: $"{path}.moteRise and .moteSwirl must be finite."); }
        if (effect.Motes < 0) { errors.Add(item: $"{path}.motes {effect.Motes} is negative."); }
        if (!((effect.PieceLift >= 0f) && (effect.PieceLift <= 1f))) { errors.Add(item: $"{path}.pieceLift {effect.PieceLift} is not a share in [0, 1]."); }
        if (!((effect.PieceBounce >= 0f) && (effect.PieceBounce <= 1f))) { errors.Add(item: $"{path}.pieceBounce {effect.PieceBounce} is not a share in [0, 1]."); }
        if ((effect.EdgeColor is not null) && !IsHexColor(value: effect.EdgeColor)) { errors.Add(item: $"{path}.edgeColor '{effect.EdgeColor}' is not a #RRGGBB colour."); }
        if ((effect.MoteColor is not null) && !IsHexColor(value: effect.MoteColor)) { errors.Add(item: $"{path}.moteColor '{effect.MoteColor}' is not a #RRGGBB colour."); }
        if ((effect.Direction is { } direction) && !(direction.Value.LengthSquared() > 0f)) { errors.Add(item: $"{path}.direction is not a direction."); }
    }
}
