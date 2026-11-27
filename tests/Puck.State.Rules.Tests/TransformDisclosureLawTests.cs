using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Transforms cannot publish hidden selectors, membership or live cell values through their results.</summary>
public sealed class TransformDisclosureLawTests {
    private static readonly StateVisibility Private = new(Readers: ["seat1"]);

    // Every row reserves per-cell policy storage, so a runtime law can restrict one cell; the named rows carry the
    // declared row policy the compiler judges.
    private static StateSection WithPolicies(params string[] names) {
        var section = TransformFixture.Section();
        return section with { Rows = section.Rows!.Select(row => row with {
            CellVisibility = true,
            Visibility = names.Contains(row.Name.Value) ? Private : row.Visibility
        }).ToArray() };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenSelectorsCannotChoosePublicTransformResults(bool liveEnd) {
        var section = WithPolicies(liveEnd ? "rankValue" : "pointer");
        var context = TransformFixture.Context(section: section);
        var transform = liveEnd
            ? (StateTransform)new StateTransform.Transfer(From: "deck", To: "$zones[(rankValue)]", Selector: ZoneSelector.First)
            : new StateTransform.WriteSet(Row: "target", Set: "scores", SetKey: "$cell:pointer:$value", Value: 1L);
        var rule = new Rule(Name: TransformFixture.Name(value: "leak"), Zones: ["hand"], Effects: [new ActionEffect.TransformState(Transform: transform)]);

        var exception = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll(context: context, rules: [rule]));
        Assert.Contains("TransformWidensAudience", exception.Message);
    }

    [Fact]
    public void PrivateZoneMembershipCannotTransferIntoAPublicZone() {
        var context = TransformFixture.Context(section: WithPolicies("deck"));
        Assert.False(RuleCompiler.TryResolveTransform(new StateTransform.Transfer(From: "deck", To: "hand", Selector: ZoneSelector.First), context, out _, out var reason));
        Assert.Contains("TransformWidensAudience", reason);
    }

    [Fact]
    public void ExplicitRuleWritesAndObserveStillPublishTheirDeclaredResults() {
        var section = WithPolicies("rankValue", "truth");
        var context = TransformFixture.Context(section: section);
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(
            Name: TransformFixture.Name(value: "publish"), Mode: ActionTriggerMode.Level,
            Gate: EvaluatorFixture.Compare(row: "rankValue", comparison: ExpressionOp.Equal, value: 0m),
            Effects: [new ActionEffect.SetState(State: "mask", FromState: "rankValue"),
                new ActionEffect.TransformState(Transform: new StateTransform.Observe(Row: "known"))])]);
        var host = TransformFixture.Host(context: context, section: section);
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);

        Assert.Empty(evaluator.Diagnostics());
        Assert.Equal(CellValue.Int(value: 0L), host.Arena.Read(rowOrdinal: TransformFixture.Ordinal(context: context, name: "mask"),
            key: host.Arena.Keys.Intern(name: StateRow.SlotKey)));
        Assert.Equal("a=4,b=6", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "known")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HiddenControlFlowCannotChooseAPublicTransform(bool branch, bool runtime) {
        var section = runtime ? WithPolicies() : WithPolicies("rankValue");
        var context = TransformFixture.Context(section: section);
        var gate = EvaluatorFixture.Compare(row: "rankValue", comparison: ExpressionOp.Equal, value: 0m);
        var transform = new ActionEffect.TransformState(Transform: new StateTransform.Sort(Row: "deck", By: [new SortKey(Row: "rank")]));
        var rule = new Rule(Name: TransformFixture.Name(value: "conditional"), Mode: ActionTriggerMode.Level,
            Gate: branch ? null : gate, Effects: [branch ? new ActionEffect.If(Condition: gate, Then: [transform]) : transform]);
        if (!runtime) {
            var exception = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll(context: context, rules: [rule]));
            Assert.Contains("TransformWidensAudience", exception.Message);
            return;
        }
        var rules = RuleCompiler.CompileAll(context: context, rules: [rule]);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(host.Arena.TryWriteVisibility(rowOrdinal: TransformFixture.Ordinal(context: context, name: "rankValue"),
            key: host.Arena.Keys.Intern(name: StateRow.SlotKey), visibility: Private));
        var evaluator = new RuleEvaluator(host: host);
        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);
        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHiddenLiveSelectorRewindsTheWholeRuleFiring(bool liveEnd) {
        var section = WithPolicies();
        var context = TransformFixture.Context(section: section);
        var transform = liveEnd
            ? (StateTransform)new StateTransform.Transfer(From: "deck", To: "$zones[(rankValue)]", Selector: ZoneSelector.First)
            : new StateTransform.WriteSet(Row: "target", Set: "scores", SetKey: "$cell:pointer:$value", Value: 1L);
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(
            Name: TransformFixture.Name(value: "leak"), Zones: ["hand"], Mode: ActionTriggerMode.Level,
            Effects: [EvaluatorFixture.Set(row: "mask", value: 7m), new ActionEffect.TransformState(Transform: transform)])]);
        var host = TransformFixture.Host(context: context, section: section);
        var arena = host.Arena;
        var mask = TransformFixture.Ordinal(context: context, name: "mask");
        var slot = arena.Keys.Intern(name: StateRow.SlotKey);
        var before = arena.Read(rowOrdinal: mask, key: slot);
        Assert.True(arena.TryWriteVisibility(rowOrdinal: TransformFixture.Ordinal(context: context, name: liveEnd ? "rankValue" : "pointer"), key: slot, visibility: Private));
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);

        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal(before, arena.Read(rowOrdinal: mask, key: slot));
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
        Assert.Equal("", TransformFixture.Listing(arena: arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "hand")));
        Assert.Equal("0,0,0,0", TransformFixture.BoardListing(arena: arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "target")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LiveRestrictionsAreCheckedBeforeAnyTransformWrites(bool zone, bool confined) {
        var section = confined ? WithPolicies("deck") : WithPolicies();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(RuleCompiler.TryResolveTransform(new StateTransform.Sort(Row: "deck", By: [new SortKey(Row: "rank")]), context, out var transform, out var reason), reason);
        var deck = TransformFixture.Ordinal(context: context, name: "deck");
        var restricted = TransformFixture.Ordinal(context: context, name: zone ? "deck" : "rank");
        Assert.True(host.Arena.TryWriteVisibility(rowOrdinal: restricted, key: host.Arena.Keys.Intern(name: TransformFixture.Name(value: "a")), visibility: Private));
        var before = TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck);

        var applied = host.TryTransform(binding: ArenaTransformBinding.None, transform: transform, moved: out var moved, refusal: out var refusal);

        Assert.Equal(confined, applied);
        Assert.Equal(confined, moved);
        if (confined) {
            Assert.Equal("b=2,c=3,a=1", TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck));
        } else {
            Assert.Contains("TransformWidensAudience", refusal.Reason);
            Assert.Equal(before, TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenRuleLocalsCannotControlAPublicTransform(bool runtime) {
        var section = runtime ? WithPolicies() : WithPolicies("rankValue");
        var context = TransformFixture.Context(section: section);
        Assert.True(ExpressionSpelling.TryParse(text: "1 / (rankValue + 1)", program: out var program, error: out var error), error);
        var rule = new Rule(
            Name: TransformFixture.Name(value: "localControl"),
            Mode: ActionTriggerMode.Level,
            Locals: [new RuleLocal(Name: TransformFixture.Name(value: "probe"), Expression: program!)],
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Sort(Row: "deck", By: [new SortKey(Row: "rank")]))]);
        if (!runtime) {
            var exception = Assert.Throws<RuleException>(() => RuleCompiler.CompileAll(context: context, rules: [rule]));
            Assert.Contains("TransformWidensAudience", exception.Message);
            return;
        }
        var rules = RuleCompiler.CompileAll(context: context, rules: [rule]);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(host.Arena.TryWriteVisibility(
            rowOrdinal: TransformFixture.Ordinal(context: context, name: "rankValue"),
            key: host.Arena.Keys.Intern(name: StateRow.SlotKey),
            visibility: Private));
        var evaluator = new RuleEvaluator(host: host);
        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);
        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
    }

    [Fact]
    public void PushRayDisclosureChecksThePoolsOwnBackingRows() {
        var cellField = TransformFixture.Name(value: "cell");
        var kindField = TransformFixture.Name(value: "kind");
        var poolName = TransformFixture.Name(value: "pieces");
        var section = new StateSection(
            Lattices: [new LatticeTopology.Grid(Name: TransformFixture.Name(value: "map"), Origin: new Puck.Assets.Documents.DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 4, Depth: 1)],
            Rows: [new StateRow(Name: TransformFixture.Name(value: "secretSlot"), Kind: CellKind.Int, Visibility: Private, Cells: [TransformFixture.Cell(key: StateRow.SlotKey.Value, value: 0L)])],
            Records: [new StateRecord(Name: TransformFixture.Name(value: "Piece"), Fields: [
                new StatePoolField(Name: cellField, Kind: CellKind.Int, Min: 0, Max: long.MaxValue),
                new StatePoolField(Name: kindField, Kind: CellKind.Int),
            ])],
            Pools: [new StatePool(Name: poolName, Record: TransformFixture.Name(value: "Piece"), Capacity: 4, Initial: [
                new StatePoolSeed(Slot: 0, Values: [new StatePoolValue(Field: cellField, Value: CellValue.Int(value: 0L)), new StatePoolValue(Field: kindField, Value: CellValue.Int(value: 1L))]),
                new StatePoolValue[] { new(Field: cellField, Value: CellValue.Int(value: 1L)), new(Field: kindField, Value: CellValue.Int(value: 1L)) } is var second ? new StatePoolSeed(Slot: 1, Values: second) : default!,
            ])]
        );
        var catalog = StateCatalog.Compile(section: section);
        var runRow = new PatternRow(Name: TransformFixture.Name(value: "pushable"), Kind: CellKind.Int,
            Symbols: [new PatternSymbol(Name: TransformFixture.Name(value: "push"), Min: 1, Max: 1), new PatternSymbol(Name: TransformFixture.Name(value: "empty"), Min: 0, Max: 0)],
            Pattern: new PatternNode.Sequence(Items: [new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "push")), new PatternNode.Symbol(Name: "empty")]));
        var pushRow = new PatternRow(Name: TransformFixture.Name(value: "push"), Kind: CellKind.Int,
            Symbols: [new PatternSymbol(Name: TransformFixture.Name(value: "push"), Min: 1, Max: 1)],
            Pattern: new PatternNode.Symbol(Name: "push"));
        var stopRow = new PatternRow(Name: TransformFixture.Name(value: "stop"), Kind: CellKind.Int,
            Symbols: [new PatternSymbol(Name: TransformFixture.Name(value: "stop"), Min: 2, Max: 2)],
            Pattern: new PatternNode.Symbol(Name: "stop"));
        var context = new RuleCompileContext(section: section, catalog: catalog, tables: null, patterns: [runRow, pushRow, stopRow], generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: TransformFixture.Name(value: "push"), Effects: [new ActionEffect.ForEachPool(
            Pool: poolName.Value,
            Binding: TransformFixture.Name(value: "mover"),
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.PushRay(
                Pool: poolName, Cell: cellField, Value: kindField,
                From: StateChannelRef.OfBindingField(binding: "mover", field: "cell"),
                Topology: TransformFixture.Name(value: "map"), Direction: TransformFixture.Name(value: "E"),
                Pattern: "pushable", PushPattern: "push", StopPattern: "stop", Empty: 0))]
        )]));
        var host = new ArenaEffectHost(arena: new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin));
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: [compiled], stepTicks: 1UL);

        Assert.Empty(evaluator.Diagnostics());
        Assert.True(catalog.TryGetPool(name: poolName, pool: out var pool));
        Assert.True(host.Arena.TryPoolKey(slot: 1, key: out var slotKey));
        Assert.Equal(CellValue.Int(value: 3L), host.Arena.Read(rowOrdinal: pool!.Fields[0].RowOrdinal, key: slotKey));
    }

    // A catalog is reused across a redeclaration that changes only a row's policy (StateCatalog.MatchesShape does not
    // compare it), so the zones the arena's check walks come from the arena's own declarations, not the catalog's.
    [Fact]
    public void AZoneRedeclaredPrivateUnderAReusedCatalogWithholdsItsTokensFromAPublicTransfer() {
        var fixture = TransformFixture.Section();
        var hand = fixture.Rows!.Single(row => row.Name.Value == "hand");
        var rows = fixture.Rows!.Select(row => (row == hand) ? row with { Cells = [TransformFixture.Cell(key: "a", value: 1L)] } : row)
            .Append(hand with { Name = TransformFixture.Name(value: "discard") }).ToArray();
        var declared = fixture with { Rows = rows };
        var redeclared = declared with { Rows = rows.Select(row => (row.Name.Value == "hand") ? row with { Visibility = Private } : row).ToArray() };
        var catalog = StateCatalog.Compile(section: declared);
        Assert.True(catalog.MatchesShape(section: redeclared));
        var context = new RuleCompileContext(section: redeclared, catalog: catalog, tables: null, patterns: TransformFixture.Patterns(),
            generators: TransformFixture.Generators(), simulationRateHz: 30, vocabulary: RuleVocabulary.Core);
        var host = TransformFixture.Host(context: context, section: redeclared);
        Assert.True(RuleCompiler.TryResolveTransform(new StateTransform.Transfer(From: "deck", To: "discard", Selector: ZoneSelector.First), context, out var transform, out var reason), reason);

        var applied = host.TryTransform(binding: ArenaTransformBinding.None, transform: transform, moved: out var moved, refusal: out var refusal);

        Assert.False(applied);
        Assert.False(moved);
        Assert.Contains("TransformWidensAudience", refusal.Reason);
        Assert.Equal("a=1,b=2,c=3", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "deck")));
        Assert.Equal("", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "discard")));
    }

    // A firing checks the one row each live end selects, so a gate's read of another member of the end's family is a
    // read the check makes itself: here the binding moves its own pile (pile[$each]) on the face-down top card of the
    // pile its next cell names, read through the rule's zone table over the same members.
    [Fact]
    public void AGateOnAFamilyMemberTheBindingDidNotSelectCannotChooseAPublicTransfer() {
        static StateRow Pile(string name, bool cellPolicies, params string[] tokens) => new(
            Name: TransformFixture.Name(value: name), Kind: CellKind.Int, Capacity: 4, CellVisibility: cellPolicies,
            Domain: new StateDomain.KeysOf(Ordered: true, Row: TransformFixture.Name(value: "tokens")),
            Cells: [.. tokens.Select(token => TransformFixture.Cell(key: token, value: 0L))]);
        var section = new StateSection(
            Families: [new StateFamily(Name: TransformFixture.Name(value: "pile"), Size: 2)],
            Rows: [
                new StateRow(Name: TransformFixture.Name(value: "tokens"), Kind: CellKind.Int, Capacity: 4,
                    Cells: [.. Enumerable.Range(0, 4).Select(token => TransformFixture.Cell(key: $"{token}", value: token))]),
                Pile(name: "pile0", cellPolicies: true, "0"),
                Pile(name: "pile1", cellPolicies: true, "2"),
                Pile(name: "discard", cellPolicies: false),
                new StateRow(Name: TransformFixture.Name(value: "tables"), Kind: CellKind.Int, Capacity: 2, Cells: [TransformFixture.Cell(key: "0", value: 0L)]),
                new StateRow(Name: TransformFixture.Name(value: "next"), Kind: CellKind.Int, Capacity: 2, Cells: [TransformFixture.Cell(key: "0", value: 1L)]),
                // A public face per token key, kept off the token domain so no zone restricts it: only the pile it is
                // read through withholds the card.
                new StateRow(Name: TransformFixture.Name(value: "faces"), Kind: CellKind.Int, Capacity: 4,
                    Cells: [.. Enumerable.Range(0, 4).Select(token => TransformFixture.Cell(key: $"{token}", value: 5L + token))]),
            ]);
        var context = TransformFixture.Context(section: section);
        var rules = RuleCompiler.CompileAll(context: context, rules: [new Rule(
            Name: TransformFixture.Name(value: "peek"), ForEach: "tables", Mode: ActionTriggerMode.Level, Zones: ["pile0", "pile1"],
            Gate: new ActionPredicate.CompareValue(Comparison: ExpressionOp.Equal, Kind: CellKind.Int,
                Left: RulesFixture.Program(text: "faces[zone($zones[next[$each]], last)]"), Right: RulesFixture.Program(text: "7")),
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Transfer(From: "pile[$each]", To: "discard", Selector: ZoneSelector.First))])]);
        var host = TransformFixture.Host(context: context, section: section);
        Assert.True(host.Arena.TryWriteVisibility(rowOrdinal: TransformFixture.Ordinal(context: context, name: "pile1"),
            key: host.Arena.Keys.Intern(name: TransformFixture.Name(value: "2")), visibility: Private));
        var evaluator = new RuleEvaluator(host: host);

        _ = evaluator.Evaluate(latch: new RuleLatch(), rules: rules, stepTicks: 1UL);

        Assert.Contains(evaluator.Diagnostics(), diagnostic => diagnostic.ToString()!.Contains("TransformWidensAudience", StringComparison.Ordinal));
        Assert.Equal("0=0", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "pile0")));
        Assert.Equal("", TransformFixture.Listing(arena: host.Arena, rowOrdinal: TransformFixture.Ordinal(context: context, name: "discard")));
    }

    [Fact]
    public void ParenthesizedVectorCellOperandListsUnderlyingRowAsReadSubject() {
        var remember = new StateTransform.Remember(Into: "dest", Key: "k", From: "(`pack$secretVec`[a])", UnlessWithin: "0.5");
        Assert.Contains(new StateSubject(Name: "pack$secretVec", Access: StateAccess.Read), remember.Subjects());
    }
}
