# Puck.Transpiler

The compile-time language includes typed `module` declarations and `use` sites. Imports make module declarations
and their lexical `let` dependencies available without instantiating them; each use expands a fresh copy, and `use name as alias(...)` namespaces every
declared name and registered internal reference recursively, as the generated name `alias$name` the using scope reads as
`alias.name` (`DocumentScope.TryQualify`). Module parameters admit `Point`, `Angle`, `Asset`,
`Module`, `Pool`, `Row`, and `Gate`; a `Module exporting ...` parameter refuses an argument whose declaration does
not expose every required name. A `test` declaration parses an optional `with module(arguments)` subject in the
same spelling a `use` site carries, so what a test is about is read by the grammar rather than by a vocabulary's
own reader.

Puck.Transpiler provides the schema-neutral core of the `.puck` authoring
language: syntax trees, parsing, diagnostics, formatting, and value lowering.
Operand trees share the state expression parser and retain unresolved names, explicit grouping and interpolation
atoms. Vocabularies bind these nodes by position; member values convert structurally from the compile-time AST.
`PuckSyntaxRewriter.RewriteOperandSyntax` traverses their names and operators without editing source strings.
`Ast/SyntaxWalk` is the one enumeration of a node's children (`Children`) and the one search for the nodes a source
position stands in (`PathAt`); a reader that walks a tree without rebuilding it walks through there, while a rewrite
keeps its typed descent. `Ast/QualifiedName` is the one reading of a dotted name (`box.child.score`, `alias.name`,
`binding.field`, `Suit.hearts`): it splits and joins the segments, and spells a member-access chain in either the
document or the operand grammar as the name it reads. `Editing/LspJson` builds every Language Server Protocol completion item, position and range
the world and cartridge editor services answer with, and converts between an LSP position and a source offset; a
range that crosses line breaks ends on the line it ends on.
A compile reads files only through `Modules/CompileInputs`, which records inside a `Record` scope every fact the
compile learned — each file's bytes, each path it probed, absent or present, and each directory it listed, with
every file name spelled as the file system spells it — so a vocabulary's cache can prove a held compile still stands.

## Documentation

- [Puck DSL and document transpilation](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/dsl.md) — syntax, expressions, units, templates, formatting, and rewriting a source.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.World.Transpiler.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
