using System.Buffers.Binary;
using System.Reflection;

using Puck.Abstractions;
using Xunit;

namespace Puck.World.Testing;

/// <summary>The scan that holds every World suite to its state-root isolation: no Puck assembly in a suite's output, its own
/// included, resolves a per-user root (<c>PuckUserDirectory.Resolve</c> of <c>world</c>, <c>bakes</c>,
/// <c>compiled-worlds</c> or <c>compilations</c>) outside the desktop World's entry point, which no test runs. It reads each
/// method's IL for a <c>ldstr</c> of one of those names fed straight into the resolver. <c>Puck.World.Tests</c> proves the
/// scan finds the one site per name that exists.</summary>
internal static class PerUserRootScan {
    // The desktop World, whose entry point is the one site allowed to resolve each name.
    internal const string DesktopAssembly = "Puck.World.dll";
    internal const byte CallOpcode = 0x28;
    internal const byte LoadStringOpcode = 0x72;
    // The metadata table of a ldstr operand: the user-string heap.
    internal const int UserStringTable = 0x70;

    // The per-user subdirectories only the desktop entry point may name: the state root and the three device caches.
    internal static readonly string[] PerUserNames = [
        "bakes",
        "compilations",
        "compiled-worlds",
        "world",
    ];

    // The top-level statements are asynchronous, so the entry point's own site sits in their state machine.
    internal static bool IsEntryPoint(string site) => site.StartsWith(comparisonType: StringComparison.Ordinal, value: "Program+<<Main>$>");
    // Every method body in the assembly with its IL, as "<declaring type>.<method>". A type the runtime cannot load
    // contributes the methods it can.
    internal static IEnumerable<(string Site, MethodBase Method, byte[] Il)> MethodBodies(Assembly assembly) {
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
    internal static MethodBase? OperandMethod(MethodBase method, byte[] il, int offset) {
        try {
            return method.Module.ResolveMethod(metadataToken: BinaryPrimitives.ReadInt32LittleEndian(source: il.AsSpan(start: offset)));
        } catch (ArgumentException) {
            return null;
        }
    }
    // Every method in the assembly that loads one of the per-user names and passes it straight to the per-user
    // resolver, with the name it resolves.
    internal static IEnumerable<(string Site, string Name)> PerUserSites(Assembly assembly) {
        foreach (var (site, method, il) in MethodBodies(assembly: assembly)) {
            for (var offset = 0; ((offset + 10) <= il.Length); offset++) {
                if ((il[offset] != LoadStringOpcode) || (il[(offset + 5)] != CallOpcode)) {
                    continue;
                }

                var stringToken = BinaryPrimitives.ReadInt32LittleEndian(source: il.AsSpan(start: (offset + 1)));

                if ((stringToken >>> 24) != UserStringTable) {
                    continue;
                }

                string? resolved;

                try {
                    resolved = ((
                        (method.Module.ResolveString(metadataToken: stringToken) is { } name) &&
                        PerUserNames.Contains(value: name) &&
                        (OperandMethod(il: il, method: method, offset: (offset + 6)) is { } target) &&
                        (target.DeclaringType?.FullName == typeof(PuckUserDirectory).FullName) &&
                        (target.Name == nameof(PuckUserDirectory.Resolve))
                    )
                        ? name
                        : null);
                } catch (ArgumentException) {
                    resolved = null;
                }

                if (resolved is not null) {
                    yield return (site, resolved);
                }
            }
        }
    }
    /// <summary>Asserts that no Puck assembly in the running suite's output directory, <paramref name="suite"/> among
    /// them, resolves a per-user root outside the desktop World's entry point.</summary>
    /// <param name="suite">The suite's own assembly, which must be among those scanned.</param>
    internal static void AssertNoPerUserRootOutsideTheEntryPoint(Assembly suite) {
        var assemblies = Directory.EnumerateFiles(
            path: AppContext.BaseDirectory,
            searchPattern: "Puck*.dll"
        ).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Contains(
            collection: assemblies.Select(selector: Path.GetFileName),
            expected: Path.GetFileName(path: suite.Location)
        );

        var sites = assemblies.SelectMany(selector: path => PerUserSites(assembly: Assembly.LoadFrom(assemblyFile: path)).Select(selector: site => (Assembly: Path.GetFileName(path: path), site.Site, site.Name)))
            .Where(predicate: site => !(string.Equals(a: site.Assembly, b: DesktopAssembly, comparisonType: StringComparison.Ordinal) && IsEntryPoint(site: site.Site)))
            .Select(selector: static site => $"{site.Assembly}: {site.Site} ({site.Name})")
            .ToArray();

        Assert.True(
            condition: (sites.Length == 0),
            userMessage: $"a per-user root is resolved outside the desktop entry point — take the WorldStateRoot or WorldCacheRoots the host hands you instead: {string.Join(separator: ", ", values: sites)}"
        );
    }
}
