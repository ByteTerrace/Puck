# Puck.Transpiler

The compile-time language includes typed `module` declarations and `use` sites. Imports make module declarations
and their lexical `let` dependencies available without instantiating them; each use expands a fresh copy, and `use name as alias(...)` namespaces every
declared name and registered internal reference recursively. Module parameters admit `Point`, `Angle`, `Asset`,
`Module`, `Pool`, `Row`, and `Gate`; a `Module exporting ...` parameter refuses an argument whose declaration does
not expose every required name.

Puck.Transpiler provides the schema-neutral core of the `.puck` authoring
language: syntax trees, parsing, diagnostics, formatting, and value lowering.
Operand trees share the state expression parser and retain unresolved names, explicit grouping and interpolation
atoms. Vocabularies bind these nodes by position; member values convert structurally from the compile-time AST.
`PuckSyntaxRewriter.RewriteOperandSyntax` traverses their names and operators without editing source strings.

## Documentation

- [Puck DSL and document transpilation](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/dsl.md) — syntax, expressions, units, templates, formatting, and rewriting a source.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.World.Transpiler.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
