namespace Puck.World;

/// <summary>The <c>theme</c> section's validation: every <see cref="BindableColor"/>/<see cref="BindableScalar"/>
/// field checked for admissibility (a hex literal, or a state binding naming a compatible declared cell), plus the
/// document's own finite/range checks per field. A bindable scalar is judged against its field's declared domain
/// (<see cref="WorldValueFields"/>); <see cref="WorldThemeCapacity.TypeAbsoluteFloorSize"/> floors the plain-float type
/// sizes.</summary>
public static partial class WorldDefinitionValidator {
    private static void RequireBindableColor(BindableColor color, WorldDefinition definition, string path, List<string> errors) {
        if (!color.IsAuthorable(definition: definition)) {
            errors.Add(item: $"{path} '{color}' {BindableColor.Grammar}.");
        }
    }
    private static void RequireStyleColor(WorldThemeStyleColor color, WorldDefinition definition, string path, List<string> errors) {
        if ((color.Role is null) == (color.Color is null)) {
            errors.Add(item: $"{path} must author exactly one of role (a core color role) or color (its own color).");
        }

        if (color.Color is { } own) {
            RequireBindableColor(
                color: own,
                definition: definition,
                errors: errors,
                path: $"{path}.color"
            );
        }
    }
    private static void RequireScrim(WorldThemeScrim scrim, WorldDefinition definition, string path, List<string> errors) {
        RequireBindableColor(
            color: scrim.Color,
            definition: definition,
            errors: errors,
            path: $"{path}.color"
        );
        JudgeScalar(
            definition: definition,
            errors: errors,
            field: WorldValueFields.ScrimAlpha,
            path: $"{path}.alpha",
            scalar: scrim.Alpha
        );
    }
    private static void RequireTypeSize(float size, string path, List<string> errors) {
        RequirePositive(
            errors: errors,
            name: path,
            value: size
        );

        if (size < WorldThemeCapacity.TypeAbsoluteFloorSize) {
            errors.Add(item: $"{path} {size} is below the {WorldThemeCapacity.TypeAbsoluteFloorSize}px MSDF glyph coverage floor — below it glyph coverage degrades over a moving world.");
        }
    }
    private static void ValidateTheme(WorldDefinition definition, List<string> errors) {
        if (definition.ThemeRaw is not { } theme) {
            return;
        }

        var color = theme.Color;

        foreach (var (name, value) in ((ReadOnlySpan<(string, BindableColor)>)[
            ("surfaceBase", color.SurfaceBase),
            ("surfaceRaised", color.SurfaceRaised),
            ("surfaceInset", color.SurfaceInset),
            ("lineHair", color.LineHair),
            ("lineSoft", color.LineSoft),
            ("textPrimary", color.TextPrimary),
            ("textDim", color.TextDim),
            ("textMute", color.TextMute),
            ("accent", color.Accent),
            ("accentQuiet", color.AccentQuiet),
            ("accentInk", color.AccentInk),
            ("positive", color.Positive),
            ("warning", color.Warning),
            ("danger", color.Danger),
        ])) {
            RequireBindableColor(
                color: value,
                definition: definition,
                errors: errors,
                path: $"theme.color.{name}"
            );
        }

        RequireScrim(
            definition: definition,
            errors: errors,
            path: "theme.color.scrimPanel",
            scrim: color.ScrimPanel
        );
        RequireScrim(
            definition: definition,
            errors: errors,
            path: "theme.color.scrimStrip",
            scrim: color.ScrimStrip
        );
        RequireScrim(
            definition: definition,
            errors: errors,
            path: "theme.color.scrimChip",
            scrim: color.ScrimChip
        );
        RequireTypeSize(
            errors: errors,
            path: "theme.type.microSize",
            size: theme.Type.MicroSize
        );
        RequireTypeSize(
            errors: errors,
            path: "theme.type.monoSize",
            size: theme.Type.MonoSize
        );
        RequirePositive(
            errors: errors,
            name: "theme.motion.durMed",
            value: theme.Motion.DurMed
        );
        RequirePositive(
            errors: errors,
            name: "theme.icon.strokeHalfWidth",
            value: theme.Icon.StrokeHalfWidth
        );

        var styles = theme.Styles;

        foreach (var (name, value) in ((ReadOnlySpan<(string, float)>)[
            ("space.space2", theme.Space.Space2),
            ("space.space3", theme.Space.Space3),
            ("space.space8", theme.Space.Space8),
            ("radius.radius1", theme.Radius.Radius1),
            ("radius.radius2", theme.Radius.Radius2),
            ("radius.radius3", theme.Radius.Radius3),
            ("stroke.hairline", theme.Stroke.Hairline),
            ("styles.keycap.toggleMarch", styles.Keycap.ToggleMarch),
            ("styles.cursor.dotMaxHalf", styles.Cursor.DotMaxHalf),
            ("styles.cursor.labelGap", styles.Cursor.LabelGap),
            ("styles.wheel.hubLabelGap", styles.Wheel.HubLabelGap),
            ("styles.toast.minHeight", styles.Toast.MinHeight),
            ("styles.toast.iconSize", styles.Toast.IconSize),
            ("styles.toast.railWidth", styles.Toast.RailWidth),
        ])) {
            RequireNonNegative(
                errors: errors,
                name: $"theme.{name}",
                value: value
            );
        }

        // The halo's falloff divides by its reach.
        RequirePositive(
            errors: errors,
            name: "theme.styles.glow.haloBlur",
            value: styles.Glow.HaloBlur
        );
        RequirePositive(
            errors: errors,
            name: "theme.styles.keycap.referenceSize",
            value: styles.Keycap.ReferenceSize
        );
        RequirePositive(
            errors: errors,
            name: "theme.styles.console.headHeight",
            value: styles.Console.HeadHeight
        );

        foreach (var (name, value) in ((ReadOnlySpan<(string, float)>)[
            ("keycap.restOpacity", styles.Keycap.RestOpacity),
            ("bar.labelAlpha", styles.Bar.LabelAlpha),
            ("bar.hintAlpha", styles.Bar.HintAlpha),
            ("bar.quietAlpha", styles.Bar.QuietAlpha),
            ("console.selectionAlpha", styles.Console.SelectionAlpha),
            ("cursor.alpha", styles.Cursor.Alpha),
            ("cursor.dotRatio", styles.Cursor.DotRatio),
            ("wheel.ringAlpha", styles.Wheel.RingAlpha),
            ("wheel.activeRingAlpha", styles.Wheel.ActiveRingAlpha),
            ("wheel.labelAlpha", styles.Wheel.LabelAlpha),
            ("hud.trackAlpha", styles.Hud.TrackAlpha),
        ])) {
            RequireUnitInterval(
                errors: errors,
                name: $"theme.styles.{name}",
                value: value
            );
        }

        foreach (var (name, field, value) in ((ReadOnlySpan<(string, WorldValueField, BindableScalar)>)[
            ("ringAlpha", WorldValueFields.GlowRingAlpha, styles.Glow.RingAlpha),
            ("haloAlpha", WorldValueFields.GlowHaloAlpha, styles.Glow.HaloAlpha),
            ("neutralRingAlpha", WorldValueFields.GlowNeutralRingAlpha, styles.Glow.NeutralRingAlpha),
            ("neutralHaloAlpha", WorldValueFields.GlowNeutralHaloAlpha, styles.Glow.NeutralHaloAlpha),
        ])) {
            JudgeScalar(
                definition: definition,
                errors: errors,
                field: field,
                path: $"theme.styles.glow.{name}",
                scalar: value
            );
        }

        RequireStyleColor(
            color: styles.Keycap.Badge,
            definition: definition,
            errors: errors,
            path: "theme.styles.keycap.badge"
        );
        RequireStyleColor(
            color: styles.Keycap.BadgeInk,
            definition: definition,
            errors: errors,
            path: "theme.styles.keycap.badgeInk"
        );
        RequireStyleColor(
            color: styles.Console.Echo,
            definition: definition,
            errors: errors,
            path: "theme.styles.console.echo"
        );
    }
}
