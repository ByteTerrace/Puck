using System.Numerics;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldThemeValidationLawTests {
    [Fact]
    public void A_bound_theme_alpha_uses_the_same_closed_domain_guard_and_transition_counts() {
        static WorldStateRow Alpha(int value) => new(CellName.Parse(candidate: "alpha"), CellKind.Int,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: value))]);
        var definition = Fixtures.BuildDocument().WithWorldState([Alpha(value: 0)]) with {
            ThemeRaw = MinimalTheme() with { Elevation = MinimalTheme().Elevation with { BloomHaloAlpha = new(binding: "state.alpha") } },
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        var live = definition;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => live));

        mirror.Install(engineTick: 0, tick: 0);
        var resolve = new WorldThemeResolve();
        var reports = new List<WorldValueDomainDiagnostic>();

        resolve.Domains.Transition += reports.Add;
        Assert.Equal(0f, resolve.Resolve(definition: definition, mirror: mirror, revision: 0).Elevation.BloomHaloAlpha);
        live = definition.WithWorldState([Alpha(value: 2)]);
        mirror.Refresh(stamp: new WorldStateStamp(EngineTick: EngineTicks.PerSecond, Everything: false, MovedRows: new[] { 0 }, Tick: 1));
        mirror.Apply(fraction: 1f);
        Assert.Equal(1f, resolve.Resolve(definition: definition, mirror: mirror, revision: 0).Elevation.BloomHaloAlpha);
        var checks = resolve.Domains.Checks;

        _ = resolve.Resolve(definition: definition, mirror: mirror, revision: 0);
        Assert.Equal(checks, resolve.Domains.Checks);
        live = definition.WithWorldState([Alpha(value: 0)]);
        mirror.Refresh(stamp: new WorldStateStamp(EngineTick: (2 * EngineTicks.PerSecond), Everything: false, MovedRows: new[] { 0 }, Tick: 2));
        mirror.Apply(fraction: 1f);
        Assert.Equal(0f, resolve.Resolve(definition: definition, mirror: mirror, revision: 0).Elevation.BloomHaloAlpha);
        Assert.Equal(2, reports.Count);
        Assert.Equal("theme.elevation.bloomHaloAlpha", reports[0].Field);
        Assert.True(condition: resolve.TryRead(kind: WorldThemeResolve.DomainClamps, value: out var clamps));
        Assert.Equal(actual: clamps, expected: 1);
    }
    [InlineData(true, 0.2f, "theme.color.scrimPanel.alpha.keys[1].value")]
    [InlineData(false, 2f, "theme.elevation.bloomHaloAlpha.keys[1].value")]
    [Theory]
    public void Every_keyed_literal_owes_its_theme_fields_range(bool scrim, float invalid, string path) {
        var alpha = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, 0.9f), new(4d, invalid)]));
        var theme = MinimalTheme();

        theme = (scrim ? theme with { Color = theme.Color with { ScrimPanel = theme.Color.ScrimPanel with { Alpha = alpha } } }
            : theme with { Elevation = theme.Elevation with { BloomHaloAlpha = alpha } });
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]),
            ThemeRaw = theme,
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: path);
        Assert.Contains(actualString: reason, expectedSubstring: "must be in");
    }
    [Fact]
    public void A_keyed_theme_uses_the_pure_resolver_and_only_its_used_clock_invalidates_it() {
        var accent = new BindableColor(keys: new WorldKeys<BindableColor>(Clock: "day", Keys: [new(0d, new(Raw: "#000000")), new(4d, new(Raw: "#FFFFFF"))]));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d), new WorldClock("unused", PeriodSeconds: 3d)]),
            ThemeRaw = MinimalTheme() with { Color = MinimalColor() with { Accent = accent } },
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));
        var theme = new WorldThemeResolve();

        mirror.Install(engineTick: 0UL, tick: 0UL);
        _ = theme.Resolve(definition: definition, mirror: mirror, revision: 1);
        mirror.Refresh(stamp: new WorldStateStamp(1UL, EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(fraction: 1f);
        var expected = new WorldValueResolver(definition, mirror.Presented).Color(accent, Vector4.Zero);

        Assert.Equal(expected, mirror.Color(color: accent, fallback: Vector4.Zero));
        Assert.Equal(expected.X, theme.Resolve(definition: definition, mirror: mirror, revision: 1).Color.Accent.R);
        Assert.Equal(2, theme.Resolutions);
        // One complete used-clock period returns the same phase while the unused clock moves.
        mirror.Refresh(stamp: new WorldStateStamp(2UL, (9UL * EngineTicks.PerSecond), ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(fraction: 1f);
        _ = theme.Resolve(definition: definition, mirror: mirror, revision: 1);
        Assert.Equal(2, theme.Resolutions);
        var invalid = definition with { TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("unused", PeriodSeconds: 3d)]) };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: invalid, reason: out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "theme.color.accent");
        Assert.Contains(actualString: reason, expectedSubstring: "names no clock 'day'");
    }
    [Fact]
    public void The_manifest_registers_key_values_and_used_state_clocks_but_no_unused_clock() {
        var accent = new BindableColor(keys: new WorldKeys<BindableColor>(Clock: "day", Keys: [new(0d, new(Raw: "state.first")), new(0.5d, new(Raw: "state.second"))]));
        var definition = Fixtures.BuildDocument().WithWorldState([
            new WorldStateRow(CellName.Parse(candidate: "phase"), CellKind.Fixed, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(rawBits: Puck.Maths.FixedQ4816.FromDouble(value: 0.25d).Value))]),
            new WorldStateRow(CellName.Parse(candidate: "unused-phase"), CellKind.Fixed, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(rawBits: 0L))]),
            new WorldStateRow(CellName.Parse(candidate: "first"), CellKind.Text, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text(value: "#000000"))]),
            new WorldStateRow(CellName.Parse(candidate: "second"), CellKind.Text, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text(value: "#FFFFFF"))]),
        ]) with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", State: "phase"), new WorldClock("unused", State: "unused-phase")]),
            ThemeRaw = MinimalTheme() with { Color = MinimalColor() with { Accent = accent } },
        };
        var manifest = WorldPresentationManifest.Of(definition: definition);

        Assert.Contains(collection: manifest.Bindings.ToArray(), filter: binding => (binding.Binding.Row == "phase"));
        Assert.Contains(collection: manifest.Bindings.ToArray(), filter: binding => (binding.Binding.Row == "first"));
        Assert.Contains(collection: manifest.Bindings.ToArray(), filter: binding => (binding.Binding.Row == "second"));
        Assert.DoesNotContain(collection: manifest.Bindings.ToArray(), filter: binding => (binding.Binding.Row == "unused-phase"));
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(engineTick: 0UL, tick: 0UL);
        var expected = new WorldValueResolver(definition, default).Color(accent, Vector4.Zero);

        Assert.Equal(actual: expected.X, expected: 0.5f);
        Assert.Equal(expected, mirror.Color(color: accent, fallback: Vector4.Zero));
    }
}
