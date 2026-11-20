# Puck.Launcher.Setup

Puck.Launcher.Setup is the installer for a player build. It installs the newest
release of a release channel into an install root, or updates the root to it,
and lays the stub that starts the installed version beside the install's
release record. It carries no installation logic of its own: it runs the
launcher's ordinary update chain (`UpdateService` with the attestation
verifier, the content-addressed stager and the applier) against the root.

## Usage

```text
Puck.Launcher.Setup [<channel address>] [--to <install root>] [--channel <name>] [--trust <root key fingerprint>] [--app <id>]
```

The channel address is a release tree's root, an `https` URL or a directory,
laid out as `puck publish` and `puck channel push` write it. A first install
reads the root key the channel publishes beside its manifest
(`<address>/<channel>/trust.json`), checks that the key's fingerprint is the
one the file names, and pins it by writing the install root's subscription
(`ChannelSubscription`, in the file `ChannelSubscription.FileName` names): the address, the channel and the
anchor. `--trust` requires a fingerprint instead of accepting the published
one. Every later run reads the subscription and verifies each release against
the pinned anchor, so a different address or channel for the same root is
refused; another channel goes into another directory.

Without `--to`, the root is the installer's own directory when that directory
already follows a channel (the copy the installer lays in the root), and the
per-user Puck `player` directory otherwise. After the update chain has made a
version current, the installer copies the version's `launcher/` files (the
stub and the installer, each one self-contained file) into the root and writes
the stub's configuration (`StubConfiguration`) to start `Puck.World`. It then
prints the command that starts the install. Exit 0 means the root holds a
current version, 1 that nothing could be installed, and 2 a usage error.

A World started by the stub from that root updates from the same subscription
(`update.check`, `update.apply`), and the stub starts the new version at its
next launch.

`puck world player` publishes this project self-contained into a player build's
`launcher/`; `puck publish --bootstrap launcher` also writes it to the channel's
stable `<channel>/<rid>/` path, from which a first install downloads it. The
[CI guide](../../docs/development/ci.md#the-player-channel) and
[the player channel decision](../../docs/decisions/runtime-and-delivery.md#the-player-channel)
describe the channel this installs from.

## Documentation

📚 [Launcher](../Puck.Launcher/README.md) · 🛠️ [Stub](../Puck.Launcher.Stub/README.md)
