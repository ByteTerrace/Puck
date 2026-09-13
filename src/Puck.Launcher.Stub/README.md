# Puck.Launcher.Stub

Puck.Launcher.Stub is the small executable that starts an installed application
version. It reads the installation's current and last-good pointers, records
launch attempts, and uses the health policy to decide whether to retry or
roll back. It stays outside the application's self-update mechanism.

## Usage

The installation root is the stub executable's own directory. Configuration
beside the stub selects the application executable and allowed attempt count;
command-line arguments are forwarded to the selected application rather than
used to redirect the installation root.

`StubDecisionTable` contains the selection policy. A rollback cannot select a
last-good version whose state generation is older than the current version's;
that case keeps launching the current version to avoid an incompatible read.
`StubInstall` and `StubHealth` handle the installation pointers and attempt
records. See [Puck.Launcher](../Puck.Launcher/README.md) for update staging and
the application's side of the release workflow.

## Verification

The [launcher tests](../../tests/Puck.Launcher.Tests/README.md) exercise the
stub's decision table alongside the release chain. Tests of that policy do
not demonstrate recovery from damage to the stub executable itself.

## Documentation

📚 [CI and releases](../../docs/development/ci.md) · 🛠️ [Development](../../docs/development/README.md)
