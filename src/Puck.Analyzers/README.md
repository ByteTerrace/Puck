# Puck.Analyzers

Puck.Analyzers provides the Roslyn analyzers and code fixes used when building
the repository. It checks verified-code declarations, source-file length
limits, comment smells, strict-enum usage, unmanaged function-pointer calls, and
environment reads. It is a
compiler extension, not an engine runtime dependency or a published package.

## Usage

The shared [build properties](../../Directory.Build.props) attach the analyzer
to repository projects. The analyzer project itself is excluded to avoid a
build cycle. Its Roslyn dependency must remain compatible with the compiler
selected by the repository SDK; the project file documents that constraint.

The verified-code check reads the repository's
[verification manifest](../../VerifiedCode.json). The file-length and
comment-smell checks read the repository's two ratchet ledgers, the
[file-length ledger](../../FileLengths.json) and the
[comment-smell ledger](../../CommentSmells.json). In a ratchet ledger a recorded
per-file count may only fall. Both ledgers use one ledger type and one analyzer
base, and `puck lengths` and `puck comment-smells` regenerate them. Update each
file through its owning workflow rather than suppressing a diagnostic to hide a
changed declaration.
The [CLI reference](../Puck.Cli/README.md) documents the related inspection tools.

ENV001 refuses any read of the process environment whose variable is not named,
with its reason and the assemblies that may read it, in `EnvironmentReadAllowlist`,
and any read whose name is not a compile-time constant. Nothing in Puck is
switched by an environment variable; the
[configuration guide](../../docs/development/contributing.md#configuration-and-diagnostics)
names what replaces one.

INTEROP001 refuses a call through an unmanaged function pointer whose signature
mentions a type parameter anywhere but behind a pointer, because that call throws
`MarshalDirectiveException` at run time. The
[code conventions](../../docs/development/contributing.md#code-and-documentation-conventions)
state the rule.

## Verification

The [analyzer tests](../../tests/Puck.Analyzers.Tests/README.md) compile source
fixtures in memory and inspect diagnostics and code fixes. They exercise the
compiler integration separately from the production projects that load it.

## Documentation

📚 [Development](../../docs/development/README.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
