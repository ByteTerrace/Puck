# Example documents

These small documents show the authoring formats for shapes and audio. They
are reference assets, not standalone applications. The
[authoring guide](../authoring/README.md) explains how to choose a workflow;
[World authoring](../../src/Puck.World.Authoring/README.md) defines the formats.

## Creations

A creation describes shapes, materials and their composition. Browse
[creations](creations/) for the full set, or start with one of these:

- [Adventurer](creations/adventurer.creation.json): a humanoid model.
- [CRT robot](creations/crt-robot.creation.json): a character built around a screen.
- [Lantern fish](creations/lantern-fish.creation.json): an organic character.
- [Town cottage](creations/town-cottage-a.creation.json): a building assembled
  from repeated shapes.
- [Town arcade](creations/town-arcade.creation.json): a cabinet-shaped asset.

The directory also includes a second cottage, a fountain, grocery, marquee,
and street furniture such as benches, lamps, mailboxes, planters and trees.

## Audio

The [tunes](tunes/) directory contains
[Brickfall](tunes/brickfall.audio.json) and a
[small tune example](tunes/tune.audio.json). Read the owning audio format before
choosing a playback or cartridge target; the document alone does not establish
that every target accepts the same resources.

## Runnable examples

To see a complete host workflow, use the
[shader pipeline example](../../src/Puck.World/README.md#shader-pipelines) or the
[cartridge forge guide](../../src/Puck.GamingBricks.Forge/README.md). Those guides
link the source files, launch commands and expected results together.
