# Puck.GamingBricks.Transpiler

Authors `puck.cartridge.v1` documents in the Puck DSL. The language itself — parser, syntax tree, diagnostics,
formatter, units — is [`Puck.Transpiler`](../Puck.Transpiler/README.md); this project is the vocabulary that knows
what a cartridge's sections mean, the peer of `Puck.World.Transpiler`.

Both artifacts are committed: the `.puck` source and the `.cartridge.json` it generates. The regeneration gate in
`tests/Puck.GamingBricks.Transpiler.Tests` compiles the committed source and compares it byte for byte against the
committed document, so the two can never drift apart quietly.

## Round trip

```bash
puck decompile src/Puck.World/Assets/cartridges/tetris.cgb.cartridge.json --output tetris.cgb.puck
puck compile tetris.cgb.puck --output tetris.cgb.cartridge.json --validate
```

`--validate` runs the forge's own document validation (`CartridgeDocuments.Validate`) over the lowered JSON:
lowering only proves the source was well formed, never that the cartridge is legal.

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

variables: [
    { name: "ph", initial: 14 }
]
```

The language's array builtins run at compile time, which is what keeps a cartridge's big data sections short:

```
arrays [
    { name: "field", initial: map(range(0, 180), i => 0) }
    { name: "speeds", initial: map(range(0, 21), level => 48 - (level * 2)) }
]
```

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

An operand is one of three things and nothing else — a literal byte, a variable, or `array[index]` — because that
is all the hardware evaluates. A `let` name resolves to its bound value at compile time, so it is never mistaken
for a machine variable.

A gate is a conjunction: conditions are tested in order and the first failure stops the rule, so `and` flattens
into the condition list and `or`/`not` are refused by name.

## What this project does not do

It compiles no code. A lowered document goes to `HgbCartridgeCompiler` or `AgbCartridgeCompiler` in the per-brick
forges, which is where SM83 and Thumb live.
