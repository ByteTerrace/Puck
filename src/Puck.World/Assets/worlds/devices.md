# Debug world device row

The debug world shows one instance of every gaming machine the World hosts, on a
row of labelled plinths at z = -6 west of the spawn, 3 m apart and facing +Z.
[`devices.puck`](devices.puck) holds the row; `debug.puck` imports it whole
(`import "devices"`). Each device is a named `machines` row whose screen shows
it running, and each names the folder its swappable content comes from in
`contentFolder`.

| x | Device | Instance | Engine and model | Screen | Boots | `contentFolder` |
|---|---|---|---|---|---|---|
| -10 | Humble Gaming Brick, DMG | `brick-dmg` | `gaming-brick`, `dmg` | 0 | `tetromino.dmg` | `../cartridges/dmg` |
| -13 | Humble Gaming Brick, CGB | `brick-cgb` | `gaming-brick`, `cgb` | 1 | `tetromino.cgb` | `../cartridges/cgb` |
| -16 | Humble Gaming Brick, AGB mode | `brick-agb` | `gaming-brick`, `agb` | 2 | `hgb-mirror.cgb` | `../cartridges/cgb` |
| -19 | Advanced Gaming Brick | `advanced-brick` | `advanced-gaming-brick` | 3 | `pip.agb` | `../cartridges/agb` |
| -22 | Tune jukebox | `tune-jukebox` | `tune-instrument` | 4 | `arrival-tune.audio.json` | `tunes` |
| -25 | Humble Gaming Deck | `gaming-deck` | `humble-gaming-deck` | 5 | `colour-bars.nes` | `../cartridges/deck` |

The `gaming-brick` engine's three hardware families are the three brick devices;
its other revisions (`mgb`, `sgb`, `cgb0` and the rest) are steppings of those
families that `device.model` selects on a running brick. The DMG mounts the
forge's `dmg` cartridges: a CGB cartridge is Color-only, and its speed switch
parks a monochrome processor, so on the DMG it holds on the firmware logo (a
`fast` boot shows a white screen). The AGB
mode device is the AGB console running brick cartridges; the Advanced Gaming
Brick is the same console running its own. The jukebox's content is a
`puck.tune.v1` document, and its screen is the jukebox's transport display. The
Humble Gaming Deck is the NTSC NES console, a set above it showing the
cartridge in its slot; its screen route names the controller port an engaged
seat plays (`controls`, port one), since the console has two.

The shipped cartridges sit one folder per forge target, `cartridges/cgb/`,
`cartridges/dmg/` and `cartridges/agb/`, each source beside its compiled
document. A device lists the files in its folder its engine can mount: the
`.cartridge.json` documents for a brick, every file for the jukebox and the
deck, whose engines read their content raw. `test-card.agb` is the Advanced
Gaming Brick's second cartridge: eight colour bars drifting one pixel a frame,
so a picture that stops drifting is a machine that stopped. The DMG's two are
`tetromino.dmg`, the CGB game in four shades, and `test-card.dmg`, four shade
bars drifting the same way (`machine.swap brick-dmg test-card`). The deck's two cartridges in `cartridges/deck/` are built by
`puck cartridges deck` rather than compiled from a source:
`colour-bars.nes` shows eight bars that A, B and select tint and start turns
grey, and `moving-sprite.nes` bounces a ball the d-pad steers
([the Deck's hosting page](../../../../docs/emulation/hgd/hosting.md#test-cartridges)).

## Devices

Every line runs at the console; `<instance>` is a name from the table.

| To | Console line |
|---|---|
| List what a device can swap to, and what it runs now | `machine.content <instance>` |
| Swap to another cartridge in its folder | `machine.swap <instance> <file>`, or `machine.swap <instance> <stem>` (`machine.swap brick-cgb light-gun`) |
| Read the running content back | `screen.state <screen>` (`cartridge <path> hash … rom …`) or `machine.content <instance>` (`mounted=`) |
| Read every device's generation and status | `machine.state` |
| Reset a device on its current content | `machine.operation <instance> <generation> machine.reset {"schema":"puck.<engine>.machine-reset.v1"}` |
| Eject the content, leaving the device on with nothing in it | `machine.operation <instance> <generation> content.eject {"schema":"puck.<engine>.content-eject.v1"}`; `machine.swap` puts one back |
| Change a brick's revision live | `machine.operation <instance> <generation> device.model {"schema":"puck.gaming-brick.device-model-set.v1","model":"sgb"}` |
| Stand at a device, facing it | `body.pose <x> 0 -4.5 0 0 0` |
| Engage it (within 3 m), playing it with the Moth's controls | `body.engage <screen>` |
| Disengage | `body.disengage` |
| Detach the screen while the machine keeps running, then show it again | `screen.eject <screen>`, then `screen.source <screen> row` |
| Put every device back on its authored cartridge (and pick up an edit to `devices.puck`) | `world.reload` |
| Frame the whole row | `view.override camera look -17.5 2.6 4.5 -17.5 0.6 -6`, then `view.override camera auto` |
| Frame one device | `view.override camera look <x> 1.1 -4.4 <x> 0.75 -6` |

`<engine>` in a schema is the instance's engine: `gaming-brick`,
`advanced-gaming-brick`, `tune-instrument` or `humble-gaming-deck`. `machine.state <instance>`
prints the generation an operation names; a swap, reset or eject replaces the
machine under a new generation.

An engaged device wears the `devicePad` kit: forward and strafe drive the d-pad,
jump is A, flight is B and rise is start. The Moth binds a gamepad's South
button to both jump and rise, so South presses A and start together, and North
presses B; `player.signal gamepad.buttonSouth press` is the scripted A. Engaging captures the seat, so the
Moth stands still until `body.disengage`. The screens name no engage channel,
so a jump beside a device stays a jump; engaging is the console line.

Each `machine.swap` restarts the device on the new cartridge and keeps the rest
of its configuration (its model and boot mode). A name the folder does not hold
is refused with the list of what it does hold, and a stem two files share is
refused until the whole file name is given.

## Limits

- A swap, reset or eject is a provider operation, and provider operations are
  refused while `replay.record` runs (`WhileRecording`): the replay format does
  not capture them. Engaging and playing a device records and verifies.
- No device has a speaker row, so the machines run silent; a `speakers` row
  naming an instance's `audio` output gives one a voice.
- The light gun cartridge (`light-gun.cgb`) senses a pointer only on a screen
  whose route input is `Simulation`; the device screens keep the default
  `Presentation`, so it shows its lit and dark halves and senses nothing.
