# Bundled HGB firmware

These original Puck boot ROM startup images are generated from
[`BootRomBuilder`](../../Puck.HumbleGamingBrick.Forge/BootRomBuilder.cs), one
image per `ConsoleModel`. They are runtime resources, not external downloads.
The `.bin` images in this directory are distributed under the adjacent
[Apache-2.0 or MIT licenses](LICENSE); this does not change the license of the emulator or
the Forge tooling.

Regenerate or verify them with the documented
[`puck firmware hgb` command](../../Puck.Cli/README.md).
Never hand-edit an image. A regeneration changes firmware identity, so review
the source change and run the HGB Forge tests and Post firmware/handoff gates.
The [Forge guide](../../Puck.HumbleGamingBrick.Forge/README.md#the-authored-boot-roms)
owns presentation, header checks, hardware handoff and the limits of the
independent conformance evidence.
