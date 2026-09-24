# Quickstart: compile and run a rule

In this quickstart, you build a tiny shop with the state libraries: a player earns
a coin every tick and buys a card whenever they can afford one. Along the way you
declare state, compile rules, run them over an arena, and watch the engine roll
back a purchase that can't complete. It takes about ten minutes.

When you finish, you'll know the four objects every state program uses (the
section, the catalog, the arena, and the evaluator) and where to read next.

## Prerequisites

- The [.NET SDK](https://dotnet.microsoft.com/download) version pinned by the
  repository's `global.json`.
- A clone of the Puck repository. The [getting-started guide](../../getting-started.md)
  covers the setup.

## Create the project

1. Create a console project anywhere on your machine:

   ```bash
   dotnet new console -n Shop
   cd Shop
   ```

1. Add references to the two state projects. Replace `<puck>` with the path to
   your clone:

   ```bash
   dotnet add reference <puck>/src/Puck.State/Puck.State.csproj
   dotnet add reference <puck>/src/Puck.State.Rules/Puck.State.Rules.csproj
   ```

1. Open `Shop.csproj` and add `<PlatformTarget>x64</PlatformTarget>` to its
   `PropertyGroup`, so it matches the platform the Puck projects build for.

## Write the program

Replace the contents of `Program.cs` with the following code:

```csharp
using Puck.State;
using Puck.State.Rules;

// 1. Declare the state: two Int slots. A card collection can hold at most one card.
StateRow[] rows = [
    new StateRow(
        Name: CellName.Parse("coins"),
        Kind: CellKind.Int,
        Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(0L))]),
    new StateRow(
        Name: CellName.Parse("cards"),
        Kind: CellKind.Int,
        Max: 1,
        Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(0L))]),
];
var section = new StateSection(Rows: rows);
var catalog = StateCatalog.Compile(section: section);

// 2. Declare and compile the rules.
Rule[] authored = [
    new Rule(
        Name: CellName.Parse("earn"),
        Effects: [new ActionEffect.AddState(State: "coins", Value: 1m)]),
    new Rule(
        Name: CellName.Parse("buyCard"),
        Gate: new ActionPredicate.CompareState(
            State: "coins", Comparison: ExpressionOp.GreaterOrEqual, Value: 3m),
        Effects: [
            new ActionEffect.AddState(State: "coins", Value: -3m),
            new ActionEffect.AddState(State: "cards", Value: 1m),
        ]),
];
var context = new RuleCompileContext(
    section: section, catalog: catalog, tables: null, patterns: null,
    generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
var rules = RuleCompiler.CompileAll(rules: authored, context: context);

// 3. Create the arena and a host, then run seven ticks.
var arena = new StateArena(catalog: catalog, section: section, time: ArenaTime.Origin);
var host = new ArenaEffectHost(arena: arena);
var evaluator = new RuleEvaluator(host: host);
var latch = new RuleLatch();

catalog.TryResolve(lane: StateLane.Document, name: "coins", handle: out var coins);
catalog.TryResolve(lane: StateLane.Document, name: "cards", handle: out var cards);
catalog.Keys.TryResolve(name: StateRow.SlotKey, key: out var slot);

for (var tick = 1UL; tick <= 7UL; tick++)
{
    host.Advance(tick: tick, engineTick: tick);
    evaluator.Evaluate(rules: rules, latch: latch, stepTicks: 1UL);
    arena.TryRead(rowOrdinal: coins.Ordinal, key: slot, value: out var c);
    arena.TryRead(rowOrdinal: cards.Ordinal, key: slot, value: out var d);
    Console.WriteLine($"tick {tick}: coins={c.AsInt} cards={d.AsInt}");
}

// 4. Report the refusals the evaluator counted.
foreach (var diagnostic in evaluator.Diagnostics())
{
    Console.WriteLine($"{diagnostic.Rule}: {diagnostic.Refusal} x{diagnostic.Count} ({diagnostic.Detail})");
}
```

## Run it

```bash
dotnet run -c Release
```

You should see this output:

```text
tick 1: coins=1 cards=0
tick 2: coins=2 cards=0
tick 3: coins=0 cards=1
tick 4: coins=1 cards=1
tick 5: coins=2 cards=1
tick 6: coins=3 cards=1
tick 7: coins=4 cards=1
buyCard: MutationRejected x2 (row 'cards' cell '$value' would leave the row's declared envelope)
```

## Understand what happened

The program has four parts, and each one maps to an object you'll meet
throughout the manual.

1. **Declare the state.** A `StateRow` names a row of values and says what kind
   of value it holds. Both rows here are **slots**, rows that hold a single value
   under the reserved key `StateRow.SlotKey`. The `cards` row declares `Max: 1`,
   so any write that would push it above one is refused. The `StateSection`
   collects the rows, and `StateCatalog.Compile` turns their names into
   **handles**, compact addresses that the compiler and the arena use instead of
   strings.
1. **Compile the rules.** A `Rule` has an optional **gate** (the condition that
   must hold) and a list of **effects** (the changes it attempts). `earn` has no
   gate, so it fires on every evaluation. `buyCard` fires when `coins >= 3`.
   `RuleCompiler.CompileAll` resolves every name against the catalog and checks
   that each operation makes sense for the row it touches.
1. **Run the rules over an arena.** The `StateArena` stores every value. The
   `ArenaEffectHost` is the smallest possible host: it serves reads and writes
   against the arena and nothing else. Each tick, `host.Advance` moves the clock
   and `RuleEvaluator.Evaluate` runs the rules in order. The `RuleLatch`
   remembers what each gate did last time, which matters for rules that fire
   only when their gate first opens.
1. **Read the results.** `arena.TryRead` returns the current value as a
   `CellValue`, and `evaluator.Diagnostics()` lists every refusal the evaluator
   counted.

Now look at the output tick by tick:

- **Ticks 1 and 2.** Only `earn` fires. The `buyCard` gate is closed.
- **Tick 3.** `earn` raises `coins` to 3, and because rules run in order and see
  each other's writes, `buyCard` sees the 3 in the same tick. Its gate opens, it
  spends three coins, and adds a card.
- **Ticks 6 and 7.** The gate opens again, but `cards` is already at its maximum.
  The second effect is refused, so the engine rewinds the whole firing and the
  coins are never spent. That's why `coins` keeps climbing and the diagnostic
  line counts two refused firings.

> [!IMPORTANT]
> A rule firing is atomic. Every effect in one firing lands together, or none of
> them do. You don't need a special construct to get that behavior; it's how
> every rule runs. [Rules and firing](rules.md) explains the details, including
> savepoints and effects that leave the arena.

## Try a variation

Add `Mode: ActionTriggerMode.Edge,` to the `buyCard` rule, after its `Gate`, and
run the program again. The diagnostic now reads `x1`. An **Edge** rule fires
only when its gate changes from closed to open, so at tick 7, when the gate is
still open from tick 6, the rule doesn't try again. The default mode, **Level**,
fires on every evaluation where the gate holds.

## Author the same rules in a world

When you build a world for Puck.World, you don't write C#. You write the same
state and rules in a `.puck` source file, and the world host compiles and runs
them for you:

```puck
schema: "puck.world.definition.v1"
documentId: "shop-quickstart-v1"

state {
  world {
    slot coins = 0
    slot cards = 0 bounds(..1)
  }
}

rule earn {
  coins = coins + 1
}

rule buyCard {
  when coins >= 3
  coins = coins - 3
  cards = cards + 1
}
```

Compile it to the canonical JSON document with the Puck CLI:

```bash
puck compile shop.world.puck -o shop.world.json --validate
```

The output holds the same rows (`coins`, and `cards` with `"max": 1`) and the
same two rules, with each assignment expressed as a `setState` effect over a
compiled expression. [State in Puck.World](worlds.md) explains how a world hosts
this state, and the [`.puck` language reference](../dsl.md) covers the syntax.

## Clean up

Delete the `Shop` folder when you're done. The quickstart doesn't change
anything in your Puck clone apart from the build outputs of the two referenced
projects.

## Next steps

- [State and rules overview](../state.md): see how the pieces you just used fit
  together, and find the article for each part of the system.
- [Rows, cells, and values](data-model.md): learn every kind of row you can
  declare, from slots to ordered piles and boards.
- [Rules and firing](rules.md): learn gates, modes, iteration, transactions, and
  how to diagnose a rule that doesn't fire.
