using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a host's draw-site descriptor comes from the site table it was constructed with, so
/// every host handed the same table draws the same stream for a site; with no table, a site is named by its catalog
/// row name; and a table that does not cover the catalog one descriptor per ordinal is refused at construction.</summary>
public sealed class DrawSiteTableLawTests {
    private static StateArena Arena() {
        var section = EvaluatorFixture.Section();

        return new StateArena(
            catalog: StateCatalog.Compile(section: section),
            section: section,
            time: ArenaTime.Origin
        );
    }

    [Fact]
    public void ASiteTableNamesEverySiteAndNoTableNamesItByItsRow() {
        var arena = Arena();
        var sites = arena.Catalog.Descriptors.Select(selector: descriptor => $"doc.{descriptor.Name}").ToArray();
        var tabled = new ArenaEffectHost(
            arena: arena,
            sites: sites
        );
        var named = new ArenaEffectHost(arena: arena);

        for (var ordinal = 0; (ordinal < arena.Catalog.Count); ordinal++) {
            Assert.Equal(expected: sites[ordinal], actual: tabled.DrawSite(rowOrdinal: ordinal));
            Assert.Equal(expected: arena.Catalog.Descriptors[ordinal].Name, actual: named.DrawSite(rowOrdinal: ordinal));
        }
    }
    [Fact]
    public void ASiteTableThatDoesNotCoverTheCatalogIsRefused() {
        var arena = Arena();
        var partial = arena.Catalog.Descriptors.Skip(count: 1).Select(selector: descriptor => descriptor.Name).ToArray();

        Assert.Throws<ArgumentException>(testCode: () => new ArenaEffectHost(
            arena: arena,
            sites: partial
        ));
    }
}
