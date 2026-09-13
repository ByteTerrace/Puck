# Puck.Launcher

Puck.Launcher supplies the shared application loop and launcher services. It
connects fixed-step execution, console input and output, presentation pacing,
backend selection, and release updates to the host's dependency-injection
container. A composition root chooses the services it needs.

## Usage

`LauncherServiceRegistration` contains the shared registration entry points.
Platform presentation registration lives in
[Puck.Launcher.Windows](../Puck.Launcher.Windows/README.md) and
[Puck.Launcher.Linux](../Puck.Launcher.Linux/README.md). Keeping that registration
separate lets the composition root choose a platform without making this
library depend on a GPU backend.

`Release/` owns launcher-side release verification, staging, and update
application. The [launcher stub](../Puck.Launcher.Stub/README.md) selects the
installed version to start and handles the health-based rollback decision.
The [release guide](../../docs/development/ci.md) owns the shared publishing
workflow and its environment requirements.

## Verification

The [launcher tests](../../tests/Puck.Launcher.Tests/README.md) exercise host
loop controls and the release/update path. A library test does not replace
running the selected platform presentation path on its intended hardware.

## Documentation

📚 [Engine architecture](../../docs/architecture/README.md) · 🛠️ [Development](../../docs/development/README.md)
