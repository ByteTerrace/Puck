using System.Buffers.Binary;
using System.Reflection;

using Puck.Abstractions;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: no test in this suite can resolve the per-user world state root, and no run's roots are
/// process-global. The default root, <c>PuckUserDirectory.Resolve("world")</c>, is named in exactly one place, the
/// desktop World's entry point (the top-level statements of <c>Program</c> in <c>Puck.World.dll</c>), which no test
/// runs; every other consumer takes the <see cref="WorldStateRoot"/> its host or fixture hands it. The capture and
/// schedule roots, which resolve under that state root or where <c>--capture-dir</c> and <c>--schedule-dir</c> point,
/// are built from the command line in one place, <see cref="WorldBootComposition.AddWorldBoot"/>, and injected from
/// there. The laws read the IL of every assembly in this suite's output directory: for a <c>ldstr "world"</c> fed
/// straight into <see cref="PuckUserDirectory.Resolve"/>, and for a <c>newobj</c> of either boot-built root outside
/// the boot. No root type carries static state, so none can hold an override one composition leaks into the next. The
/// desktop assembly is the control that proves each scan finds the one site that exists.
/// </summary>
public sealed class WorldStateRootIsolationLawTests {
    private const byte CallOpcode = 0x28;
    private const byte LoadStringOpcode = 0x72;
    private const byte NewObjectOpcode = 0x73;
    private const string StateRootName = "world";
    // The metadata table of a ldstr operand: the user-string heap.
    private const int UserStringTable = 0x70;

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
    // The top-level statements are asynchronous, so the entry point's own site sits in their state machine.
    private static bool IsEntryPoint(string site) => site.StartsWith(comparisonType: StringComparison.Ordinal, value: "Program+<<Main>$>");
    // Every method body in the assembly with its IL, as "<declaring type>.<method>". A type the runtime cannot load
    // contributes the methods it can.
    private static IEnumerable<(string Site, MethodBase Method, byte[] Il)> MethodBodies(Assembly assembly) {
        Type[] types;

        try {
            types = assembly.GetTypes();
        } catch (ReflectionTypeLoadException exception) {
            types = [.. exception.Types.OfType<Type>()];
        }

        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        foreach (var type in types) {
            foreach (var method in type.GetMethods(bindingAttr: Declared).Cast<MethodBase>().Concat(second: type.GetConstructors(bindingAttr: Declared))) {
                byte[]? il;

                try {
                    il = method.GetMethodBody()?.GetILAsByteArray();
                } catch (Exception exception) when ((exception is BadImageFormatException or FileNotFoundException or TypeLoadException)) {
                    continue;
                }

                if (il is not null) {
                    yield return ($"{type.FullName}.{method.Name}", method, il);
                }
            }
        }
    }
    // The method a call or newobj operand at offset names, or null when the bytes matched inside some other
    // instruction's operand and name no method.
    private static MethodBase? OperandMethod(MethodBase method, byte[] il, int offset) {
        try {
            return method.Module.ResolveMethod(metadataToken: BinaryPrimitives.ReadInt32LittleEndian(source: il.AsSpan(start: offset)));
        } catch (ArgumentException) {
            return null;
        }
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
    // Every method in the assembly that loads the state root's name and passes it straight to the per-user resolver.
    private static IEnumerable<string> PerUserStateRootSites(Assembly assembly) {
        foreach (var (site, method, il) in MethodBodies(assembly: assembly)) {
            for (var offset = 0; ((offset + 10) <= il.Length); offset++) {
                if ((il[offset] != LoadStringOpcode) || (il[(offset + 5)] != CallOpcode)) {
                    continue;
                }

                var stringToken = BinaryPrimitives.ReadInt32LittleEndian(source: il.AsSpan(start: (offset + 1)));

                if ((stringToken >>> 24) != UserStringTable) {
                    continue;
                }

                bool resolvesStateRoot;

                try {
                    resolvesStateRoot = (
                        (method.Module.ResolveString(metadataToken: stringToken) == StateRootName) &&
                        (OperandMethod(il: il, method: method, offset: (offset + 6)) is { } target) &&
                        (target.DeclaringType?.FullName == typeof(PuckUserDirectory).FullName) &&
                        (target.Name == nameof(PuckUserDirectory.Resolve))
                    );
                } catch (ArgumentException) {
                    resolvesStateRoot = false;
                }

                if (resolvesStateRoot) {
                    yield return site;
                }
            }
        }
    }

    [Fact]
    public void NoAssemblyThisSuiteLinksResolvesThePerUserStateRootOutsideTheDesktopEntryPoint() {
        var desktop = Path.GetFileName(path: typeof(WorldBootComposition).Assembly.Location);
        var sites = OutputAssemblies().SelectMany(selector: path => PerUserStateRootSites(assembly: Assembly.LoadFrom(assemblyFile: path)).Select(selector: site => (Assembly: Path.GetFileName(path: path), Site: site)))
            .Where(predicate: site => !(string.Equals(a: site.Assembly, b: desktop, comparisonType: StringComparison.Ordinal) && IsEntryPoint(site: site.Site)))
            .Select(selector: static site => $"{site.Assembly}: {site.Site}")
            .ToArray();

        Assert.True(
            condition: (sites.Length == 0),
            userMessage: $"the per-user world state root is resolved outside the desktop entry point — take the WorldStateRoot the host hands you instead: {string.Join(separator: ", ", values: sites)}"
        );
    }
    [Fact]
    public void TheDesktopCompositionRootIsTheOneSiteThatResolvesIt() => Assert.True(condition: IsEntryPoint(site: Assert.Single(collection: PerUserStateRootSites(assembly: typeof(WorldBootComposition).Assembly))));
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
