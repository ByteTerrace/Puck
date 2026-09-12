# Puck.GamingBricks.Transpiler

Authors `puck.cartridge.v1` documents in the Puck DSL. The language itself — parser, syntax tree, diagnostics,
formatter, units — is [`Puck.Transpiler`](../Puck.Transpiler/README.md); this project is the vocabulary that knows
what a cartridge's sections mean, the peer of `Puck.World.Transpiler`.

Both artifacts are committed: the source and the cartridge document it generates. The regeneration gate in
`tests/Puck.GamingBricks.Transpiler.Tests` compiles the committed source and compares it byte for byte against the
committed document, so the two can never drift apart quietly.

## Round trip

```bash
puck decompile src/Puck.World/Assets/cartridges/tetris.cgb.cartridge.json --output tetris.cgb.puck
puck compile tetris.cgb.puck --output tetris.cgb.cartridge.json --validate
```

`--validate` runs the forge's own document validation (`CartridgeDocuments.Validate`) over the lowered JSON:
lowering only proves the source was well formed, never that the cartridge is legal.

`puck lint` and the CLI language server select this vocabulary from `schema: "puck.cartridge.v1"`, use the same
forge validation, and locate errors at their authored rows or properties. Editor completions also follow the
schema. Without `--output`, compile writes `<source>.cartridge.json` for a cartridge and `<source>.world.json`
for a world. The regeneration gate covers every committed `.puck` cartridge source.

Runtime value fields accept ordinary expressions: `x: cursor + 1` and `map(row: cursor + 1, column: 0, tile: 0)`
have the same meaning as quoted operand spellings, including fields inside object literals and generated arrays.
Compile-time bindings are substituted before native compilation. Byte expressions admit literals in 0..255;
larger bare values are admitted only where a wide slot is supported. A wide read cannot be assigned to a byte
destination implicitly. Empty required sections, including `rules []`, survive decompilation.

Decompiling is one-way, like every Puck decompiler — `let` and `template` are a source-only idea and are gone from
the JSON. The guarantee runs the other way: source written by the decompiler compiles back to the document it was
read from, byte for byte.

## Grammar

Everything outside `rule` is the language's ordinary property and block syntax, lowered straight to JSON:

```
schema: "puck.cartridge.v1"
target: "cgb"
title: "TETRIS"

let wellFloor = 17

variables [
    { name: "ph", initial: 14 }
]
```

The language's collection builtins (`range`, `length`, `concat`, `map`, `filter`, `reduce`, `distinct`, `sort`,
`groupBy` — [`Puck.Transpiler`](../Puck.Transpiler/README.md#collection-builtins-and-lambdas)) run at compile time,
which is what keeps a cartridge's big data sections short:

```
arrays [
    { name: "field", initial: map(range(0, 180), i => 0) }
    { name: "speeds", initial: map(range(0, 21), level => 48 - (level * 2)) }
]
```

The named scalar functions (`squareRoot`, `remainder`, `greatestCommonDivisor`, and the rest of
[`Puck.State`'s shared vocabulary](../Puck.Transpiler/README.md#scalar-functions-come-from-puckstate-not-from-here))
fold the same way here as in any other document — an integer-domain one exactly, by delegating to
`ExpressionArithmetic`, everything else in `double`.

`for` is the core language's compile-time loop, and it expands here exactly as it does in a world document: a
`for` written where a `rule` or a section row belongs emits its body once per element, and the document carries the
rows it produced, never the loop. An interpolated header names each generated row apart:

```
for (level, index) in [4, 9, 16] {
    variable $"speed{index}" {
        initial: level
    }
}
```

The bound names are compile-time values, so a loop variable used as an operand lowers to a literal byte rather than
to a machine variable of the same name, and it shadows a `let` of that name for the length of one iteration.

`map(range(...), i => ...)` remains the spelling for a repeated VALUE inside one row (an array cell, a variable's
initial contents); `for` is the spelling for a repeated ROW. Neither reaches inside a rule body: a rule's steps are
parsed by the language's own effect dispatcher, which has no `for` production, and a rule's `repeat <count> as
<name> { }` is unrelated: the machine runs it at play time, with a compile-time count in 1..255.

A statement this vocabulary has no case for is refused rather than dropped, so a misspelled row keyword cannot
silently lose the row and everything nested in it.

A `rule` carries a gate and a body:

```
rule "soft-drop" {
    when key(down, held) and ph == 2 and dt >= wellFloor
    py += 1
    dt = 0
}
```

| Form | Lowers to |
|---|---|
| `a = b` `a += b` `a -= b` `a *= b` `a /= b` `a %= b` `a &= b` `a \|= b` `a ^= b` `a <<= b` `a >>= b` | `set` with operation `set`/`add`/`subtract`/`mul`/`div`/`mod`/`and`/`or`/`xor`/`shl`/`shr` |
| `if <gate> { … } else { … }`, `else if` chained | `if` with `when`/`then`/`else` |
| `repeat <count> as <name> { … }` | `repeat`; the count is a compile-time whole number in 1..255 |
| `break` | `break` |
| `map(row:, column:, tile:, palette:)`, `play(sound:)`, `fade(amount:, toward:)`, `blit`, `plot`, `blend`, `clock`, `save`, `load`, `stop` | the like-named step |
| `key(<button>, held\|pressed\|released)` | a `key` condition |
| `a == b` `!=` `<` `<=` `>` `>=` | a `compare` condition |

An operand is an expression — the engine's own `ValueExpression`, in the same infix spelling a world rule uses, so
`(score + bonus) * 2` and `field[cursor + 1]` are single operands and the compiler allocates whatever the machine
holds in flight. A cartridge admits the subset an eight-bit machine spends a few instructions on
(`CartridgeExpressions.Reads`); an operation the rule language evaluates and a cartridge does not is refused at
that name. A `let` or `for` name resolves to its bound value at compile time wherever it appears in the expression,
so it is never mistaken for a machine variable.

A gate composes through `and`, `or` and `not`, lowering to the engine's `all`, `any` and `not`. `key(...)` is a read
of the reserved `$key:<button>:<mode>` operand compared against one, which is why input sits under `or` and `not`
like anything else:

```
rule "steer" {
    when (key(left, held) or key(right, held)) and not paused == 1
    dx = $key:right:held - $key:left:held
}
```

## What this project does not do

It compiles no code. A lowered document goes to `HgbCartridgeCompiler` or `AgbCartridgeCompiler` in the per-brick
forges, which is where SM83 and Thumb live.
