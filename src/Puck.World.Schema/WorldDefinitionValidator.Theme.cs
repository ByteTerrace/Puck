namespace Puck.World;

/// <summary>The <c>theme</c> section's validation: every <see cref="BindableColor"/>/<see cref="BindableScalar"/>
/// field checked for admissibility (a hex literal, or a state binding naming a compatible declared cell), plus the
/// document's own finite/range checks per field. Two floors live here, engine-side, never authored:
/// <see cref="WorldThemeCapacity.TypeAbsoluteFloorSize"/> (a plain-float field — always a literal, so a value below
/// it refuses outright) and <see cref="WorldThemeCapacity.ScrimMinAlpha"/> (a <see cref="BindableScalar"/> — a
/// literal below it refuses here, but a state binding cannot be checked at boot, so <c>WorldThemeResolve</c> clamps
/// it to the floor at resolve time instead).</summary>
public static partial class WorldDefinitionValidator {
    private static void RequireBindableColor(BindableColor color, WorldDefinition definition, string path, List<string> errors) {
        if (!color.IsAuthorable(definition: definition)) {
            errors.Add(item: $"{path} '{color.Raw}' {BindableColor.Grammar}.");
        }
    }
    private static void RequireBindableScalar(BindableScalar scalar, WorldDefinition definition, string path, List<string> errors) {
        if (!scalar.IsAuthorable(definition: definition)) {
            errors.Add(item: $"{path} {(scalar.Binding ?? scalar.Literal?.ToString() ?? "(absent)")} {BindableScalar.Grammar}.");
        }
    }
    private static void RequireBindableUnitScalar(BindableScalar scalar, WorldDefinition definition, string path, List<string> errors) {
        RequireBindableScalar(
            definition: definition,
            errors: errors,
            path: path,
            scalar: scalar
        );

        if (
            (scalar.Literal is { } literal) &&
            ((literal < 0f) || (literal > 1f))
        ) {
            errors.Add(item: $"{path} {literal} must be in [0, 1].");
        }
    }
    private static void RequireScrim(WorldThemeScrim scrim, WorldDefinition definition, string path, List<string> errors) {
        RequireBindableColor(
            color: scrim.Color,
            definition: definition,
            errors: errors,
            path: $"{path}.color"
        );
        RequireBindableScalar(
            definition: definition,
            errors: errors,
            path: $"{path}.alpha",
            scalar: scrim.Alpha
        );

        if (
            (scrim.Alpha.Literal is { } literal) &&
            ((literal < WorldThemeCapacity.ScrimMinAlpha) || (literal > 1f))
        ) {
            errors.Add(item: $"{path}.alpha {literal} must be in [{WorldThemeCapacity.ScrimMinAlpha}, 1] — below it the guaranteed-AA contrast floor (over both a dark corner and a lit CRT) breaks. A state.<row> binding is not refused here; it clamps to the floor at resolve time instead.");
        }
    }
    private static void RequireBloomHue(WorldThemeBloomHue hue, WorldDefinition definition, string path, List<string> errors) {
        RequireBindableColor(
            color: hue.Ring,
            definition: definition,
            errors: errors,
            path: $"{path}.ring"
        );
        RequireBindableColor(
            color: hue.Halo,
            definition: definition,
            errors: errors,
            path: $"{path}.halo"
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
    private static void RequireUnitFloat(float value, string path, List<string> errors) {
        if (
            !float.IsFinite(f: value) ||
            (value < 0f) ||
            (value > 1f)
        ) {
            errors.Add(item: $"{path} {value} must be finite and in [0, 1].");
        }
    }
    private static void ValidateTheme(WorldDefinition definition, List<string> errors) {
        if (definition.ThemeRaw is not { } theme) {
            return;
        }

        ValidateThemeColor(
            color: theme.Color,
            definition: definition,
            errors: errors
        );
        ValidateThemeSpace(
            errors: errors,
            space: theme.Space
        );
        ValidateThemeRadius(
            errors: errors,
            radius: theme.Radius
        );
        ValidateThemeType(
            errors: errors,
            type: theme.Type
        );
        ValidateThemeElevation(
            definition: definition,
            elevation: theme.Elevation,
            errors: errors
        );
        ValidateThemeDiegetic(
            definition: definition,
            diegetic: theme.Diegetic,
            errors: errors
        );
        ValidateThemeMotion(
            errors: errors,
            motion: theme.Motion
        );
        ValidateThemeIcon(
            errors: errors,
            icon: theme.Icon
        );
        ValidateThemeChrome(
            chrome: theme.Chrome,
            errors: errors
        );
    }
    private static void ValidateThemeChrome(WorldThemeChrome chrome, List<string> errors) {
        RequireUnitFloat(errors: errors, path: "theme.chrome.dimQuietAlpha", value: chrome.DimQuietAlpha);
        RequireUnitFloat(errors: errors, path: "theme.chrome.barLabelAlpha", value: chrome.BarLabelAlpha);
        RequireUnitFloat(errors: errors, path: "theme.chrome.barHintAlpha", value: chrome.BarHintAlpha);
        RequireUnitFloat(errors: errors, path: "theme.chrome.cursorAlpha", value: chrome.CursorAlpha);
        RequireUnitFloat(errors: errors, path: "theme.chrome.cursorDotRatio", value: chrome.CursorDotRatio);
        RequireNonNegative(errors: errors, name: "theme.chrome.cursorDotMaxHalf", value: chrome.CursorDotMaxHalf);
        RequireNonNegative(errors: errors, name: "theme.chrome.cursorLabelGap", value: chrome.CursorLabelGap);
        RequireUnitFloat(errors: errors, path: "theme.chrome.wheelRingAlpha", value: chrome.WheelRingAlpha);
        RequireUnitFloat(errors: errors, path: "theme.chrome.wheelActiveRingAlpha", value: chrome.WheelActiveRingAlpha);
        RequireNonNegative(errors: errors, name: "theme.chrome.wheelActiveRingOffset", value: chrome.WheelActiveRingOffset);
        RequireUnitFloat(errors: errors, path: "theme.chrome.wheelLabelAlpha", value: chrome.WheelLabelAlpha);
        RequireNonNegative(errors: errors, name: "theme.chrome.wheelHubDotHalf", value: chrome.WheelHubDotHalf);
        RequireNonNegative(errors: errors, name: "theme.chrome.wheelMarkerHalf", value: chrome.WheelMarkerHalf);
        RequireNonNegative(errors: errors, name: "theme.chrome.wheelMarkerGapRatio", value: chrome.WheelMarkerGapRatio);
        RequireNonNegative(errors: errors, name: "theme.chrome.wheelHubLabelGap", value: chrome.WheelHubLabelGap);
    }
    private static void ValidateThemeColor(WorldThemeColor color, WorldDefinition definition, List<string> errors) {
        RequireBindableColor(color: color.SurfaceBase, definition: definition, errors: errors, path: "theme.color.surfaceBase");
        RequireBindableColor(color: color.SurfacePanel, definition: definition, errors: errors, path: "theme.color.surfacePanel");
        RequireBindableColor(color: color.SurfaceRaised, definition: definition, errors: errors, path: "theme.color.surfaceRaised");
        RequireBindableColor(color: color.SurfaceInset, definition: definition, errors: errors, path: "theme.color.surfaceInset");
        RequireScrim(scrim: color.ScrimPanel, definition: definition, errors: errors, path: "theme.color.scrimPanel");
        RequireScrim(scrim: color.ScrimStrip, definition: definition, errors: errors, path: "theme.color.scrimStrip");
        RequireScrim(scrim: color.ScrimChip, definition: definition, errors: errors, path: "theme.color.scrimChip");
        RequireBindableColor(color: color.LineHair, definition: definition, errors: errors, path: "theme.color.lineHair");
        RequireBindableColor(color: color.LineSoft, definition: definition, errors: errors, path: "theme.color.lineSoft");
        RequireBindableColor(color: color.LineStrong, definition: definition, errors: errors, path: "theme.color.lineStrong");
        RequireBindableColor(color: color.LineInset, definition: definition, errors: errors, path: "theme.color.lineInset");
        RequireBindableColor(color: color.TextPrimary, definition: definition, errors: errors, path: "theme.color.textPrimary");
        RequireBindableColor(color: color.TextDim, definition: definition, errors: errors, path: "theme.color.textDim");
        RequireBindableColor(color: color.TextMute, definition: definition, errors: errors, path: "theme.color.textMute");
        RequireBindableColor(color: color.Accent, definition: definition, errors: errors, path: "theme.color.accent");
        RequireBindableColor(color: color.AccentQuiet, definition: definition, errors: errors, path: "theme.color.accentQuiet");
        RequireBindableColor(color: color.AccentLine, definition: definition, errors: errors, path: "theme.color.accentLine");
        RequireBindableColor(color: color.AccentInk, definition: definition, errors: errors, path: "theme.color.accentInk");
        RequireBindableColor(color: color.Positive, definition: definition, errors: errors, path: "theme.color.positive");
        RequireBindableColor(color: color.Warning, definition: definition, errors: errors, path: "theme.color.warning");
        RequireBindableColor(color: color.Danger, definition: definition, errors: errors, path: "theme.color.danger");
        RequireBindableColor(color: color.Phosphor, definition: definition, errors: errors, path: "theme.color.phosphor");
        RequireBindableColor(color: color.PhosphorDim, definition: definition, errors: errors, path: "theme.color.phosphorDim");
        RequireBindableColor(color: color.PhosphorCyan, definition: definition, errors: errors, path: "theme.color.phosphorCyan");
        RequireBindableColor(color: color.BadgeDark, definition: definition, errors: errors, path: "theme.color.badgeDark");
        RequireBindableColor(color: color.BadgeLight, definition: definition, errors: errors, path: "theme.color.badgeLight");
    }
    private static void ValidateThemeDiegetic(WorldThemeDiegetic diegetic, WorldDefinition definition, List<string> errors) {
        RequireBindableColor(color: diegetic.PlateTop, definition: definition, errors: errors, path: "theme.diegetic.plateTop");
        RequireBindableColor(color: diegetic.PlateMid, definition: definition, errors: errors, path: "theme.diegetic.plateMid");
        RequireBindableColor(color: diegetic.PlateBottom, definition: definition, errors: errors, path: "theme.diegetic.plateBottom");
        RequireBindableColor(color: diegetic.PlateStripeColor, definition: definition, errors: errors, path: "theme.diegetic.plateStripeColor");
        RequireBindableColor(color: diegetic.EmbossFill, definition: definition, errors: errors, path: "theme.diegetic.embossFill");
        RequireBindableColor(color: diegetic.EngraveFill, definition: definition, errors: errors, path: "theme.diegetic.engraveFill");
        RequireUnitFloat(errors: errors, path: "theme.diegetic.embossShadowDropAlpha", value: diegetic.EmbossShadowDropAlpha);
        RequireNonNegative(errors: errors, name: "theme.diegetic.embossShadowDropBlur", value: diegetic.EmbossShadowDropBlur);
        RequireFinite(errors: errors, name: "theme.diegetic.embossShadowDropOffsetY", value: diegetic.EmbossShadowDropOffsetY);
        RequireUnitFloat(errors: errors, path: "theme.diegetic.embossShadowLitAlpha", value: diegetic.EmbossShadowLitAlpha);
        RequireFinite(errors: errors, name: "theme.diegetic.embossShadowLitOffsetY", value: diegetic.EmbossShadowLitOffsetY);
        RequireUnitFloat(errors: errors, path: "theme.diegetic.engraveShadowLipAlpha", value: diegetic.EngraveShadowLipAlpha);
        RequireFinite(errors: errors, name: "theme.diegetic.engraveShadowLipOffsetY", value: diegetic.EngraveShadowLipOffsetY);
        RequireUnitFloat(errors: errors, path: "theme.diegetic.engraveShadowRecessAlpha", value: diegetic.EngraveShadowRecessAlpha);
        RequireNonNegative(errors: errors, name: "theme.diegetic.engraveShadowRecessBlur", value: diegetic.EngraveShadowRecessBlur);
        RequireFinite(errors: errors, name: "theme.diegetic.engraveShadowRecessOffsetY", value: diegetic.EngraveShadowRecessOffsetY);
        RequireBindableColor(color: diegetic.ScreenWellOuter, definition: definition, errors: errors, path: "theme.diegetic.screenWellOuter");
        RequireBindableColor(color: diegetic.ScreenWellInner, definition: definition, errors: errors, path: "theme.diegetic.screenWellInner");
        RequireBindableColor(color: diegetic.BezelOuter, definition: definition, errors: errors, path: "theme.diegetic.bezelOuter");
        RequireBindableColor(color: diegetic.BezelInner, definition: definition, errors: errors, path: "theme.diegetic.bezelInner");
        RequireBindableColor(color: diegetic.BezelEdge, definition: definition, errors: errors, path: "theme.diegetic.bezelEdge");
        RequireNonNegative(errors: errors, name: "theme.diegetic.phosphorGlowBlur", value: diegetic.PhosphorGlowBlur);
    }
    private static void ValidateThemeElevation(WorldThemeElevation elevation, WorldDefinition definition, List<string> errors) {
        RequireNonNegative(errors: errors, name: "theme.elevation.bloomHaloBlur", value: elevation.BloomHaloBlur);
        RequireFinite(errors: errors, name: "theme.elevation.bloomHaloSpread", value: elevation.BloomHaloSpread);
        RequireBindableUnitScalar(definition: definition, errors: errors, path: "theme.elevation.bloomHaloAlpha", scalar: elevation.BloomHaloAlpha);
        RequireNonNegative(errors: errors, name: "theme.elevation.bloomRingWidth", value: elevation.BloomRingWidth);
        RequireBindableUnitScalar(definition: definition, errors: errors, path: "theme.elevation.bloomRingAlpha", scalar: elevation.BloomRingAlpha);
        RequireBindableUnitScalar(definition: definition, errors: errors, path: "theme.elevation.bloomNeutralHaloAlpha", scalar: elevation.BloomNeutralHaloAlpha);
        RequireBindableUnitScalar(definition: definition, errors: errors, path: "theme.elevation.bloomNeutralRingAlpha", scalar: elevation.BloomNeutralRingAlpha);
        RequireNonNegative(errors: errors, name: "theme.elevation.bloomHeldInsetBlur", value: elevation.BloomHeldInsetBlur);
        RequireFinite(errors: errors, name: "theme.elevation.bloomHeldInsetSpread", value: elevation.BloomHeldInsetSpread);
        RequireBindableUnitScalar(definition: definition, errors: errors, path: "theme.elevation.bloomHeldInsetAlpha", scalar: elevation.BloomHeldInsetAlpha);
        RequireBloomHue(definition: definition, errors: errors, hue: elevation.BloomAccent, path: "theme.elevation.bloomAccent");
        RequireBloomHue(definition: definition, errors: errors, hue: elevation.BloomPositive, path: "theme.elevation.bloomPositive");
        RequireBloomHue(definition: definition, errors: errors, hue: elevation.BloomWarning, path: "theme.elevation.bloomWarning");
        RequireBloomHue(definition: definition, errors: errors, hue: elevation.BloomDanger, path: "theme.elevation.bloomDanger");
        RequireBloomHue(definition: definition, errors: errors, hue: elevation.BloomNeutral, path: "theme.elevation.bloomNeutral");
        RequireNonNegative(errors: errors, name: "theme.elevation.pressHeldGlowBlur", value: elevation.PressHeldGlowBlur);
        RequireFinite(errors: errors, name: "theme.elevation.pressHeldGlowSpread", value: elevation.PressHeldGlowSpread);
        RequireBindableColor(color: elevation.PressHeldGlowColor, definition: definition, errors: errors, path: "theme.elevation.pressHeldGlowColor");
        RequireNonNegative(errors: errors, name: "theme.elevation.pressHeldShadowBlur", value: elevation.PressHeldShadowBlur);
        RequireFinite(errors: errors, name: "theme.elevation.pressHeldShadowOffsetY", value: elevation.PressHeldShadowOffsetY);
        RequireFinite(errors: errors, name: "theme.elevation.pressHeldTranslateY", value: elevation.PressHeldTranslateY);
        RequireBindableColor(color: elevation.PressHeldShadowColor, definition: definition, errors: errors, path: "theme.elevation.pressHeldShadowColor");
        RequireNonNegative(errors: errors, name: "theme.elevation.shadowSeatBlur", value: elevation.ShadowSeatBlur);
        RequireFinite(errors: errors, name: "theme.elevation.shadowSeatOffsetY", value: elevation.ShadowSeatOffsetY);
        RequireFinite(errors: errors, name: "theme.elevation.shadowSeatSpread", value: elevation.ShadowSeatSpread);
        RequireFinite(errors: errors, name: "theme.elevation.shadowSeatStripSpread", value: elevation.ShadowSeatStripSpread);
        RequireBindableColor(color: elevation.ShadowSeatColor, definition: definition, errors: errors, path: "theme.elevation.shadowSeatColor");
        RequireBindableColor(color: elevation.ShadowSeatStripColor, definition: definition, errors: errors, path: "theme.elevation.shadowSeatStripColor");
        RequireFinite(errors: errors, name: "theme.elevation.catchlightOffsetY", value: elevation.CatchlightOffsetY);
        RequireBindableColor(color: elevation.CatchlightColor, definition: definition, errors: errors, path: "theme.elevation.catchlightColor");
        RequireUnitFloat(errors: errors, path: "theme.elevation.chipRestOpacity", value: elevation.ChipRestOpacity);
        RequireNonNegative(errors: errors, name: "theme.elevation.edgeHairlineWidth", value: elevation.EdgeHairlineWidth);
        RequireNonNegative(errors: errors, name: "theme.elevation.ringStatusWidth", value: elevation.RingStatusWidth);
        RequireUnitFloat(errors: errors, path: "theme.elevation.ringStatusAlpha", value: elevation.RingStatusAlpha);
    }
    private static void ValidateThemeIcon(WorldThemeIcon icon, List<string> errors) => RequirePositive(
        errors: errors,
        name: "theme.icon.strokeHalfWidth",
        value: icon.StrokeHalfWidth
    );
    private static void ValidateThemeMotion(WorldThemeMotion motion, List<string> errors) {
        RequirePositive(errors: errors, name: "theme.motion.caretBlink", value: motion.CaretBlink);
        RequirePositive(errors: errors, name: "theme.motion.durFast", value: motion.DurFast);
        RequirePositive(errors: errors, name: "theme.motion.durMed", value: motion.DurMed);
        RequirePositive(errors: errors, name: "theme.motion.durPanel", value: motion.DurPanel);
        ValidateThemeCubicBezier(bezier: motion.EaseStd, errors: errors, path: "theme.motion.easeStd");
        ValidateThemeCubicBezier(bezier: motion.EaseOut, errors: errors, path: "theme.motion.easeOut");
    }
    private static void ValidateThemeCubicBezier(WorldThemeCubicBezier bezier, string path, List<string> errors) {
        RequireUnitFloat(errors: errors, path: $"{path}.x1", value: bezier.X1);
        RequireFinite(errors: errors, name: $"{path}.y1", value: bezier.Y1);
        RequireUnitFloat(errors: errors, path: $"{path}.x2", value: bezier.X2);
        RequireFinite(errors: errors, name: $"{path}.y2", value: bezier.Y2);
    }
    private static void ValidateThemeRadius(WorldThemeRadius radius, List<string> errors) {
        RequireNonNegative(errors: errors, name: "theme.radius.radius1", value: radius.Radius1);
        RequireNonNegative(errors: errors, name: "theme.radius.radius2", value: radius.Radius2);
        RequireNonNegative(errors: errors, name: "theme.radius.radius3", value: radius.Radius3);
    }
    private static void ValidateThemeSpace(WorldThemeSpace space, List<string> errors) {
        RequireNonNegative(errors: errors, name: "theme.space.space0", value: space.Space0);
        RequireNonNegative(errors: errors, name: "theme.space.space1", value: space.Space1);
        RequireNonNegative(errors: errors, name: "theme.space.space2", value: space.Space2);
        RequireNonNegative(errors: errors, name: "theme.space.space3", value: space.Space3);
        RequireNonNegative(errors: errors, name: "theme.space.space4", value: space.Space4);
        RequireNonNegative(errors: errors, name: "theme.space.space5", value: space.Space5);
        RequireNonNegative(errors: errors, name: "theme.space.space6", value: space.Space6);
        RequireNonNegative(errors: errors, name: "theme.space.space8", value: space.Space8);
        RequirePositive(errors: errors, name: "theme.space.heightBadge", value: space.HeightBadge);
        RequirePositive(errors: errors, name: "theme.space.heightBindBar", value: space.HeightBindBar);
        RequirePositive(errors: errors, name: "theme.space.heightChip", value: space.HeightChip);
        RequirePositive(errors: errors, name: "theme.space.heightConsoleHead", value: space.HeightConsoleHead);
        RequirePositive(errors: errors, name: "theme.space.heightModeRow", value: space.HeightModeRow);
        RequirePositive(errors: errors, name: "theme.space.heightPromptRow", value: space.HeightPromptRow);
        RequirePositive(errors: errors, name: "theme.space.heightTrackerBar", value: space.HeightTrackerBar);
        RequirePositive(errors: errors, name: "theme.space.heightTrackerCell", value: space.HeightTrackerCell);
    }
    private static void ValidateThemeType(WorldThemeType type, List<string> errors) {
        RequireTypeSize(errors: errors, path: "theme.type.bodySize", size: type.BodySize);
        RequirePositive(errors: errors, name: "theme.type.bodyLine", value: type.BodyLine);
        RequireIntRange(errors: errors, max: 900, min: 100, name: "theme.type.bodyWeight", value: type.BodyWeight);
        RequireTypeSize(errors: errors, path: "theme.type.labelSize", size: type.LabelSize);
        RequirePositive(errors: errors, name: "theme.type.labelLine", value: type.LabelLine);
        RequireNonNegative(errors: errors, name: "theme.type.labelTracking", value: type.LabelTracking);
        RequireIntRange(errors: errors, max: 900, min: 100, name: "theme.type.labelWeight", value: type.LabelWeight);
        RequireTypeSize(errors: errors, path: "theme.type.microSize", size: type.MicroSize);
        RequirePositive(errors: errors, name: "theme.type.microLine", value: type.MicroLine);
        RequireNonNegative(errors: errors, name: "theme.type.microTracking", value: type.MicroTracking);
        RequireIntRange(errors: errors, max: 900, min: 100, name: "theme.type.microWeight", value: type.MicroWeight);
        RequireTypeSize(errors: errors, path: "theme.type.monoSize", size: type.MonoSize);
        RequirePositive(errors: errors, name: "theme.type.monoLine", value: type.MonoLine);
        RequireNonNegative(errors: errors, name: "theme.type.monoTracking", value: type.MonoTracking);
        RequireIntRange(errors: errors, max: 900, min: 100, name: "theme.type.monoWeight", value: type.MonoWeight);
        RequireTypeSize(errors: errors, path: "theme.type.monoBadgeSize", size: type.MonoBadgeSize);
        RequireTypeSize(errors: errors, path: "theme.type.monoReadoutSize", size: type.MonoReadoutSize);
        RequireTypeSize(errors: errors, path: "theme.type.titleSize", size: type.TitleSize);
        RequirePositive(errors: errors, name: "theme.type.titleLine", value: type.TitleLine);
        RequireNonNegative(errors: errors, name: "theme.type.titleTracking", value: type.TitleTracking);
        RequireIntRange(errors: errors, max: 900, min: 100, name: "theme.type.titleWeight", value: type.TitleWeight);
    }
}
