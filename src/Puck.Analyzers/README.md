# Puck.Analyzers

Puck.Analyzers provides the Roslyn analyzers and code fixes used when building
the repository. It checks verified-code declarations, source-file length
limits, and strict-enum usage. It is a compiler extension, not an engine
runtime dependency or a published package.

## Usage

The shared [build properties](../../Directory.Build.props) attach the analyzer
to repository projects. The analyzer project itself is excluded to avoid a
build cycle. Its Roslyn dependency must remain compatible with the compiler
selected by the repository SDK; the project file documents that constraint.

The verified-code and file-length checks read the repository's
[verification manifest](../../VerifiedCode.json) and
[file-length ledger](../../FileLengths.json). Update those through their owning
workflow rather than suppressing a diagnostic to hide a changed declaration.
The [CLI reference](../Puck.Cli/README.md) documents the related inspection tools.

## Verification

The [analyzer tests](../../tests/Puck.Analyzers.Tests/README.md) compile source
fixtures in memory and inspect diagnostics and code fixes. They exercise the
compiler integration separately from the production projects that load it.

## Documentation

📚 [Development](../../docs/development/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
