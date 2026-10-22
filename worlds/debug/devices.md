# Debug world devices

The debug world shows one of each gaming device the World hosts in Wonderland
([`wonderland.md`](wonderland.md)), the room beyond the gallery lobby's west
wall that the Moth reaches a quarter her size through the lobby's big painting.
Every device faces +X, toward the mouse hole she comes out of: the deck's
television on the west wall 7.8 m ahead of her, the Advanced Gaming Brick on
its plinth to her right and the Humble Gaming Brick on the floor to her left.
At a quarter her size the plinth stands over her head and the television
towers over her; walking in at full size through the room's own doorway, she
looms over them. `debug.go devices` stands you 2.3 m in front of the
television, at whatever size you are.
[`devices.puck`](devices.puck) holds the devices; `debug.puck` imports it whole
(`import "devices"`). Each device is a named `machines` row whose screen shows
it running, and each lists the folders its swappable content comes from in
`contentFolders`: its shipped folders, then the owner's local folder under the
git-ignored `roms/` at the repository root.

| At (x, z) | Device | Instance | Engine and model | Screen | Boots, with no local default | `contentFolders` |
|---|---|---|---|---|---|---|
| 6.6, 29.4 | Humble Gaming Brick | `humble-gaming-brick` | `gaming-brick`, `cgb` | 0 | `tetromino.cgb` | `cartridges/cgb`, `cartridges/dmg`, `roms/hgb` |
| 5, 31.6 | Advanced Gaming Brick | `advanced-gaming-brick` | `advanced-gaming-brick` | 1 | `pip.agb` | `cartridges/agb`, `roms/agb` |
| 1.4, 30.5 | Humble Gaming Deck | `humble-gaming-deck` | `humble-gaming-deck` | 2 | `colour-bars.nes` | `cartridges/deck`, `roms/hgd` |

## The devices

Each device is modelled in its real millimetres, as its own prototype beside
its stand's, so the bake meshes it at its own scale rather than at the
stand's. Every device is shown at its real size (the owner's ruling). The
Advanced Gaming Brick stands on a low display plinth whose name sits on a
placard tipped 35 degrees up toward a standing reader. The Humble Gaming Brick
leans back on its own machined rest on the floor, so at a quarter her size the
Moth walks right up to its face. Every placement turns -90 degrees about up
(`deviceYaw`), and each screen row's origin and axes turn with it.

| Device | Body | On | Screen glass (exactly the picture) |
|---|---|---|---|
| Humble Gaming Brick: a Color brick whose shell is clear atomic-orange glass over its green board, chips, speaker, cartridge and two AA cells; leaning back 20 degrees on a machined rest | 75 by 133 by 27.5 mm | the floor | 160 by 144 at 0.275 mm square pixels: 44 by 39.6 mm |
| Advanced Gaming Brick: an anodised aluminium clamshell, chamfered 1.2 mm, edge-to-edge dark glass on its upper half, a polished hinge barrel; open to its 150-degree stop, the lower half on a 30-degree rest | 84.6 by 82 by 23.7 mm closed | a 0.4 m square plinth 0.8 m tall | 240 by 160 at 0.255 mm square pixels: 61.2 by 40.8 mm |
| Humble Gaming Deck: a top-loading console (graphite base, aluminium deck, cartridge in the slot on top, two controller ports in front, its pad plugged into port 1) beside a 27-inch flat-faced television (silver bezel, black body, tapered tube housing, a speaker grille under the picture) | console 160 by 168 by 64 mm; television 740 by 621 mm, 505 mm deep | a walnut AV cabinet 1.2 by 0.58 m whose 50 mm top is 0.65 m up | 256 by 240 at 2.143 by 1.875 mm (8:7) pixels: 548.6 by 450 mm, so the 224 lines a set shows are a 548.6 by 420 mm, 4:3 picture |

The plinth puts the Advanced Brick's screen about 0.95 m up, turned up toward
the Moth's eye at 1.75 m (her eyes in `avatars/moth.puck`; she is 2.06 m tall),
and twice her height when she is a quarter her size. The cabinet puts the
television's picture centre 1.06 m up, 0.74 m under her eye standing at full
size and 0.6 m over it at a quarter. The stands are solid and the devices are
not: the analytic contact provider collides each stand as its boxes, and a
quarter-size Moth can step up into the cabinet's open right-hand bay, where the
spare cartridges lie, or climb the game cases at its south end to its top
(wonderland.md).

Each screen row's glass is exactly the picture, never the case: `halfWidth`
is the source's width times the horizontal pixel pitch over 2, `halfHeight` its
height times the vertical pitch over 2, as a thin slab square to the device's
face, `round: 0`. A screen's `origin` is the glass's front face (the slab runs
back from it), and it stands proud of the lens or tube round it (1 mm on a
handheld, 3 mm on the television): a face behind the
lens's is hidden by it, and the device shows no picture until it is engaged.
The handhelds' pitches are square and the real ones; the television's
horizontal pitch is its vertical pitch times 8/7, so the glass's own shape
gives the console's 8:7 pixels. The bezel, lens or tube round the picture is
the device's own geometry, outside the glass.

The Humble Gaming Brick's shell is real glass: its palette slot declares
`glass { depth: 0.009, ior: 1.49 }` over an atomic-orange tint (`#FF4F10`), so
it bakes into a group of its own and the glass passes shade it over the opaque
image
([glass after the opaque image](../../docs/rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image)):
the board, chips, speaker, cells and cartridge inside it are ordinary opaque
shapes seen through it, bent by its refraction and tinted by Beer–Lambert over
the distance through it. The shell is a solid prism, so a view ray crosses it
from its face to the first opaque part behind. The depth is about the 13 mm of
shell (shown) over the board, so the board reads through one full depth of the
tint, a saturated orange, and the 34 mm through the whole body at its rim
through two and a half, a deep red-orange. Glass tints only the light it transmits and adds no colour of its
own, so what lies behind it sets how bright the orange reads: the board is a
pale green, which reads amber through the tint, where a dark board turned the
shell brown and grey; the speaker behind its slots is a dark cone. A static
placement's glass depth is world units, unscaled by the placement. The limits
are the glass passes': one glass layer a pixel, and the glass casts no shadow.
The cost is the three glass passes over the views the brick shows in, and a
glass draw of the shell's own mesh.

The stands' names are embossed text runs at a 40 mm em and a 2 mm relief. An
embossed run bakes as a group of its own, refined toward its declared detail
(the thinner of its relief and a tenth of its em, about a stroke's width) but no
finer than an eighth of a 15 mm cell along the run, 1.875 mm; a stroke must span
two of those cells to survive every placement, so lettering much under a 40 mm
em loses strokes (an 18 mm "I" dropped out of the deck's name). The devices
carry no wordmarks for that reason: lettering a few millimetres high is below
what the bake keeps.

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
| Stand at a device, facing it | `body.pose <x + 1> 0.05 <z> 90 0 0`, with the device's `x` and `z` from the first table |
| Engage it (within 3 m at full size, 0.75 m at a quarter), playing it with the Moth's controls | the context button: E, or X on a pad (`player.signal keyboard.e press`, then `release`); or `body.engage <screen>` |
| Leave it | the context button again; or `body.disengage` |
| Detach the screen while the machine keeps running, then show it again | `screen.eject <screen>`, then `screen.source <screen> row` |
| Put every device back on its boot cartridge, its local default or its shipped one (and pick up an edit to `devices.puck` or a `folder.json`) | `world.reload` |
| Frame the whole room from the mouse hole, at a quarter-size Moth's eye | `view.override camera look 8.8 0.45 30.5 1.4 0.9 30.5`, then `view.override camera auto` |
| Frame the Humble Gaming Brick, square to its face | `view.override camera look 7.103 0.343 29.4 6.586 0.155 29.4` |
| Frame the Advanced Gaming Brick | `view.override camera look 5.441 1.227 31.6 4.964 0.952 31.6` |
| Frame the deck and its television | `view.override camera look 3 1.45 30.5 1.4 0.95 30.5` |

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

Out of her suit and at her full size, the Moth picks the Humble Gaming Brick up
to play it ([moth-pilot.md](avatars/moth-pilot.md#hold-the-handheld)). While she
is engaged on screen 0, the `brick-pick-up` rule (it reads the engagement as
`engaged(each, 0)`) sets her `pilotHold` cell: her arms bring the brick up to
her eyes, the brick's placement shows nothing on its machined rest (its
`respond`, a placement of its own beside the rest's), and screen 0's `carry`
moves the live picture onto the brick in her hands. Engaging starts on a view of
her holding the brick (`brick-hold`, the camera screen 0's `carry` names: at her
side and a little above her hands, brick, picture, hands and face in frame), so
the pickup shows; the Frame control (the pad's East, or `L` on a keyboard; the
console line `player.frame`) goes to the picture alone, filling the window as at
any device, and back. The device's controls play in both views. On any other
engaged screen the same control goes between the picture and the room. The
controller bar labels it Frame on the Move page, and steps aside only while the
picture is framed. Leaving puts the brick back on
its rest and lowers her arms. In the suit, or at
a quarter her size, where the brick is a third her height, she plays it where
it stands.

An engaged device wears the `devicePad` kit: forward and strafe drive the d-pad,
jump is A, flight is B, the Moth's `start` channel is start and her `select`
channel is select. The controls are the seat's own whichever way the camera
faces: right is right on the device, never turned by the room. `screen.state
<screen>` prints the pad each device receives (`pad=<buttons>/stick(<x>, <y>)`,
the stick's +X right and +Y up), and on a brick running a forged cartridge each
control also lands in the joypad byte the cartridge copies to `0xC280` each
frame (`screen.peek <screen> 0xC280`; a local folder default is not forged and
leaves it alone):

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
