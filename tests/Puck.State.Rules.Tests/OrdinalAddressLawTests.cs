using System.Reflection;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A compiled program addresses every row and cell by ordinal and interned key; an authored name leaves the
/// compiler only inside a read-back spelling.</summary>
public sealed class OrdinalAddressLawTests {
    // What a read-back may carry: a rule's own name, an effect's or gate's authored spelling, a family's or
    // subprogram's name, a stable undo-group target, a zone table's authored entries and token domain, and a text literal. Every other
    // text-valued member of a compiled shape would be an address in disguise.
    private static readonly string[] ReadBackMembers = [
        "Describe",
        "Group",
        "Name",
        "Names",
        "Spelling",
        "Text",
        "TokenDomain",
    ];

    // Everything a compiled program can reach: the two roots the evaluator is handed, every registered fact case,
    // and the transitive closure of their public members' types inside the compiler's own namespace.
    private static IEnumerable<Type> CompiledShapes() {
        var pending = new Stack<Type>();
        var swept = new HashSet<Type>();

        foreach (var type in typeof(CompiledRule).Assembly.GetTypes()) {
            if (
                Sweepable(type: type) &&
                (
                typeof(IRuleOperand).IsAssignableFrom(c: type) ||
                typeof(IRuleEffect).IsAssignableFrom(c: type) ||
                typeof(IRuleKey).IsAssignableFrom(c: type) ||
                (type == typeof(CompiledRule)) ||
                (type == typeof(CompiledRuleGroup)))
            ) {
                pending.Push(item: type);
            }
        }
        while (pending.Count != 0) {
            var type = pending.Pop();

            if (!swept.Add(item: type)) {
                continue;
            }

            yield return type;

            foreach (var property in type.GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)) {
                foreach (var reached in Unwrap(type: property.PropertyType)) {
                    if (
                        Sweepable(type: reached) &&
                        !swept.Contains(item: reached)
                    ) {
                        pending.Push(item: reached);
                    }
                }
            }
        }
    }
    private static IEnumerable<Type> Unwrap(Type type) {
        if (type.IsArray) {
            return Unwrap(type: type.GetElementType()!);
        }
        if (type.IsGenericType) {
            return type.GetGenericArguments().SelectMany(selector: Unwrap);
        }

        return [type];
    }
    // A member whose value is text, whether one string or a list of them.
    private static IEnumerable<PropertyInfo> TextMembers(Type type) => type
        .GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
        .Where(predicate: static property => Unwrap(type: property.PropertyType).Contains(value: typeof(string)));
    private static bool Sweepable(Type type) => (
        type.IsPublic &&
        !type.IsAbstract &&
        !type.IsInterface &&
        !type.IsEnum &&
        (type.Namespace == typeof(CompiledRule).Namespace)
    );

    [Fact]
    public void EveryCompiledShapeCarriesItsAddressesAsOrdinalsAndKeys() {
        var swept = 0;

        foreach (var type in CompiledShapes()) {
            swept++;
            foreach (var property in TextMembers(type: type)) {
                Assert.Contains(
                    collection: ReadBackMembers,
                    expected: property.Name
                );
            }
        }

        Assert.NotEqual(
            actual: swept,
            expected: 0
        );
    }
    [Fact]
    public void TheSweepReachesTheShapesTheRuleAndItsGroupsCarry() {
        var swept = CompiledShapes().ToHashSet();

        Assert.Contains(
            collection: swept,
            expected: typeof(ZoneTable)
        );
        Assert.Contains(
            collection: swept,
            expected: typeof(LiveRow)
        );
        Assert.Contains(
            collection: swept,
            expected: typeof(CompiledRuleGroup)
        );
    }
    [Fact]
    public void EveryPermittedReadBackMemberIsOneASweptShapeCarries() {
        var carried = CompiledShapes()
            .SelectMany(selector: TextMembers)
            .Select(selector: static property => property.Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var member in ReadBackMembers) {
            Assert.Contains(
                collection: carried,
                expected: member
            );
        }
    }
    [Fact]
    public void ACompiledRulesReadAndWriteSetsAreOrdinalOnly() {
        var context = RulesFixture.Context();
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [
                    new ActionEffect.SetState(
                        Key: "$each",
                        State: "codes",
                        Value: 1m
                    ),
                    new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                        From: "deck",
                        Selector: ZoneSelector.First,
                        To: "hand"
                    )),
                ],
                forEach: "tokens",
                gate: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.Greater,
                    State: "$reduce:count:deck",
                    Value: 0m
                ),
                name: "ordinals"
            )
        );
        var reads = RuleDataflow.Reads(rule: compiled);
        var writes = RuleDataflow.Writes(rule: compiled);

        Assert.NotEmpty(collection: reads);
        Assert.NotEmpty(collection: writes);
        Assert.All(
            action: access => Assert.InRange(
                actual: access.RowOrdinal,
                high: (context.Catalog.Count - 1),
                low: 0
            ),
            collection: reads.Concat(second: writes)
        );
    }
    [Fact]
    public void ARulesDescribeTextIsTheOnlyPlaceItsAuthoredNamesSurvive() {
        var compiled = RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Greater,
                State: "score",
                Value: 1m
            ),
            name: "named"
        ));

        Assert.Equal(
            actual: compiled.Describe,
            expected: "named"
        );
        Assert.Contains(
            actualString: compiled.Gate[0].Describe,
            expectedSubstring: "score"
        );
        Assert.Equal(
            actual: Assert.IsType<StateCellOperand>(@object: compiled.Gate[0].LeftSource.Operand).RowOrdinal,
            expected: 0
        );
    }
}
