# Debug world device row

The debug world shows one of each gaming device the World hosts, at the size of
the real thing, on a row of labelled stands in the gallery hall's lobby
([`shapes.md`](shapes.md#the-hall)), on its level 0 floor at z = 28, 2.5 m apart
and facing -Z, toward the entrance, so their screens light the lobby's floor and
walls; the lobby's room lights start off so that light shows, and its wall
switch or `gallery.lights 0 on` turns them on
([the hall's lighting](shapes.md#the-hall)). Each stand carries the device's own
name. `debug.go devices` stands you in front of the middle one.
[`devices.puck`](devices.puck) holds the row; `debug.puck` imports it whole
(`import "devices"`). Each device is a named `machines` row whose screen shows
it running, and each lists the folders its swappable content comes from in
`contentFolders`: its shipped folders, then the owner's local folder under the
git-ignored `roms/` at the repository root.

| x | Device | Instance | Engine and model | Screen | Boots, with no local default | `contentFolders` |
|---|---|---|---|---|---|---|
| 17.5 | Humble Gaming Brick | `humble-gaming-brick` | `gaming-brick`, `cgb` | 0 | `tetromino.cgb` | `cartridges/cgb`, `cartridges/dmg`, `roms/hgb` |
| 15 | Advanced Gaming Brick | `advanced-gaming-brick` | `advanced-gaming-brick` | 1 | `pip.agb` | `cartridges/agb`, `roms/agb` |
| 12.5 | Humble Gaming Deck | `humble-gaming-deck` | `humble-gaming-deck` | 2 | `colour-bars.nes` | `cartridges/deck`, `roms/hgd` |

## The devices

Each device is modelled at its real size in millimetres, as its own prototype
beside its stand's, so the bake meshes it at its own scale (a 0.5 mm cell on
the handhelds) rather than at the stand's:

| Device | Body | On | Screen glass (exactly the picture) |
|---|---|---|---|
| Humble Gaming Brick: a Color brick in clear atomic orange, cartridge in its top, leaning back 20 degrees on a machined rest | 75 by 133 by 27.5 mm | a 0.4 m square plinth 1.15 m tall | 160 by 144 at 0.275 mm square pixels: 44 by 39.6 mm |
| Advanced Gaming Brick: an anodised aluminium clamshell, chamfered 1.2 mm, edge-to-edge dark glass on its upper half, a polished hinge barrel; open to its 150-degree stop, the lower half on a 30-degree rest | 84.6 by 82 by 23.7 mm closed | a plinth like the brick's | 240 by 160 at 0.255 mm square pixels: 61.2 by 40.8 mm |
| Humble Gaming Deck: a top-loading console (graphite base, aluminium deck, cartridge in the slot on top, two controller ports in front, its pad plugged into port 1) beside a 14-inch television with rabbit ears | console 160 by 168 by 64 mm; television cabinet 370 by 330 mm, 375 mm deep with the tube's housing | a walnut cart whose top is 0.55 m up | 256 by 240 at 1 by 0.875 mm (8:7) pixels: 256 by 210 mm, so the 224 lines a set shows are a 256 by 196 mm, 4:3 picture |

The plinths put a handheld's screen about 1.3 m up, turned up toward the Moth's
eye at 1.75 m (her eyes in `avatars/moth.puck`; she is 2.06 m tall), and the
cart puts the television's picture 0.79 m up, where a set stands on a low
table. The stands are solid and the devices are not: the analytic contact
provider collides each stand as its box.

Each screen row's glass is exactly the picture, never the case: `halfWidth`
is the source's width times the horizontal pixel pitch over 2, `halfHeight` its
height times the vertical pitch over 2, as a thin slab (`halfDepth` 1 mm on the
handhelds, 2 mm on the television) square to the device's face, `round: 0`. The
handhelds' pitches are square; the television's horizontal pitch is its
vertical pitch times 8/7, so the glass's own shape gives the console's 8:7
pixels. The bezel, lens or tube glass round the picture is the device's own
geometry, outside the glass.

The Humble Gaming Brick's see-through shell is not real transparency: the
renderer draws opaque surfaces. Each of its two shell slots carries a palette
`inset`, a painted plane inside the case that the surface refracts the view onto
(ior 1.49, acrylic) and shows in place of its own colour, under a clearcoat: the
front's lies 7.5 mm under the face on the board, a ramp from the dark processor
through the board to clear orange; the back's lies over the battery bay and
paints its two cells. So the shell reads as orange plastic with a board and
cells inside it, while what stands behind the brick (its rest, the plinth) does
not show through it. The cost is the inset's shading arithmetic on the pixels
those two slots cover (a refraction, a plane intersection and a four-stop
ramp), with no extra march or pass.

A `roms/` folder is spelled `../../roms/<device>` beside this file, so it
resolves when the world boots from its package in the checkout
(`--world worlds/debug`, the recipe in [`debug.md`](debug.md)). Wherever it does
not exist (CI, the Azure deploy, a compiled copy of the package, any machine
without the owner's ROMs)
`machine.content` lists it `present=false` and the world boots and validates the
same: every device boots a shipped cartridge. The files in it are the owner's
and never enter the repository, a build output, a package or a log line.

## A local default

Every device row sets `preferFolderDefault: true`, so a device boots the
default its folders name in place of its shipped cartridge. A folder names its
default in its own `folder.json`, beside the files:

```json
{
  "schema": "puck.content-folder.v1",
  "default": "<file name in this folder>"
}
```

The owner writes it once per local folder, and nothing committed names the
file: the `roms/` folders are git-ignored, so the default lives only on the
machine that holds the images. Set it from the console rather than by hand:

| To | Console line |
|---|---|
| Make a file a device's default | `machine.default <instance> <name>`, where `<name>` is any name `machine.swap` takes; it writes the file's folder's `folder.json` and names none in the device's other folders |
| Clear a device's default | `machine.default <instance> none` |
| See it | `machine.content <instance>` (`default=` the file it boots, `defaultRefused=` why it cannot; each folder segment carries its own `default=`) |
| Boot it now | `world.reload`, or `machine.swap <instance> <name>` for the running session |

The boot and every `world.reload` read the folders' `folder.json` files: the
first folder, in `contentFolders` order, whose file names a default decides.
A default the folder does not hold, one the device cannot mount, or a
`folder.json` that is not this shape (strict: the schema tag is required, and
any other member is refused) is refused on stderr by its folder, never by the
file's name, and the device boots its shipped cartridge. Wherever the `roms/`
folders are absent the devices boot their shipped cartridges as before. The
world document, a session capture and a replay tape name the shipped
cartridge either way; a replay records and verifies the same hash with or
without the local defaults, since a machine's core is outside the hash.
`machine.default` writes local files, so it needs the local console or a local
seat. The `folder.json` is never listed as content.

The Humble Gaming Brick runs the Color model, which plays the forge's `cgb` and
`dmg` cartridges alike; `device.model` switches a running brick to another
revision (`dmg`, `mgb`, `sgb`, `agb` and the rest). A Color-only cartridge on
a `dmg` model holds on the firmware logo, since its speed switch parks a
monochrome processor. The Humble Gaming Deck is the NTSC NES console, the
television beside it showing the cartridge in its slot; its screen route names the
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
| Stand at a device, facing it | `body.pose <x> 0.05 26.5 180 0 0` |
| Engage it (within 3 m), playing it with the Moth's controls | the context button: E, or X on a pad (`player.signal keyboard.e press`, then `release`); or `body.engage <screen>` |
| Leave it | the context button again; or `body.disengage` |
| Detach the screen while the machine keeps running, then show it again | `screen.eject <screen>`, then `screen.source <screen> row` |
| Put every device back on its boot cartridge, its local default or its shipped one (and pick up an edit to `devices.puck` or a `folder.json`) | `world.reload` |
| Frame the whole row | `view.override camera look 15 1.9 25.6 15 1 28`, then `view.override camera auto` |
| Frame the Humble Gaming Brick, square to its face | `view.override camera look 17.5 1.41 27.64 17.5 1.27 28.02` |
| Frame the Advanced Gaming Brick | `view.override camera look 15 1.46 27.66 15 1.26 28.01` |
| Frame the deck and its television | `view.override camera look 12.5 1.2 26.9 12.5 0.72 28` |

`<engine>` in a schema is the instance's engine: `gaming-brick`,
`advanced-gaming-brick` or `humble-gaming-deck`. `machine.state <instance>`
prints the generation an operation names; a swap, reset or eject replaces the
machine under a new generation.

## Engaging a device

Walk up to a device and press the context button, E on a keyboard or X on a
pad (the Moth's `interact` channel, which every screen route here names as its
`engageChannel`). A press engages the nearest free device within its route's
3 m (on the ground plane, the earlier in document order on a tie); no prompt shows, since the controller bar is the
debug world's only on-screen UI. Engaging captures the seat, so the
Moth stands still, and the camera eases in on the device's screen, square-on,
with everything between the camera and the screen (the Moth included) left out,
while the binding bar and the cursor step aside. It frames the screen's picture,
the glass inside its bezel, at the largest whole number of window pixels per
source row that fits the window, its width following the glass's own shape, and
as it settles the CRT look, the room's fog and the tone map fade away. Settled,
the window is indistinguishable from a desktop emulator: the console's whole
picture, centred, source-exact pixels and colours, on black. Resizing,
maximizing or going full screen refits it at once.

Each screen route's `display` authors the black around the picture (`fill`),
and `world.screens` echoes it. The same button leaves the device: the camera
eases back to the Moth, and the screen's bezel, CRT look and the room fade back
in around it. The device's pad does not play the context button, so it never
reaches the machine.

An engaged device wears the `devicePad` kit: forward and strafe drive the d-pad,
jump is A, flight is B, the Moth's `start` channel is start and her `select`
channel is select. On a brick, each
control lands in the joypad byte a forged cartridge copies to `0xC280` each
frame (`screen.peek <screen> 0xC280`):

| Control (`player.signal` source) | Keyboard | Machine button | Byte |
|---|---|---|---|
| `gamepad.leftStick` right, left, up, down | `d`, `a`, `w`, `s` | d-pad | `0x01`, `0x02`, `0x04`, `0x08` |
| `gamepad.buttonSouth` | `space` | A | `0x10` |
| `gamepad.buttonNorth` | `f` | B | `0x20` |
| `gamepad.start` | `enter` | start | `0x80` |
| `gamepad.back` | `shiftRight` | select | `0x40` |
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
