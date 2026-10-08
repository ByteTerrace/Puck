using System.Reflection;

using Puck.Abstractions;
using Puck.World.Server;
using Xunit;
using static Puck.World.Testing.PerUserRootScan;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: no test in this suite can resolve the per-user world state root or the per-user device caches,
/// and no run's roots are process-global. The per-user defaults, <c>PuckUserDirectory.Resolve</c> of <c>world</c>,
/// <c>bakes</c>, <c>compiled-worlds</c> and <c>compilations</c>, are each named in exactly one place, the desktop
/// World's entry point (the top-level statements of <c>Program</c> in <c>Puck.World.dll</c>), which no test runs;
/// every other consumer takes the <see cref="WorldStateRoot"/> and <see cref="WorldCacheRoots"/> its host or fixture
/// hands it. The capture and schedule roots, which resolve under that state root or where <c>--capture-dir</c> and
/// <c>--schedule-dir</c> point, are built from the command line in one place, <see cref="WorldBootComposition.AddWorldBoot"/>,
/// and injected from there. The laws read the IL of every assembly in this suite's output directory: for a
/// <c>ldstr</c> of one of those names fed straight into <see cref="PuckUserDirectory.Resolve"/>, and for a
/// <c>newobj</c> of either boot-built root outside the boot. No root type carries static state, so none can hold an
/// override one composition leaks into the next. The desktop assembly is the control that proves each scan finds the
/// one site per name that exists.
/// </summary>
public sealed class WorldStateRootIsolationLawTests {
    private const byte NewObjectOpcode = 0x73;

    // The roots the boot builds from its command line and nothing else constructs.
    private static readonly Type[] BootBuiltRoots = [
        typeof(WorldCaptureRoot),
        typeof(WorldScheduleRoot),
    ];

    // Every Puck assembly in this suite's output directory, its own and the linked desktop assembly included.
    private static string[] OutputAssemblies() {
        var assemblies = Directory.EnumerateFiles(
            path: AppContext.BaseDirectory,
            searchPattern: "Puck*.dll"
        ).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Contains(
            collection: assemblies.Select(selector: Path.GetFileName),
            expected: Path.GetFileName(path: typeof(WorldStateRootIsolationLawTests).Assembly.Location)
        );
        Assert.Contains(
            collection: assemblies.Select(selector: Path.GetFileName),
            expected: Path.GetFileName(path: typeof(WorldBootComposition).Assembly.Location)
        );

        return assemblies;
    }
    // Every method in the assembly that constructs one of the boot-built roots.
    private static IEnumerable<string> BootBuiltRootSites(Assembly assembly) {
        var roots = BootBuiltRoots.Select(selector: static type => type.FullName).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var (site, method, il) in MethodBodies(assembly: assembly)) {
            for (var offset = 0; ((offset + 5) <= il.Length); offset++) {
                if ((il[offset] == NewObjectOpcode) && (OperandMethod(il: il, method: method, offset: (offset + 1)) is ConstructorInfo { DeclaringType.FullName: { } built }) && roots.Contains(item: built)) {
                    yield return $"{site} ({built})";
                }
            }
        }
    }

    [Fact]
    public void NoAssemblyThisSuiteLinksResolvesAPerUserRootOutsideTheDesktopEntryPoint() {
        // The scan every World suite shares, here where the desktop assembly whose entry point it allows is present.
        Assert.Equal(expected: DesktopAssembly, actual: Path.GetFileName(path: typeof(WorldBootComposition).Assembly.Location));
        AssertNoPerUserRootOutsideTheEntryPoint(suite: typeof(WorldStateRootIsolationLawTests).Assembly);
    }
    [Fact]
    public void TheDesktopCompositionRootIsTheOneSiteThatResolvesEach() {
        var sites = PerUserSites(assembly: typeof(WorldBootComposition).Assembly).ToArray();

        Assert.All(collection: sites, action: static site => Assert.True(condition: IsEntryPoint(site: site.Site), userMessage: site.Site));
        Assert.Equal(
            actual: sites.Select(selector: static site => site.Name).Order(comparer: StringComparer.Ordinal),
            expected: PerUserNames.Order(comparer: StringComparer.Ordinal)
        );
    }
    // A fixture builds its own capture and schedule roots, so this suite's own assembly is not scanned; every engine
    // assembly takes the ones the boot registered.
    [Fact]
    public void OnlyTheBootBuildsTheCaptureAndScheduleRoots() {
        var suite = Path.GetFileName(path: typeof(WorldStateRootIsolationLawTests).Assembly.Location);
        var sites = OutputAssemblies()
            .Where(predicate: path => !string.Equals(a: Path.GetFileName(path: path), b: suite, comparisonType: StringComparison.Ordinal))
            .SelectMany(selector: path => BootBuiltRootSites(assembly: Assembly.LoadFrom(assemblyFile: path)).Select(selector: site => $"{Path.GetFileName(path: path)}: {site}"))
            .ToArray();
        var boot = $"{typeof(WorldBootComposition).FullName}.{nameof(WorldBootComposition.AddWorldBoot)}";

        Assert.Equal(
            actual: sites.Order(comparer: StringComparer.Ordinal),
            expected: BootBuiltRoots.Select(selector: type => $"{Path.GetFileName(path: typeof(WorldBootComposition).Assembly.Location)}: {boot} ({type.FullName})").Order(comparer: StringComparer.Ordinal)
        );
    }
    [InlineData(typeof(WorldCacheRoots))]
    [InlineData(typeof(WorldCaptureRoot))]
    [InlineData(typeof(WorldScheduleRoot))]
    [InlineData(typeof(WorldStateRoot))]
    [Theory]
    public void NoRootCarriesStaticOrMutableState(Type root) {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        Assert.True(condition: root.IsSealed);
        Assert.Empty(collection: root.GetFields(bindingAttr: Declared).Where(predicate: static field => (field.IsStatic || !field.IsInitOnly)).Select(selector: static field => field.Name));
        Assert.Empty(collection: root.GetProperties(bindingAttr: Declared).Where(predicate: static property => (property.GetMethod?.IsStatic ?? (property.SetMethod?.IsStatic ?? false))).Select(selector: static property => property.Name));
    }
}
