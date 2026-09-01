using System.Reflection;

using Xunit;

namespace Puck.Commands.Tests;

public sealed class ApiSurfaceTests {
    // The four shapes the README pins as "internal to construct and public only to read". A public
    // constructor on any of them would open a second way to mint dispatch state beside the router,
    // which is precisely the path the fixed-step contract exists to close: a caller who can build a
    // CommandSnapshot by hand can also dispatch input the router never ordered.
    private static readonly string[] InternalToConstruct = [
        "Puck.Commands.CommandContext",
        "Puck.Commands.CommandEntry",
        "Puck.Commands.CommandLane",
        "Puck.Commands.CommandSnapshot",
    ];
    private static readonly string[] RetiredShapeMarkers = [
        "Compat",
        "Deprecated",
        "Legacy",
        "Obsolete",
    ];

    private static IEnumerable<Type> ExportedTypes() =>
        typeof(CommandRegistry).Assembly.GetExportedTypes().OrderBy(keySelector: static type => type.FullName, comparer: StringComparer.Ordinal);

    [Fact]
    public void NoPublicMemberCarriesObsolete() {
        // Rule 5 has no deprecation ceremony to run: a member that should go is deleted with its
        // callers in the same change, so a published surface never carries [Obsolete] as a marker.
        // The compiler stamps its own [Obsolete] on every ref struct so down-level compilers refuse
        // the type; that one is a language mechanism, not a deprecation, so byref-like types are
        // read for their members only.
        var offenders = new List<string>();

        foreach (var type in ExportedTypes()) {
            if (!type.IsByRefLike && (type.GetCustomAttributes(attributeType: typeof(ObsoleteAttribute), inherit: false).Length != 0)) {
                offenders.Add(item: type.FullName!);
            }

            foreach (var member in type.GetMembers(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (member.GetCustomAttributes(attributeType: typeof(ObsoleteAttribute), inherit: false).Length != 0) {
                    offenders.Add(item: ((type.FullName + ".") + member.Name));
                }
            }
        }

        Assert.Equal(expected: string.Empty, actual: string.Join(separator: ", ", values: offenders));
    }
    [Fact]
    public void NoPublicTypeIsNamedForARetiredShape() {
        // Rule 5 again, read off the names: a "LegacyBinding" or "CommandCompat" on the published
        // surface would mean a second shape survived beside the one that replaced it.
        var offenders = ExportedTypes()
            .Where(predicate: static type => RetiredShapeMarkers.Any(predicate: marker => type.Name.Contains(comparisonType: StringComparison.Ordinal, value: marker)))
            .Select(selector: static type => type.FullName!);

        Assert.Equal(expected: string.Empty, actual: string.Join(separator: ", ", values: offenders));
    }
    [Fact]
    public void PublicSurfaceExposesNoMutableField() {
        // A public settable field is state a consumer can reach around every constructor and
        // validation path in this assembly. An enum's compiler-generated `value__` is the storage
        // slot of the enum itself and is not a field a caller can see or assign.
        var offenders = new List<string>();

        foreach (var type in ExportedTypes().Where(predicate: static type => !type.IsEnum)) {
            foreach (var field in type.GetFields(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (!field.IsLiteral && !field.IsInitOnly) {
                    offenders.Add(item: ((type.FullName + ".") + field.Name));
                }
            }
        }

        Assert.Equal(expected: string.Empty, actual: string.Join(separator: ", ", values: offenders));
    }
    [Fact]
    public void SnapshotShapesStayInternalToConstruct() {
        var assembly = typeof(CommandRegistry).Assembly;
        var offenders = new List<string>();

        foreach (var name in InternalToConstruct) {
            var type = assembly.GetType(name: name, throwOnError: false);

            // A rename that loses one of these types must fail here rather than pass vacuously:
            // an absent type proves nothing about the constructor the test is guarding.
            Assert.NotNull(@object: type);

            foreach (var constructor in type.GetConstructors(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
                offenders.Add(item: (((name + "(") + string.Join(separator: ", ", values: constructor.GetParameters().Select(selector: static parameter => parameter.ParameterType.Name))) + ")"));
            }
        }

        Assert.Equal(expected: string.Empty, actual: string.Join(separator: ", ", values: offenders));
    }
}
