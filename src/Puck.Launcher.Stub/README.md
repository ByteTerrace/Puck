# Puck.Launcher.Stub

Puck.Launcher.Stub is the small executable that starts an installed application
version. It asks the install's release record which version to launch, counts
the launch, and starts it. It stays outside the application's self-update
mechanism. Its only Puck dependency is the BCL-only `Puck.Confinement` leaf,
which also holds the release record and supplies `AtomicFile` to the updater.

## Usage

The installation root is the stub executable's own directory. Configuration
beside the stub (`stub.json`) selects the application executable and the
startup attempts a payload may use; command-line arguments are forwarded to the
selected application rather than used to redirect the installation root.

The stub begins an `InstallTransaction` (waiting up to `StubInstall.LeaseWait`
for `update.lock`) and calls `InstallTransaction.SelectLaunch`, its only policy
call. A current version whose payload has used its attempts is rejected by its
payload identity; a current version that is revoked, rejected, below the state
generation floor or without a valid install record is replaced by the most
recent proven install that is none of those, or the launch is refused by name
with exit code 2, naming every disqualification. Selection raises the floor to
the chosen version's generation, counts the launch under its payload and names
it current in one durable commit, before the child starts. The policy and its
failure schedules are in the [updater policy plan](../../docs/plans/updater-policy.md)
and the [launcher README](../Puck.Launcher/README.md#the-release-record).

Each successful process start writes its selected version, child PID and
executable path to stderr. Standard output remains the application's stream,
including when that stream is an MCP protocol connection.
Redirected input, output and error are forwarded as bytes; piped console scripts
and stdio protocol clients reach the selected child on Windows too.
For the operator MCP install, the selected application hosts a persistent
stdio front and starts each activated backend through this same stub. The
front retains earlier backends while they own Worlds, so application updates
do not end the client's connection or those World processes.

Under the transaction the stub holds `versions/<version>` open
(`StubInstall.HoldVersion`: a shared `flock` on Linux, a handle without
`FILE_SHARE_DELETE` on Windows) until the child exits, so the launcher's pruner
never removes the version the child runs from; the stub's exit releases it, a
crash included. It releases the lease before starting the child: the child's
registration waits for it, and re-checks the release record under it, so a
revocation committed after selection still stops that start. A pending install
intent for a rejected or revoked version is retired by the updater's next
recovery, which refuses it; the stub parses nothing the updater writes except
the release record.

## Verification

The [launcher tests](../../tests/Puck.Launcher.Tests/README.md) exercise
selection, rejection, rollback and refusal through `InstallTransaction`
alongside the release chain, and the `self-update` canary runs the real stub.
Tests of that policy do not demonstrate recovery from damage to the stub
executable itself.

## Documentation

📚 [CI and releases](../../docs/development/ci.md) · 🛠️ [Development](../../docs/development/README.md)
