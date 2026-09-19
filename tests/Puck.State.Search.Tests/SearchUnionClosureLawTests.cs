using System.Reflection;

using Xunit;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: every type this project marks <see cref="UnionAttribute"/> is closed — a private
/// or private-protected base constructor and only sealed cases nested in the base itself — so a case declared in
/// any assembly the project's closure can see fails this suite.</summary>
public sealed class SearchUnionClosureLawTests {
    private static IEnumerable<Type> Unions() => typeof(ArenaSearchWrite).Assembly
        .GetTypes()
        .Where(predicate: static type => (type.GetCustomAttribute<UnionAttribute>(inherit: false) is not null))
        .OrderBy(keySelector: static type => type.FullName, comparer: StringComparer.Ordinal);
    private static IEnumerable<Type> VisibleTypes() => AppDomain.CurrentDomain
        .GetAssemblies()
        .Where(predicate: static assembly => (assembly.GetName().Name?.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "Puck."
    ) ?? false))
        .SelectMany(selector: static assembly => assembly.GetTypes());

    [Fact]
    public void EveryUnionIsAnAbstractCaseHierarchyWithAPrivateBaseConstructor() {
        var unions = Unions().ToArray();

        Assert.NotEmpty(collection: unions);
        foreach (var union in unions) {
            Assert.True(
                condition: union.IsAbstract,
                userMessage: $"{union.FullName} is marked [Union] but is not an abstract case hierarchy."
            );

            var constructors = union.GetConstructors(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotEmpty(collection: constructors);
            foreach (var constructor in constructors) {
                // A copy constructor the record shape synthesizes is protected; the authored constructor is the
                // one that must be shut, so only a non-copy constructor is judged here.
                if (
                    (constructor.GetParameters() is [{ } only]) &&
                    only.ParameterType.IsAssignableFrom(c: union)
                ) {
                    continue;
                }

                Assert.True(
                    condition: (constructor.IsPrivate || constructor.IsFamilyAndAssembly),
                    userMessage: $"{union.FullName} declares a constructor a case outside it could call."
                );
            }
        }
    }
    [Fact]
    public void NoAssemblyTheProjectCanSeeDeclaresACaseOutsideItsUnion() {
        foreach (var union in Unions()) {
            foreach (var type in VisibleTypes()) {
                if (!union.IsAssignableFrom(c: type) || (type == union)) {
                    continue;
                }

                Assert.True(
                    condition: (type.IsSealed && (type.DeclaringType == union)),
                    userMessage: $"{type.FullName} is a case of {union.FullName} declared outside it."
                );
            }
        }
    }
}
