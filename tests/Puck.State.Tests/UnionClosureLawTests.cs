using System.Reflection;

using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: every type marked <see cref="UnionAttribute"/> in the <c>Puck.</c> assemblies this
/// test host loads is closed; the private base constructor is what makes a case declared elsewhere uncompilable. A
/// class-shaped union declares a private or private-protected constructor and only sealed cases nested in itself,
/// and no assembly the library can see declares another case; a struct-shaped carrier implements
/// <see cref="IUnion"/> and is sealed by being a value type, and its <see cref="IUnion.Value"/> is
/// <see langword="null"/> exactly for the caseless default. A case added anywhere else fails this suite.</summary>
public sealed class UnionClosureLawTests {
    private static readonly Assembly Library = typeof(UnionAttribute).Assembly;

    private static IEnumerable<Type> Unions() => Library
        .GetTypes()
        .Where(predicate: static type => (type.GetCustomAttribute<UnionAttribute>(inherit: false) is not null))
        .OrderBy(keySelector: static type => type.FullName, comparer: StringComparer.Ordinal);
    // Every assembly the library's own closure can reach, so a case declared in a project that references
    // Puck.State is visible to the sweep rather than silently outside it.
    private static IEnumerable<Type> VisibleTypes() => AppDomain.CurrentDomain
        .GetAssemblies()
        .Where(predicate: static assembly => (assembly.GetName().Name?.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "Puck."
    ) ?? false))
        .SelectMany(selector: static assembly => assembly.GetTypes());

    [Fact]
    public void EveryUnionIsEitherAnAbstractCaseHierarchyOrAValueCarrier() {
        var unions = Unions().ToArray();

        Assert.NotEmpty(collection: unions);

        foreach (var union in unions) {
            if (union.IsValueType) {
                Assert.True(
                    condition: typeof(IUnion).IsAssignableFrom(c: union),
                    userMessage: $"{union.FullName} is a [Union] struct that does not implement IUnion."
                );
                Assert.True(
                    condition: (union.GetCustomAttributesData().Any(predicate: static data => string.Equals(
                    a: data.AttributeType.FullName,
                    b: "System.Runtime.CompilerServices.IsReadOnlyAttribute",
                    comparisonType: StringComparison.Ordinal
                ))),
                    userMessage: $"{union.FullName} is a [Union] struct that is not readonly."
                );
            } else {
                Assert.True(
                    condition: union.IsAbstract,
                    userMessage: $"{union.FullName} is a [Union] class that is not abstract."
                );
            }
        }
    }
    [Fact]
    public void EveryClassUnionDeclaresOnlyPrivateOrPrivateProtectedConstructors() {
        foreach (var union in Unions().Where(predicate: static type => !type.IsValueType)) {
            var constructors = union.GetConstructors(bindingAttr: BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

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
                    userMessage: $"{union.FullName} declares a constructor that is neither private nor private protected, so a case can be added from outside."
                );
            }
        }
    }
    [Fact]
    public void EveryCaseIsSealedAndNestedInTheUnionThatDeclaresIt() {
        var unions = Unions().Where(predicate: static type => !type.IsValueType).ToHashSet();

        Assert.NotEmpty(collection: unions);

        foreach (var union in unions) {
            var cases = union
                .GetNestedTypes(bindingAttr: BindingFlags.Public)
                .Where(predicate: type => union.IsAssignableFrom(c: type))
                .ToArray();

            Assert.NotEmpty(collection: cases);

            foreach (var declared in cases) {
                Assert.True(
                    condition: declared.IsSealed,
                    userMessage: $"{declared.FullName} is a case of {union.FullName} and is not sealed."
                );
            }
        }

        foreach (var candidate in VisibleTypes()) {
            for (var parent = candidate.BaseType; (parent is not null); parent = parent.BaseType) {
                if (!unions.Contains(item: parent)) {
                    continue;
                }

                Assert.True(
                    condition: (
                        ReferenceEquals(
                        objA: candidate.DeclaringType,
                        objB: parent
                    ) &&
                        candidate.IsSealed
                    ),
                    userMessage: $"{candidate.FullName} derives from the closed union {parent.FullName} but is not a sealed case nested in it."
                );
            }
        }
    }
    [Fact]
    public void AValueCarriersUntypedCaseIsNullExactlyForTheDefault() {
        Assert.Null(@object: ((IUnion)default(CellValue)).Value);
        Assert.NotNull(@object: ((IUnion)CellValue.Int(value: 0L)).Value);
        Assert.NotNull(@object: ((IUnion)CellValue.Text(value: string.Empty)).Value);
    }
    [Fact]
    public void StateDomainIsSweptAsAClosedUnion() {
        Assert.Contains(
            collection: Unions(),
            filter: static type => (type == typeof(StateDomain))
        );
        Assert.Contains(
            collection: Unions(),
            filter: static type => (type == typeof(CellValue))
        );
    }
}
