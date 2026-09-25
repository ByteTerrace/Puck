using System.Buffers.Binary;
using System.Reflection;

using Puck.Abstractions;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: no test in this suite can resolve the per-user world state root. The default root,
/// <c>PuckUserDirectory.Resolve("world")</c>, is named in exactly one place, the desktop World's entry point (the
/// top-level statements of <c>Program</c> in <c>Puck.World.dll</c>), which no test runs; every other consumer takes the
/// <see cref="Puck.World.Server.WorldStateRoot"/> its host or fixture hands it. The law reads the IL of every assembly
/// in this suite's output directory, its own included, for a <c>ldstr "world"</c> fed straight into
/// <see cref="PuckUserDirectory.Resolve"/>; the desktop assembly is the control that proves the scan finds the one
/// site that exists.
/// </summary>
public sealed class WorldStateRootIsolationLawTests {
    private const byte CallOpcode = 0x28;
    private const byte LoadStringOpcode = 0x72;
    private const string StateRootName = "world";
    // The metadata table of a ldstr operand: the user-string heap.
    private const int UserStringTable = 0x70;

    // The top-level statements are asynchronous, so the entry point's own site sits in their state machine.
    private static bool IsEntryPoint(string site) => site.StartsWith(comparisonType: StringComparison.Ordinal, value: "Program+<<Main>$>");
    // Every method in the assembly that loads the state root's name and passes it straight to the per-user resolver,
    // as "<declaring type>.<method>". A type the runtime cannot load contributes the methods it can.
    private static IEnumerable<string> PerUserStateRootSites(Assembly assembly) {
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

                if (il is null) {
                    continue;
                }

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
                            (method.Module.ResolveMethod(metadataToken: BinaryPrimitives.ReadInt32LittleEndian(source: il.AsSpan(start: (offset + 6)))) is { } target) &&
                            (target.DeclaringType?.FullName == typeof(PuckUserDirectory).FullName) &&
                            (target.Name == nameof(PuckUserDirectory.Resolve))
                        );
                    } catch (ArgumentException) {
                        // The bytes matched inside some other instruction's operand and name no token.
                        resolvesStateRoot = false;
                    }

                    if (resolvesStateRoot) {
                        yield return $"{type.FullName}.{method.Name}";
                    }
                }
            }
        }
    }

    [Fact]
    public void NoAssemblyThisSuiteLinksResolvesThePerUserStateRootOutsideTheDesktopEntryPoint() {
        var desktop = Path.GetFileName(path: typeof(WorldBootComposition).Assembly.Location);
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
            expected: desktop
        );

        var sites = assemblies.SelectMany(selector: path => PerUserStateRootSites(assembly: Assembly.LoadFrom(assemblyFile: path)).Select(selector: site => (Assembly: Path.GetFileName(path: path), Site: site)))
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
}