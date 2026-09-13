# Art and visual design

This directory contains art briefs, character model specifications, concept art
packages, and visual design guidance for the reference game. Art pages describe
the intended experience and review evidence; the authored world and shader
documents remain the implementation sources.

## Design hubs and concept packages

| Document or package | Scope | Description |
|---|---|---|
| [Armored chibi hero implementation brief](armored-chibi-hero-brief.md) | Character design and motion | Aesthetic brief defining silhouette, chunky chibi anime armor, materials, and movement dynamics for the hero avatar within Puck's signed-distance renderer. |
| [Moth concept pack](moth-concept-pack-2026-09-09/README.md) (2026-09-09) | Concept art and model sheets | Curated visual study package: 6 high-resolution concept sheets, owner selection notes, prompt provenance, and wing/armor mechanical studies. |

## Authored avatars

Live avatar implementations, authored SDF primitives, and in-game controls live directly alongside the world assets:

- [Moth flight studio](../../../src/Puck.World/Assets/worlds/avatars/moth.md)—Authored body geometry, vanity slots, controls, and live iteration recipes.

- [Moth courtyard](../../../src/Puck.World/Assets/worlds/moth-courtyard.md)—Playable environment combining authored terrain, lighting, and avatar locomotion.

## Rendering aesthetics

For the mathematical principles, lighting models, material palettes, and signed-distance rendering techniques used to present this art, see:

- [SDF handbook](../../rendering/sdf/handbook/README.md)—A nine-chapter guide to signed-distance evaluation and shader pipelines.

- [SDF technical reference](../../rendering/sdf/reference/README.md)—Focused references on distance bounds, blending, and lighting formulations.
