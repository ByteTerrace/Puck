# Debug world device row

The debug world shows one of each gaming device the World hosts, on a row of
labelled plinths at z = -6 west of the spawn, 3 m apart and facing +Z. Each
plinth carries the device's own name.
[`devices.puck`](devices.puck) holds the row; `debug.puck` imports it whole
(`import "devices"`). Each device is a named `machines` row whose screen shows
it running, and each lists the folders its swappable content comes from in
`contentFolders`: its shipped folders, then the owner's local folder under the
git-ignored `roms/` at the repository root.

| x | Device | Instance | Engine and model | Screen | Boots | `contentFolders` |
|---|---|---|---|---|---|---|
| -10 | Humble Gaming Brick | `humble-gaming-brick` | `gaming-brick`, `cgb` | 0 | `tetromino.cgb` | `cartridges/cgb`, `cartridges/dmg`, `roms/hgb` |
| -13 | Advanced Gaming Brick | `advanced-gaming-brick` | `advanced-gaming-brick` | 1 | `pip.agb` | `cartridges/agb`, `roms/agb` |
| -16 | Humble Gaming Deck | `humble-gaming-deck` | `humble-gaming-deck` | 2 | `colour-bars.nes` | `cartridges/deck`, `roms/hgd` |

A `roms/` folder is spelled `../../roms/<device>` beside this file, so it
resolves when the world boots from its package in the checkout
(`--world worlds/debug`, the recipe in [`debug.md`](debug.md)). Wherever it does
not exist (CI, the Azure deploy, a compiled copy of the package, any machine
without the owner's ROMs)
`machine.content` lists it `present=false` and the world boots and validates the
same: every device boots a shipped cartridge. The files in it are the owner's
and never enter the repository, a build output or a package.

The Humble Gaming Brick runs the Color model, which plays the forge's `cgb` and
`dmg` cartridges alike; `device.model` switches a running brick to another
revision (`dmg`, `mgb`, `sgb`, `agb` and the rest). A Color-only cartridge on
a `dmg` model holds on the firmware logo, since its speed switch parks a
monochrome processor. The Humble Gaming Deck is the NTSC NES console, a set
above it showing the cartridge in its slot; its screen route names the
controller port an engaged seat plays (`controls`, port one), since the console
has two.

The shipped cartridges sit one folder per forge target, `cartridges/cgb/`,
`cartridges/dmg/` and `cartridges/agb/`, each source beside its compiled
document. A device lists every file in its folders, each mountable or refused
with the reason: a brick mounts the `.cartridge.json` documents and any raw
image whose header it runs (the header checksum holds and the mapper is one it
implements), whatever the file's extension, so a cartridge's `.puck` source is
listed as refused; the advanced brick mounts an image whose header holds the
fixed byte and complement checksum; the deck mounts an iNES image of a board it
implements (NROM, MMC1 or MMC3). `test-card.agb` is the Advanced
Gaming Brick's second cartridge: eight colour bars drifting one pixel a frame,
so a picture that stops drifting is a machine that stopped. The `dmg` folder
holds `tetromino.dmg`, the game in four shades, and `test-card.dmg`, four shade
bars drifting the same way (`machine.swap humble-gaming-brick test-card`). The deck's two cartridges in `cartridges/deck/` are built by
`puck cartridges deck` rather than compiled from a source:
`colour-bars.nes` shows eight bars that A, B and select tint and start turns
grey, and `moving-sprite.nes` bounces a ball the d-pad steers
([the Deck's hosting page](../../docs/emulation/hgd/hosting.md#test-cartridges)).

## Devices

Every line runs at the console; `<instance>` is a name from the table.

| To | Console line |
|---|---|
| List what a device can swap to, and what it runs now | `machine.content <instance>` |
| Swap to another cartridge in its folders | `machine.swap <instance> <file>`, `machine.swap <instance> <folder>/<file>` when two folders hold the name, or `machine.swap <instance> <stem>` (`machine.swap humble-gaming-brick light-gun`) |
| Read the running content back | `screen.state <screen>` (`cartridge <path> hash … rom …`, or `no cartridge`) or `machine.content <instance>` (`mounted=`) |
| Read every device's generation and status | `machine.state` |
| Reset a device on its current content | `machine.operation <instance> <generation> machine.reset {"schema":"puck.<engine>.machine-reset.v1"}` |
| Eject the content, leaving the device on with nothing in it | `machine.operation <instance> <generation> content.eject {"schema":"puck.<engine>.content-eject.v1"}`; `machine.swap` puts one back |
| Change a brick's revision live | `machine.operation <instance> <generation> device.model {"schema":"puck.gaming-brick.device-model-set.v1","model":"sgb"}` |
| Stand at a device, facing it | `body.pose <x> 0 -4.5 0 0 0` |
| Engage it (within 3 m), playing it with the Moth's controls | the context button: E, or X on a pad (`player.signal keyboard.e press`, then `release`); or `body.engage <screen>` |
| Leave it | the context button again; or `body.disengage` |
| Detach the screen while the machine keeps running, then show it again | `screen.eject <screen>`, then `screen.source <screen> row` |
| Put every device back on its authored cartridge (and pick up an edit to `devices.puck`) | `world.reload` |
| Frame the whole row | `view.override camera look -13 2.2 -1.5 -13 0.6 -6`, then `view.override camera auto` |
| Frame one device | `view.override camera look <x> 1.1 -4.4 <x> 0.75 -6` |

`<engine>` in a schema is the instance's engine: `gaming-brick`,
`advanced-gaming-brick` or `humble-gaming-deck`. `machine.state <instance>`
prints the generation an operation names; a swap, reset or eject replaces the
machine under a new generation.

## Engaging a device

Walk up to a device and press the context button, E on a keyboard or X on a
pad (the Moth's `interact` channel, which every screen route here names as its
`engageChannel`). A press within the route's 3 m engages the first device in
document order that is free; no prompt shows, since the controller bar is the
debug world's only on-screen UI. Engaging captures the seat, so the
Moth stands still, and brings up the emulator view: the camera eases in until
the device's screen fills the window's limiting axis, square-on, with
everything between the camera and the screen (the Moth included) left out, and
the binding bar and the cursor step aside. The same button leaves the device
and the camera eases back to the Moth. The device's pad does not play the context button, so it never
reaches the machine.

An engaged device wears the `devicePad` kit: forward and strafe drive the d-pad,
jump is A, flight is B and the Moth's `start` channel is start. On a brick, each
control lands in the joypad byte a forged cartridge copies to `0xC280` each
frame (`screen.peek <screen> 0xC280`):

| Control (`player.signal` source) | Keyboard | Machine button | Byte |
|---|---|---|---|
| `gamepad.leftStick` right, left, up, down | `d`, `a`, `w`, `s` | d-pad | `0x01`, `0x02`, `0x04`, `0x08` |
| `gamepad.buttonSouth` | `space` | A | `0x10` |
| `gamepad.buttonNorth` | `f` | B | `0x20` |
| `gamepad.start` | `enter` | start | `0x80` |
| `gamepad.buttonWest` | `e` | none: engages and leaves | none |

Each `machine.swap` restarts the device on the new cartridge and keeps the rest
of its configuration (its model and boot mode). A name the folders do not hold
is refused with the list of what they do hold, a stem two mountable files share
is refused until the whole file name is given, a file name two folders hold is
refused until its folder is given, and a file the device cannot mount is
refused with the reason `machine.content` gives. A folder that empties or
vanishes while the World runs shows in the next `machine.content`, and a
cartridge mounted from it keeps running.

## Limits

- A swap, reset or eject is a provider operation, and provider operations are
  refused while `replay.record` runs (`WhileRecording`): the replay format does
  not capture them. Engaging and playing a device records and verifies.
- No device has a speaker row, so the machines run silent; a `speakers` row
  naming an instance's `audio` output gives one a voice.
- The light gun cartridge (`light-gun.cgb`) senses a pointer only on a screen
  whose route input is `Simulation`; the device screens keep the default
  `Presentation`, so it shows its lit and dark halves and senses nothing.
