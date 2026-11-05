# HGB startup firmware

[`BootRomBuilder`](BootRomBuilder.cs) emits and calibrates the original Puck boot
program for each hardware revision. `HgbFirmware.GetImage` derives it in process
on demand, through the shared `DerivedBytes` cache keyed by the deriving code and
revision. Missing or corrupt products derive again; hits, misses and derivation
milliseconds are counted on stderr. Callers receive private bytes.

Only source and the [firmware notices](LICENSE) are committed. The generated
images use the adjacent Apache-2.0 or MIT licenses. Export images when needed with
`puck firmware hgb --output <dir>`. Run the HGB Post `bundled-firmware`,
`firmware-reconfigure` and `boot-rom-handoff` stages after changing the generator.
The [Forge guide](../../../docs/emulation/hgb/forge.md#the-authored-boot-roms)
owns presentation, header checks, hardware handoff and the limits of the
independent conformance evidence.

## Documentation

📚 [Machine emulation manual](../../../docs/emulation/README.md) · 🛠️ [Contributing to Puck](../../../docs/development/contributing.md)
