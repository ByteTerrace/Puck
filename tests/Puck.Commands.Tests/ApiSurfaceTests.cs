using System.Reflection;
using System.Runtime.CompilerServices;

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
    // Public settable state that is DELIBERATE, one line of reasoning per entry. A property that reaches this list
    // without one is a defect wearing an allow-list entry.
    private static readonly string[] MutableByDesign = [
        // The frame-thread hold gate: a handler arms it to defer the REST of the frame's queued lines, so it is a
        // control the host is meant to set from inside Collect rather than construction-time state. Its backing field
        // is volatile and the threading contract is written at the declaration.
        "Puck.Commands.TextCommandSource.HoldGate",
    ];
    private static readonly string[] RetiredShapeMarkers = [
        "Compat",
        "Deprecated",
        "Legacy",
        "Obsolete",
    ];

    // An `init` accessor is an ordinary setter whose return parameter carries a required custom modifier of
    // IsExternalInit — the only thing that distinguishes the two in metadata.
    private static bool IsInitOnly(MethodInfo setter) =>
        (Array.IndexOf(array: setter.ReturnParameter.GetRequiredCustomModifiers(), value: typeof(IsExternalInit)) >= 0);
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
    public void PublicSurfaceExposesNoUndeclaredMutableProperty() {
        // The field walk above is only half the question. A public non-init SETTER is the same reach-around a public
        // field is — state a consumer can rewrite after every constructor and validation path in this assembly has
        // run — and nothing here could see one, so a settable property could be added to any of the 100-plus exported
        // types without a single test noticing. An `init` setter is not that: it can only run while the object is
        // being created, which is what the declarative parts of a CommandDefinition are built with.
        var offenders = new List<string>();

        foreach (var type in ExportedTypes()) {
            foreach (var property in type.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if ((property.SetMethod is not { IsPublic: true } setter) || IsInitOnly(setter: setter)) {
                    continue;
                }

                var name = ((type.FullName + ".") + property.Name);

                if (!MutableByDesign.Contains(value: name, comparer: StringComparer.Ordinal)) {
                    offenders.Add(item: name);
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
