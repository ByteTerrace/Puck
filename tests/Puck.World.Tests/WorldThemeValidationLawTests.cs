using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the <c>theme</c> section's validation: a minimal, fully-authored theme parses and validates; the
/// two engine-side perceptual floors (type size, scrim alpha) refuse a literal violation by name; a state-bound
/// scrim alpha cannot be refused at boot (the document has no way to know its live value) but clamps to the floor
/// at resolve time instead; and absence resolves to the zeroed <see cref="WorldThemeSection.Absent"/> block.</summary>
public sealed class WorldThemeValidationLawTests {
    private static readonly BindableColor OpaqueGray = new(Raw: "#808080");
    private static readonly BindableColor BakedAlphaWhite = new(Raw: "#FFFFFF80");

    private static WorldThemeBloomHue Hue() => new(
        Halo: BakedAlphaWhite,
        Ring: BakedAlphaWhite
    );
    private static WorldThemeScrim Scrim(float alpha) => new(
        Alpha: new BindableScalar(literal: alpha),
        Color: new BindableColor(Raw: "#101010")
    );
    private static WorldThemeScrim BoundScrim(string binding) => new(
        Alpha: new BindableScalar(binding: binding),
        Color: new BindableColor(Raw: "#101010")
    );
    private static WorldThemeColor MinimalColor(float scrimAlpha = 0.9f) => new(
        Accent: OpaqueGray, AccentInk: OpaqueGray, AccentLine: BakedAlphaWhite, AccentQuiet: BakedAlphaWhite,
        BadgeDark: OpaqueGray, BadgeLight: OpaqueGray, Danger: OpaqueGray, LineHair: BakedAlphaWhite,
        LineInset: BakedAlphaWhite, LineSoft: BakedAlphaWhite, LineStrong: BakedAlphaWhite, Phosphor: OpaqueGray,
        PhosphorCyan: OpaqueGray, PhosphorDim: BakedAlphaWhite, Positive: OpaqueGray,
        ScrimChip: Scrim(alpha: scrimAlpha), ScrimPanel: Scrim(alpha: scrimAlpha), ScrimStrip: Scrim(alpha: scrimAlpha),
        SurfaceBase: OpaqueGray, SurfaceInset: OpaqueGray, SurfacePanel: OpaqueGray, SurfaceRaised: OpaqueGray,
        TextDim: OpaqueGray, TextMute: OpaqueGray, TextPrimary: OpaqueGray, Warning: OpaqueGray
    );
    private static WorldThemeSpace MinimalSpace() => new(
        HeightBadge: 20f, HeightBindBar: 20f, HeightChip: 20f, HeightConsoleHead: 20f, HeightModeRow: 20f,
        HeightPromptRow: 20f, HeightTrackerBar: 20f, HeightTrackerCell: 20f,
        Space0: 0f, Space1: 4f, Space2: 8f, Space3: 12f, Space4: 16f, Space5: 20f, Space6: 24f, Space8: 32f
    );
    private static WorldThemeRadius MinimalRadius() => new(Radius1: 3f, Radius2: 6f, Radius3: 9f);
    private static WorldThemeType MinimalType(float bodySize = 12f) => new(
        BodyLine: 16f, BodySize: bodySize, BodyWeight: 400,
        LabelLine: 16f, LabelSize: 12f, LabelTracking: 0.01f, LabelWeight: 400,
        MicroLine: 16f, MicroSize: 12f, MicroTracking: 0.01f, MicroWeight: 400,
        MonoLine: 16f, MonoSize: 12f, MonoTracking: 0.01f, MonoWeight: 400,
        MonoBadgeSize: 12f, MonoReadoutSize: 12f,
        TitleLine: 16f, TitleSize: 12f, TitleTracking: 0.01f, TitleWeight: 400
    );
    private static WorldThemeElevation MinimalElevation() => new(
        BloomHaloBlur: 10f, BloomHaloSpread: -2f, BloomHaloAlpha: new BindableScalar(literal: 0.4f),
        BloomRingWidth: 1f, BloomRingAlpha: new BindableScalar(literal: 0.5f),
        BloomNeutralHaloAlpha: new BindableScalar(literal: 0.2f), BloomNeutralRingAlpha: new BindableScalar(literal: 0.3f),
        BloomHeldInsetBlur: 10f, BloomHeldInsetSpread: -2f, BloomHeldInsetAlpha: new BindableScalar(literal: 0.4f),
        BloomAccent: Hue(), BloomPositive: Hue(), BloomWarning: Hue(), BloomDanger: Hue(), BloomNeutral: Hue(),
        PressHeldGlowBlur: 10f, PressHeldGlowSpread: -2f, PressHeldGlowColor: BakedAlphaWhite,
        PressHeldShadowBlur: 6f, PressHeldShadowOffsetY: 2f, PressHeldTranslateY: 1f, PressHeldShadowColor: BakedAlphaWhite,
        ShadowSeatBlur: 30f, ShadowSeatOffsetY: 10f, ShadowSeatSpread: -10f, ShadowSeatStripSpread: -10f,
        ShadowSeatColor: BakedAlphaWhite, ShadowSeatStripColor: BakedAlphaWhite,
        CatchlightOffsetY: 1f, CatchlightColor: BakedAlphaWhite,
        ChipRestOpacity: 0.6f, EdgeHairlineWidth: 1f, RingStatusWidth: 2f, RingStatusAlpha: 0.5f
    );
    private static WorldThemeDiegetic MinimalDiegetic() => new(
        PlateTop: OpaqueGray, PlateMid: OpaqueGray, PlateBottom: OpaqueGray, PlateStripeColor: BakedAlphaWhite,
        EmbossFill: OpaqueGray, EngraveFill: OpaqueGray,
        EmbossShadowDropAlpha: 0.5f, EmbossShadowDropBlur: 2f, EmbossShadowDropOffsetY: 2f,
        EmbossShadowLitAlpha: 0.3f, EmbossShadowLitOffsetY: -1f,
        EngraveShadowLipAlpha: 0.2f, EngraveShadowLipOffsetY: 1f,
        EngraveShadowRecessAlpha: 0.5f, EngraveShadowRecessBlur: 1f, EngraveShadowRecessOffsetY: -1f,
        ScreenWellOuter: OpaqueGray, ScreenWellInner: OpaqueGray,
        BezelOuter: OpaqueGray, BezelInner: OpaqueGray, BezelEdge: OpaqueGray,
        PhosphorGlowBlur: 4f
    );
    private static WorldThemeMotion MinimalMotion() => new(
        CaretBlink: 1000f, DurFast: 100f, DurMed: 150f, DurPanel: 250f,
        EaseStd: new WorldThemeCubicBezier(X1: 0.2f, Y1: 0f, X2: 0f, Y2: 1f),
        EaseOut: new WorldThemeCubicBezier(X1: 0.4f, Y1: 0f, X2: 1f, Y2: 1f)
    );
    private static WorldThemeIcon MinimalIcon() => new(StrokeHalfWidth: 0.08f);

    private static WorldThemeSection MinimalTheme(float bodySize = 12f, float scrimAlpha = 0.9f) => new(
        Color: MinimalColor(scrimAlpha: scrimAlpha),
        Space: MinimalSpace(),
        Radius: MinimalRadius(),
        Type: MinimalType(bodySize: bodySize),
        Elevation: MinimalElevation(),
        Diegetic: MinimalDiegetic(),
        Motion: MinimalMotion(),
        Icon: MinimalIcon()
    );

    [Fact]
    public void MinimalThemeParsesAndValidates() {
        var definition = Fixtures.BuildDocument() with { ThemeRaw = MinimalTheme() };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.True(condition: admitted, userMessage: reason);
    }

    [Fact]
    public void TypeSizeBelowFloorRefusesByName() {
        var definition = Fixtures.BuildDocument() with { ThemeRaw = MinimalTheme(bodySize: 10f) };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "theme.type.bodySize");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "MSDF glyph coverage floor");
    }

    [Fact]
    public void LiteralScrimAlphaBelowFloorRefusesByName() {
        var definition = Fixtures.BuildDocument() with { ThemeRaw = MinimalTheme(scrimAlpha: 0.5f) };
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.False(condition: admitted);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "theme.color.scrimPanel.alpha");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "guaranteed-AA contrast floor");
    }

    [Fact]
    public void BoundScrimAlphaBelowFloorPassesValidationButClampsAtResolve() {
        var theme = MinimalTheme() with {
            Color = MinimalColor() with { ScrimPanel = BoundScrim(binding: "state.lowAlpha") },
        };
        var definition = Fixtures.BuildDocument() with {
            ThemeRaw = theme,
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: "lowAlpha"), Kind: CellKind.Fixed, Cells: [
                    new WorldStateCell(Key: WorldStateRow.SlotKey, Value: Puck.Maths.FixedQ4816.FromDouble(value: 0.2).Value),
                ]),
            ]),
        };

        // A state binding cannot be refused at boot — the document has no way to know the cell's live value.
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.True(condition: admitted, userMessage: reason);

        // At resolve time, the clamp is what actually enforces the floor.
        var resolved = new WorldThemeResolve().Resolve(definition: definition, revision: 1, tick: 0UL);

        Assert.True(condition: (resolved.Color.ScrimPanel.Alpha >= WorldThemeCapacity.ScrimMinAlpha));
    }

    [Fact]
    public void AbsentThemeResolvesToZeroedBlock() {
        var definition = Fixtures.BuildDocument();

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: WorldThemeSection.Absent, actual: definition.Theme);

        var resolved = new WorldThemeResolve().Resolve(definition: definition, revision: 1, tick: 0UL);

        Assert.Equal(expected: default, actual: resolved.Color.SurfaceBase);
        Assert.Equal(expected: 0f, actual: resolved.Space.HeightChip);
    }
}
