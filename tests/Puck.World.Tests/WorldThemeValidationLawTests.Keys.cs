using System.Numerics;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldThemeValidationLawTests {
    [Fact]
    public void A_bound_theme_alpha_uses_the_same_closed_domain_guard_and_transition_counts() {
        static WorldStateRow Alpha(int value) => new(CellName.Parse("alpha"), CellKind.Int,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value))]);
        var definition = Fixtures.BuildDocument().WithWorldState([Alpha(0)]) with {
            ThemeRaw = MinimalTheme() with { Elevation = MinimalTheme().Elevation with { BloomHaloAlpha = new("state.alpha") } },
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var live = definition;
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => live));
        mirror.Install(0, 0);
        var resolve = new WorldThemeResolve();
        var reports = new List<WorldValueDomainDiagnostic>();
        resolve.Domains.Transition += reports.Add;
        Assert.Equal(0f, resolve.Resolve(definition, 0, mirror).Elevation.BloomHaloAlpha);
        live = definition.WithWorldState([Alpha(2)]);
        mirror.Refresh(new WorldStateStamp(1, EngineTicks.PerSecond, new[] { 0 }, false));
        mirror.Apply(1f);
        Assert.Equal(1f, resolve.Resolve(definition, 0, mirror).Elevation.BloomHaloAlpha);
        var checks = resolve.Domains.Checks;
        _ = resolve.Resolve(definition, 0, mirror);
        Assert.Equal(checks, resolve.Domains.Checks);
        live = definition.WithWorldState([Alpha(0)]);
        mirror.Refresh(new WorldStateStamp(2, 2 * EngineTicks.PerSecond, new[] { 0 }, false));
        mirror.Apply(1f);
        Assert.Equal(0f, resolve.Resolve(definition, 0, mirror).Elevation.BloomHaloAlpha);
        Assert.Equal(2, reports.Count);
        Assert.Equal("theme.elevation.bloomHaloAlpha", reports[0].Field);
        Assert.True(resolve.TryRead(WorldThemeResolve.DomainClamps, out var clamps));
        Assert.Equal(1, clamps);
    }

    [Theory]
    [InlineData(true, 0.2f, "theme.color.scrimPanel.alpha.keys[1].value")]
    [InlineData(false, 2f, "theme.elevation.bloomHaloAlpha.keys[1].value")]
    public void Every_keyed_literal_owes_its_theme_fields_range(bool scrim, float invalid, string path) {
        var alpha = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 0.9f), new(4d, invalid)]));
        var theme = MinimalTheme();
        theme = scrim ? theme with { Color = theme.Color with { ScrimPanel = theme.Color.ScrimPanel with { Alpha = alpha } } }
            : theme with { Elevation = theme.Elevation with { BloomHaloAlpha = alpha } };
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]),
            ThemeRaw = theme,
        };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains(path, reason);
        Assert.Contains("must be in", reason);
    }

    [Fact]
    public void A_keyed_theme_uses_the_pure_resolver_and_only_its_used_clock_invalidates_it() {
        var accent = new BindableColor(new WorldKeys<BindableColor>("day", [new(0d, new("#000000")), new(4d, new("#FFFFFF"))]));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d), new WorldClock("unused", PeriodSeconds: 3d)]),
            ThemeRaw = MinimalTheme() with { Color = MinimalColor() with { Accent = accent } },
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => definition));
        var theme = new WorldThemeResolve();
        mirror.Install(0UL, 0UL);
        _ = theme.Resolve(definition, 1, mirror);
        mirror.Refresh(new WorldStateStamp(1UL, EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(1f);
        var expected = new WorldValueResolver(definition, mirror.Presented).Color(accent, Vector4.Zero);
        Assert.Equal(expected, mirror.Color(accent, Vector4.Zero));
        Assert.Equal(expected.X, theme.Resolve(definition, 1, mirror).Color.Accent.R);
        Assert.Equal(2, theme.Resolutions);
        // One complete used-clock period returns the same phase while the unused clock moves.
        mirror.Refresh(new WorldStateStamp(2UL, 9UL * EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(1f);
        _ = theme.Resolve(definition, 1, mirror);
        Assert.Equal(2, theme.Resolutions);
        var invalid = definition with { TimelineRaw = new WorldTimelineSection([new WorldClock("unused", PeriodSeconds: 3d)]) };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(invalid, out reason));
        Assert.Contains("theme.color.accent", reason);
        Assert.Contains("names no clock 'day'", reason);
    }

    [Fact]
    public void The_manifest_registers_key_values_and_used_state_clocks_but_no_unused_clock() {
        var accent = new BindableColor(new WorldKeys<BindableColor>("day", [new(0d, new("state.first")), new(0.5d, new("state.second"))]));
        var definition = Fixtures.BuildDocument().WithWorldState([
            new WorldStateRow(CellName.Parse("phase"), CellKind.Fixed, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(Puck.Maths.FixedQ4816.FromDouble(0.25d).Value))]),
            new WorldStateRow(CellName.Parse("unused-phase"), CellKind.Fixed, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(0L))]),
            new WorldStateRow(CellName.Parse("first"), CellKind.Text, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text("#000000"))]),
            new WorldStateRow(CellName.Parse("second"), CellKind.Text, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text("#FFFFFF"))]),
        ]) with {
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", State: "phase"), new WorldClock("unused", State: "unused-phase")]),
            ThemeRaw = MinimalTheme() with { Color = MinimalColor() with { Accent = accent } },
        };
        var manifest = WorldPresentationManifest.Of(definition);
        Assert.Contains(manifest.Bindings.ToArray(), binding => binding.Binding.Row == "phase");
        Assert.Contains(manifest.Bindings.ToArray(), binding => binding.Binding.Row == "first");
        Assert.Contains(manifest.Bindings.ToArray(), binding => binding.Binding.Row == "second");
        Assert.DoesNotContain(manifest.Bindings.ToArray(), binding => binding.Binding.Row == "unused-phase");
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => definition));
        mirror.Install(0UL, 0UL);
        var expected = new WorldValueResolver(definition, default).Color(accent, Vector4.Zero);
        Assert.Equal(0.5f, expected.X);
        Assert.Equal(expected, mirror.Color(accent, Vector4.Zero));
    }
}
