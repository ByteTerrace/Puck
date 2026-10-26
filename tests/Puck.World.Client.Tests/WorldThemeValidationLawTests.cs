using Puck.Overlays;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Pins the <c>theme</c> section: a document that authors none draws the engine's complete neutral theme; a
/// component style's color names a core role or its own color, and a role draws exactly what that role's slab slot
/// draws; the type floor and the style ranges refuse by name; the player's high-contrast preference floors every
/// scrim; and the resolve re-runs only for the slots its own tokens read.</summary>
public sealed class WorldThemeValidationLawTests {
    private static WorldThemeSection Default => WorldThemeSection.Default;

    private static OverlayThemeValues Resolve(WorldDefinition definition, WorldThemeResolve? resolver = null) => (resolver ?? new WorldThemeResolve(domains: new WorldValueDomainGuard())).Resolve(
        definition: definition,
        revision: 1,
        mirror: new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition))
    );
    private static WorldDefinition Themed(WorldThemeSection theme) => (Fixtures.BuildDocument() with { ThemeRaw = theme });
    private static RgbaColor SlabColor(ReadOnlySpan<uint> slab, OverlayColorRole role) => new(
        A: BitConverter.UInt32BitsToSingle(value: slab[((((int)role) * 4) + 3)]),
        B: BitConverter.UInt32BitsToSingle(value: slab[((((int)role) * 4) + 2)]),
        G: BitConverter.UInt32BitsToSingle(value: slab[((((int)role) * 4) + 1)]),
        R: BitConverter.UInt32BitsToSingle(value: slab[(((int)role) * 4)])
    );

    public static TheoryData<WorldThemeRole> Roles => [.. Enum.GetValues<WorldThemeRole>()];

    /// <summary>A world that authors no theme validates and draws the engine's neutral one: every surface has a color,
    /// a toast has a height, and the console's echo takes the role the default names.</summary>
    [Fact]
    public void AnUnthemedWorldDrawsTheEngineDefault() {
        var definition = Fixtures.BuildDocument();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: definition,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Same(
            actual: definition.Theme,
            expected: Default
        );
        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: Themed(theme: Default),
            neighbours: null,
            reason: out var themedReason
        ), userMessage: themedReason);

        var resolved = Resolve(definition: definition);

        Assert.True(condition: (resolved.Color.TextPrimary.A > 0f));
        Assert.True(condition: (resolved.Color.ScrimPanel.Alpha > 0f));
        Assert.True(condition: (resolved.Styles.Toast.MinHeight > 0f));
        Assert.Equal(
            actual: resolved.Styles.Console.Echo,
            expected: resolved.Color.Accent
        );
    }
    /// <summary>A style color naming a core role draws exactly what that role's own slab slot draws, for every role, and
    /// a style color of its own draws itself.</summary>
    [Theory]
    [MemberData(memberName: nameof(Roles))]
    public void AStyleColorNamingARoleDrawsThatRole(WorldThemeRole role) {
        var theme = (Default with {
            Styles = Default.Styles with {
                Console = Default.Styles.Console with { Echo = new WorldThemeStyleColor(Role: role) },
                Keycap = Default.Styles.Keycap with { Badge = new WorldThemeStyleColor(Color: new BindableColor(Raw: "#336699")) },
            },
        });
        var definition = Themed(theme: theme);

        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: definition,
            neighbours: null,
            reason: out var reason
        ), userMessage: reason);

        var resolved = Resolve(definition: definition);
        Span<uint> slab = stackalloc uint[OverlayTokenBlock.WordCount];

        OverlayTokenBlock.Write(
            destination: slab,
            theme: in resolved
        );
        Assert.Equal(
            actual: SlabColor(role: OverlayColorRole.ConsoleEcho, slab: slab),
            expected: SlabColor(role: WorldThemeResolve.RoleOf(role: role), slab: slab)
        );
        Assert.Equal(
            actual: SlabColor(role: OverlayColorRole.KeycapBadge, slab: slab),
            expected: new RgbaColor(A: 1f, B: (0x99 / 255f), G: (0x66 / 255f), R: (0x33 / 255f))
        );
    }
    /// <summary>A style color authors exactly one of a role or its own color; either both or neither refuses by name,
    /// beside the default's passing control.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStyleColorAuthoringBothOrNeitherRefusesByName(bool both) {
        var echo = (both
            ? new WorldThemeStyleColor(Color: new BindableColor(Raw: "#FFFFFF"), Role: WorldThemeRole.Accent)
            : new WorldThemeStyleColor());

        Laws.Refuses(
            definition: Themed(theme: (Default with { Styles = Default.Styles with { Console = Default.Styles.Console with { Echo = echo } } })),
            needle: "theme.styles.console.echo must author exactly one of role"
        );
    }
    /// <summary>A style's ranges and the type floor are enforced by name: an opacity outside [0, 1], a negative extent
    /// and a type size under the glyph coverage floor are authoring errors a boot refuses rather than draws.</summary>
    [Theory]
    [InlineData("theme.styles.bar.quietAlpha")]
    [InlineData("theme.styles.cursor.dotMaxHalf")]
    [InlineData("theme.styles.glow.haloBlur")]
    [InlineData("theme.type.microSize")]
    public void AnOutOfRangeTokenRefusesByName(string field) {
        var styles = Default.Styles;
        var theme = (field switch {
            "theme.styles.bar.quietAlpha" => (Default with { Styles = styles with { Bar = styles.Bar with { QuietAlpha = 1.5f } } }),
            "theme.styles.cursor.dotMaxHalf" => (Default with { Styles = styles with { Cursor = styles.Cursor with { DotMaxHalf = -1f } } }),
            "theme.styles.glow.haloBlur" => (Default with { Styles = styles with { Glow = styles.Glow with { HaloBlur = 0f } } }),
            _ => (Default with { Type = Default.Type with { MicroSize = 10f } }),
        });

        Laws.Refuses(
            definition: Themed(theme: theme),
            needle: field
        );
    }
    /// <summary>The player's high-contrast preference raises every scrim to the high-contrast floor and leaves a scrim
    /// already past it alone; turning it off restores the authored alpha.</summary>
    [Fact]
    public void HighContrastFloorsEveryScrim() {
        var color = Default.Color;
        var definition = Themed(theme: (Default with {
            Color = color with {
                ScrimChip = color.ScrimChip with { Alpha = new BindableScalar(literal: 0.95f) },
                ScrimPanel = color.ScrimPanel with { Alpha = new BindableScalar(literal: 0.3f) },
            },
        }));
        var resolver = new WorldThemeResolve(domains: new WorldValueDomainGuard());
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var normal = resolver.Resolve(definition: definition, mirror: mirror, revision: 1);

        resolver.HighContrast = true;

        var high = resolver.Resolve(definition: definition, mirror: mirror, revision: 1);

        Assert.Equal(actual: normal.Color.ScrimPanel.Alpha, expected: 0.3f);
        Assert.Equal(actual: high.Color.ScrimPanel.Alpha, expected: WorldThemeCapacity.HighContrastScrimAlpha);
        Assert.Equal(actual: high.Color.ScrimChip.Alpha, expected: 0.95f);
        Assert.Equal(actual: resolver.Resolutions, expected: 2);

        resolver.HighContrast = false;

        Assert.Equal(actual: resolver.Resolve(definition: definition, mirror: mirror, revision: 1).Color.ScrimPanel.Alpha, expected: 0.3f);
    }
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
            ThemeRaw = Default with {
                Color = Default.Color with { ScrimPanel = Default.Color.ScrimPanel with { Alpha = new BindableScalar(binding: "state.panelAlpha") } },
            },
        };

        var definition = Document(
            gauge: 1,
            panel: 0.9
        );
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);
        var theme = new WorldThemeResolve(domains: new WorldValueDomainGuard());

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
}
