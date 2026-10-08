using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Every list reachable from an identity projection's records section and facts row is owned by the record that
/// holds it. The identity projection wire caches each section's encoding and each facts row's validation by instance,
/// which holds only while no caller keeps a mutable alias into the instance. The law walks every type reachable from
/// <see cref="WorldStateSection"/> and <see cref="WorldStateRow"/> and, for each collection member, hands the member a
/// mutable list through both doors a record has, its constructor and its init accessor, and asserts the record keeps
/// an immutable copy rather than the list it was handed. A member typed as an array is refused outright, since its
/// elements stay writable through any reference to it, and so is a list of lists, whose inner lists a copy of the
/// outer one does not own. Red leg: a member that keeps the caller's collection.
/// </summary>
public sealed class StateOwnedListsLawTests {
    private static readonly Type[] Roots = [typeof(WorldStateSection), typeof(WorldStateRow)];

    // The record types the roots reach through their members, element types included.
    private static IEnumerable<Type> Reachable() {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(collection: Roots);

        while (pending.TryPop(result: out var type)) {
            type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

            if (type.IsArray) {
                pending.Push(item: type.GetElementType()!);

                continue;
            }
            if (type.IsGenericType) {
                foreach (var argument in type.GetGenericArguments()) {
                    pending.Push(item: argument);
                }

                continue;
            }
            if (
                type.IsPrimitive ||
                type.IsEnum ||
                (type.Namespace is not { } space) ||
                !space.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck") ||
                !seen.Add(item: type)
            ) {
                continue;
            }

            foreach (var derived in type.Assembly.GetTypes().Where(predicate: candidate => (candidate.IsSubclassOf(c: type) && !candidate.IsAbstract))) {
                pending.Push(item: derived);
            }
            foreach (var property in Members(type: type)) {
                pending.Push(item: property.PropertyType);
            }

            yield return type;
        }
    }
    private static IEnumerable<PropertyInfo> Members(Type type) => type.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)
        .Where(predicate: static property => ((property.GetIndexParameters().Length == 0) && (property.GetMethod is not null) && (property.SetMethod is not null)));
    private static bool IsCollection(Type type) => ((type != typeof(string)) && typeof(IEnumerable).IsAssignableFrom(c: type));
    // A mutable list of the member's element type, holding nothing, which the member may only keep by copying.
    private static object? MutableList(Type type) {
        if (
            !type.IsGenericType ||
            ((type.GetGenericTypeDefinition() is var definition) && (definition != typeof(IReadOnlyList<>)) && (definition != typeof(IReadOnlyCollection<>)) && (definition != typeof(IEnumerable<>)))
        ) {
            return null;
        }

        return Activator.CreateInstance(type: typeof(List<>).MakeGenericType(typeArguments: type.GetGenericArguments()));
    }
    private static bool Owned(object? kept, object handed) => (
        (kept is not null) &&
        !ReferenceEquals(objA: kept, objB: handed) &&
        (kept.GetType().IsGenericType && (kept.GetType().GetGenericTypeDefinition() == typeof(ImmutableArray<>)))
    );
    // The instance the record's primary constructor builds with `handed` for the member's parameter and defaults for
    // the rest, or null when the type has no constructor taking that parameter or refuses the defaults.
    private static object? Constructed(Type type, PropertyInfo member, object handed) {
        foreach (var constructor in type.GetConstructors()) {
            var parameters = constructor.GetParameters();

            if (!parameters.Any(predicate: parameter => string.Equals(a: parameter.Name, b: member.Name, comparisonType: StringComparison.Ordinal))) {
                continue;
            }

            var arguments = parameters.Select(selector: parameter => (string.Equals(a: parameter.Name, b: member.Name, comparisonType: StringComparison.Ordinal)
                ? handed
                : (parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : (parameter.ParameterType.IsValueType ? Activator.CreateInstance(type: parameter.ParameterType) : null)))).ToArray();

            try {
                return constructor.Invoke(parameters: arguments);
            } catch (TargetInvocationException) {
                return null;
            }
        }

        return null;
    }

    [Fact]
    public void EveryCollectionACachedStateRecordReachesIsOwned() {
        var unowned = new List<string>();
        var checkedMembers = 0;

        foreach (var type in Reachable().Where(predicate: static type => (type.IsClass && !type.IsAbstract))) {
            foreach (var member in Members(type: type).Where(predicate: member => IsCollection(type: member.PropertyType))) {
                var name = $"{type.Name}.{member.Name}";

                if (member.PropertyType.IsArray) {
                    unowned.Add(item: $"{name} is an array");

                    continue;
                }
                if (member.PropertyType.GetGenericArguments().Any(predicate: static argument => IsCollection(type: argument))) {
                    unowned.Add(item: $"{name} holds collections, whose own lists a copy of the outer one does not own");

                    continue;
                }
                if (MutableList(type: member.PropertyType) is not { } handed) {
                    unowned.Add(item: $"{name} is a {member.PropertyType.Name}, which the law cannot hand a mutable list");

                    continue;
                }

                checkedMembers++;

                // The init accessor, on an instance no constructor ran for.
                var instance = RuntimeHelpers.GetUninitializedObject(type: type);

                member.SetValue(obj: instance, value: handed);
                if (!Owned(handed: handed, kept: member.GetValue(obj: instance))) {
                    unowned.Add(item: $"{name} keeps the list its init accessor is handed");
                }

                // The constructor, where the type has one taking the member.
                if (
                    (Constructed(handed: handed, member: member, type: type) is { } built) &&
                    !Owned(handed: handed, kept: member.GetValue(obj: built))
                ) {
                    unowned.Add(item: $"{name} keeps the list its constructor is handed");
                }
            }
        }

        Assert.True(condition: (checkedMembers > 0));
        Assert.True(
            condition: (unowned.Count == 0),
            userMessage: string.Join(separator: Environment.NewLine, values: unowned)
        );
    }
}
