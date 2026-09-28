using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the <c>theme</c> section's validation: a minimal, fully-authored theme parses and validates; the
/// two engine-side perceptual floors (type size, scrim alpha) refuse a literal violation by name; a state-bound
/// scrim alpha cannot be refused at boot (the document has no way to know its live value) but clamps to the floor
/// at resolve time instead; and absence resolves to the zeroed <see cref="WorldThemeSection.Absent"/> block.</summary>
public sealed class WorldThemeValidationLawTests {
    private static readonly BindableColor OpaqueGray = new(Raw: "#808080");
    private static readonly BindableColor BakedAlphaWhite = new(Raw: "#FFFFFF80");

    private static WorldThemeScrim BoundScrim(string binding) => new(
        Alpha: new BindableScalar(binding: binding),
        Color: new BindableColor(Raw: "#101010")
    );
    private static WorldThemeBloomHue Hue() => new(
        Halo: BakedAlphaWhite,
        Ring: BakedAlphaWhite
    );
    private static WorldThemeChrome MinimalChrome() => new(
        BarHintAlpha: 0.6f,
        BarLabelAlpha: 0.9f,
        CursorAlpha: 0.9f,
        CursorDotMaxHalf: 4f,
        CursorDotRatio: 0.22f,
        CursorLabelGap: 5f,
        DimQuietAlpha: 0.35f,
        WheelActiveRingAlpha: 0.95f,
        WheelActiveRingOffset: 1.5f,
        WheelHubDotHalf: 3f,
        WheelHubLabelGap: 2f,
        WheelLabelAlpha: 1f,
        WheelMarkerGapRatio: 1.6f,
        WheelMarkerHalf: 3.5f,
        WheelRingAlpha: 0.55f
    );
    private static WorldThemeColor MinimalColor(float scrimAlpha = 0.9f) => new(
        Accent: OpaqueGray,
        AccentInk: OpaqueGray,
        AccentLine: BakedAlphaWhite,
        AccentQuiet: BakedAlphaWhite,
        BadgeDark: OpaqueGray,
        BadgeLight: OpaqueGray,
        Danger: OpaqueGray,
        LineHair: BakedAlphaWhite,
        LineInset: BakedAlphaWhite,
        LineSoft: BakedAlphaWhite,
        LineStrong: BakedAlphaWhite,
        Phosphor: OpaqueGray,
        PhosphorCyan: OpaqueGray,
        PhosphorDim: BakedAlphaWhite,
        Positive: OpaqueGray,
        ScrimChip: Scrim(alpha: scrimAlpha),
        ScrimPanel: Scrim(alpha: scrimAlpha),
        ScrimStrip: Scrim(alpha: scrimAlpha),
        SurfaceBase: OpaqueGray,
        SurfaceInset: OpaqueGray,
        SurfacePanel: OpaqueGray,
        SurfaceRaised: OpaqueGray,
        TextDim: OpaqueGray,
        TextMute: OpaqueGray,
        TextPrimary: OpaqueGray,
        Warning: OpaqueGray
    );
    private static WorldThemeDiegetic MinimalDiegetic() => new(
        BezelEdge: OpaqueGray,
        BezelInner: OpaqueGray,
        BezelOuter: OpaqueGray,
        EmbossFill: OpaqueGray,
        EmbossShadowDropAlpha: 0.5f,
        EmbossShadowDropBlur: 2f,
        EmbossShadowDropOffsetY: 2f,
        EmbossShadowLitAlpha: 0.3f,
        EmbossShadowLitOffsetY: -1f,
        EngraveFill: OpaqueGray,
        EngraveShadowLipAlpha: 0.2f,
        EngraveShadowLipOffsetY: 1f,
        EngraveShadowRecessAlpha: 0.5f,
        EngraveShadowRecessBlur: 1f,
        EngraveShadowRecessOffsetY: -1f,
        PhosphorGlowBlur: 4f,
        PlateBottom: OpaqueGray,
        PlateMid: OpaqueGray,
        PlateStripeColor: BakedAlphaWhite,
        PlateTop: OpaqueGray,
        ScreenWellInner: OpaqueGray,
        ScreenWellOuter: OpaqueGray
    );
    private static WorldThemeElevation MinimalElevation() => new(
        BloomHaloBlur: 10f,
        BloomHaloSpread: -2f,
        BloomHaloAlpha: new BindableScalar(literal: 0.4f),
        BloomRingWidth: 1f,
        BloomRingAlpha: new BindableScalar(literal: 0.5f),
        BloomNeutralHaloAlpha: new BindableScalar(literal: 0.2f),
        BloomNeutralRingAlpha: new BindableScalar(literal: 0.3f),
        BloomHeldInsetBlur: 10f,
        BloomHeldInsetSpread: -2f,
        BloomHeldInsetAlpha: new BindableScalar(literal: 0.4f),
        BloomAccent: Hue(),
        BloomPositive: Hue(),
        BloomWarning: Hue(),
        BloomDanger: Hue(),
        BloomNeutral: Hue(),
        PressHeldGlowBlur: 10f,
        PressHeldGlowSpread: -2f,
        PressHeldGlowColor: BakedAlphaWhite,
        PressHeldShadowBlur: 6f,
        PressHeldShadowOffsetY: 2f,
        PressHeldTranslateY: 1f,
        PressHeldShadowColor: BakedAlphaWhite,
        ShadowSeatBlur: 30f,
        ShadowSeatOffsetY: 10f,
        ShadowSeatSpread: -10f,
        ShadowSeatStripSpread: -10f,
        ShadowSeatColor: BakedAlphaWhite,
        ShadowSeatStripColor: BakedAlphaWhite,
        CatchlightOffsetY: 1f,
        CatchlightColor: BakedAlphaWhite,
        ChipRestOpacity: 0.6f,
        EdgeHairlineWidth: 1f,
        RingStatusWidth: 2f,
        RingStatusAlpha: 0.5f
    );
    private static WorldThemeIcon MinimalIcon() => new(StrokeHalfWidth: 0.08f);
    private static WorldThemeMotion MinimalMotion() => new(
        CaretBlink: 1000f,
        DurFast: 100f,
        DurMed: 150f,
        DurPanel: 250f,
        EaseStd: new WorldThemeCubicBezier(
            X1: 0.2f,
            X2: 0f,
            Y1: 0f,
            Y2: 1f
        ),
        EaseOut: new WorldThemeCubicBezier(
            X1: 0.4f,
            X2: 1f,
            Y1: 0f,
            Y2: 1f
        )
    );
    private static WorldThemeRadius MinimalRadius() => new(
        Radius1: 3f,
        Radius2: 6f,
        Radius3: 9f
    );
    private static WorldThemeSpace MinimalSpace() => new(
        HeightBadge: 20f,
        HeightBindBar: 20f,
        HeightChip: 20f,
        HeightConsoleHead: 20f,
        HeightModeRow: 20f,
        HeightPromptRow: 20f,
        HeightTrackerBar: 20f,
        HeightTrackerCell: 20f,
        Space0: 0f,
        Space1: 4f,
        Space2: 8f,
        Space3: 12f,
        Space4: 16f,
        Space5: 20f,
        Space6: 24f,
        Space8: 32f
    );
    private static WorldThemeSection MinimalTheme(float bodySize = 12f, float scrimAlpha = 0.9f) => new(
        Chrome: MinimalChrome(),
        Color: MinimalColor(scrimAlpha: scrimAlpha),
        Space: MinimalSpace(),
        Radius: MinimalRadius(),
        Type: MinimalType(bodySize: bodySize),
        Elevation: MinimalElevation(),
        Diegetic: MinimalDiegetic(),
        Motion: MinimalMotion(),
        Icon: MinimalIcon()
    );
    private static WorldThemeType MinimalType(float bodySize = 12f) => new(
        BodyLine: 16f,
        BodySize: bodySize,
        BodyWeight: 400,
        LabelLine: 16f,
        LabelSize: 12f,
        LabelTracking: 0.01f,
        LabelWeight: 400,
        MicroLine: 16f,
        MicroSize: 12f,
        MicroTracking: 0.01f,
        MicroWeight: 400,
        MonoBadgeSize: 12f,
        MonoLine: 16f,
        MonoReadoutSize: 12f,
        MonoSize: 12f,
        MonoTracking: 0.01f,
        MonoWeight: 400,
        TitleLine: 16f,
        TitleSize: 12f,
        TitleTracking: 0.01f,
        TitleWeight: 400
    );
    private static WorldThemeScrim Scrim(float alpha) => new(
        Alpha: new BindableScalar(literal: alpha),
        Color: new BindableColor(Raw: "#101010")
    );

    /// <summary>The theme re-resolves only when a slot one of its own tokens reads changes: a row some other consumer
    /// binds moving leaves the cached resolve in place, and the bound row moving resolves it again with the new value.</summary>
    [Fact]
    public void TheThemeReResolvesOnlyWhenASlotItBindsChanges() {
        static WorldStateRow FixedRow(string name, double value) => new(
            Name: CellName.Parse(candidate: name),
            Kind: CellKind.Fixed,
            Cells: [
                new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Fixed(rawBits: Puck.Maths.FixedQ4816.FromDouble(value: value).Value)
                ),
            ]
        );
        WorldDefinition Document(double panel, double gauge) => Fixtures.BuildDocument().WithWorldState(rows: [
            FixedRow(
                name: "panelAlpha",
                value: panel
            ),
            FixedRow(
                name: "gauge",
                value: gauge
            ),
        ]) with {
            ThemeRaw = MinimalTheme() with {
                Color = MinimalColor() with { ScrimPanel = BoundScrim(binding: "state.panelAlpha") },
            },
        };

        var definition = Document(
            gauge: 1,
            panel: 0.9
        );
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);
        var theme = new WorldThemeResolve();

        Assert.True(condition: view.TryResolveRow(
            ordinal: out var panelRow,
            rowName: "panelAlpha"
        ));
        Assert.True(condition: view.TryResolveRow(
            ordinal: out var gaugeRow,
            rowName: "gauge"
        ));
        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        // Another consumer binds the gauge.
        var gauge = mirror.Bind(
            conversion: WorldStateConversion.Number,
            token: "state.gauge"
        );
        var first = theme.Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        );

        Assert.Equal(
            expected: 1,
            actual: theme.Resolutions
        );

        definition = Document(
            gauge: 2,
            panel: 0.9
        );
        mirror.Refresh(stamp: new WorldStateStamp(
            EngineTick: 0UL,
            Everything: false,
            MovedRows: new[] { gaugeRow },
            Tick: 1UL
        ));
        Assert.True(condition: mirror.TryNumber(
            slot: gauge,
            value: out var moved
        ));
        Assert.Equal(
            actual: moved,
            expected: 2f
        );
        Assert.Equal(
            expected: first,
            actual: theme.Resolve(
                definition: definition,
                mirror: mirror,
                revision: 1
            )
        );
        Assert.Equal(
            expected: 1,
            actual: theme.Resolutions
        );

        definition = Document(
            gauge: 2,
            panel: 0.95
        );
        mirror.Refresh(stamp: new WorldStateStamp(
            EngineTick: 0UL,
            Everything: false,
            MovedRows: new[] { panelRow },
            Tick: 2UL
        ));

        var second = theme.Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        );

        Assert.Equal(
            expected: 2,
            actual: theme.Resolutions
        );
        Assert.Equal(
            expected: 0.95f,
            actual: second.Color.ScrimPanel.Alpha,
            precision: 4
        );
    }
    [Fact]
    public void AbsentThemeResolvesToZeroedBlock() {
        var definition = Fixtures.BuildDocument();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: WorldThemeSection.Absent,
            actual: definition.Theme
        );

        var resolved = new WorldThemeResolve().Resolve(
            definition: definition,
            revision: 1,
            mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition))
        );

        Assert.Equal(
            expected: default,
            actual: resolved.Color.SurfaceBase
        );
        Assert.Equal(
            expected: 0f,
            actual: resolved.Space.HeightChip
        );
        Assert.Equal(
            expected: 0f,
            actual: resolved.Chrome.DimQuietAlpha
        );
    }
    /// <summary>The writers' chrome is authored data end to end: what the document declares is what the writers read
    /// through the resolved theme, so retuning a quiet dim or a wheel marker is a document edit, never a rebuild.</summary>
    [Fact]
    public void AuthoredChromeResolvesToWhatTheWritersRead() {
        var definition = Fixtures.BuildDocument() with { ThemeRaw = MinimalTheme() };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );

        var resolved = new WorldThemeResolve().Resolve(
            definition: definition,
            revision: 1,
            mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition))
        );

        Assert.Equal(
            expected: 0.35f,
            actual: resolved.Chrome.DimQuietAlpha
        );
        Assert.Equal(
            expected: 0.9f,
            actual: resolved.Chrome.BarLabelAlpha
        );
        Assert.Equal(
            expected: 0.6f,
            actual: resolved.Chrome.BarHintAlpha
        );
        Assert.Equal(
            expected: 0.9f,
            actual: resolved.Chrome.CursorAlpha
        );
        Assert.Equal(
            expected: 0.22f,
            actual: resolved.Chrome.CursorDotRatio
        );
        Assert.Equal(
            expected: 0.55f,
            actual: resolved.Chrome.WheelRingAlpha
        );
        Assert.Equal(
            expected: 0.95f,
            actual: resolved.Chrome.WheelActiveRingAlpha
        );
        Assert.Equal(
            expected: 3.5f,
            actual: resolved.Chrome.WheelMarkerHalf
        );
    }
    [Fact]
    public void BoundScrimAlphaBelowFloorPassesValidationButClampsAtResolve() {
        var theme = MinimalTheme() with {
            Color = MinimalColor() with { ScrimPanel = BoundScrim(binding: "state.lowAlpha") },
        };
        var definition = Fixtures.BuildDocument() with {
            ThemeRaw = theme,
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "lowAlpha"),
                Kind: CellKind.Fixed,
                Cells: [
                    new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Fixed(rawBits: Puck.Maths.FixedQ4816.FromDouble(value: 0.2).Value)
                    ),
                ]
            ),
            ]),
        };

        // A state binding cannot be refused at boot — the document has no way to know the cell's live value.
        var admitted = WorldDefinitionValidator.TryValidate(
            definition: definition,
            neighbours: null,
            reason: out var reason
        );

        Assert.True(
            condition: admitted,
            userMessage: reason
        );

        // At resolve time, the clamp is what actually enforces the floor.
        var resolved = new WorldThemeResolve().Resolve(
            definition: definition,
            revision: 1,
            mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition))
        );

        Assert.True(condition: (resolved.Color.ScrimPanel.Alpha >= WorldThemeCapacity.ScrimMinAlpha));
    }
    /// <summary>The chrome block's ranges are enforced by name, beside a passing control — an opacity outside [0, 1]
    /// and a negative extent are both authoring errors a boot must refuse rather than draw.</summary>
    [Theory]
    [InlineData("theme.chrome.dimQuietAlpha")]
    [InlineData("theme.chrome.cursorDotMaxHalf")]
    public void ChromeOutOfRangeRefusesByName(string field) {
        var chrome = MinimalChrome();
        var invalid = (string.Equals(
            a: field,
            b: "theme.chrome.dimQuietAlpha",
            comparisonType: StringComparison.Ordinal
        )
            ? (chrome with { DimQuietAlpha = 1.5f })
            : (chrome with { CursorDotMaxHalf = -1f })
        );
        var denied = Fixtures.BuildDocument() with { ThemeRaw = (MinimalTheme() with { Chrome = invalid }) };
        var admitted = Fixtures.BuildDocument() with { ThemeRaw = MinimalTheme() };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: field
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    // A literal below its floor refuses naming both the field and the floor it protects: the scrim's alpha guards the
    // guaranteed-AA contrast floor, the body type size the MSDF glyph coverage floor.
    [InlineData("theme.color.scrimPanel.alpha", "guaranteed-AA contrast floor")]
    [InlineData("theme.type.bodySize", "MSDF glyph coverage floor")]
    [Theory]
    public void ALiteralBelowItsFloorRefusesByName(string field, string floor) {
        var definition = Fixtures.BuildDocument() with {
            ThemeRaw = ((field == "theme.type.bodySize")
                ? MinimalTheme(bodySize: 10f)
                : MinimalTheme(scrimAlpha: 0.5f)),
        };

        Laws.Refuses(
            definition: definition,
            needle: field
        );
        Laws.Refuses(
            definition: definition,
            needle: floor
        );
    }
    [Fact]
    public void MinimalThemeParsesAndValidates() {
        var definition = Fixtures.BuildDocument() with { ThemeRaw = MinimalTheme() };
        var admitted = WorldDefinitionValidator.TryValidate(
            definition: definition,
            neighbours: null,
            reason: out var reason
        );

        Assert.True(
            condition: admitted,
            userMessage: reason
        );
    }
}
