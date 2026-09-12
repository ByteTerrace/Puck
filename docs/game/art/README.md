# Art and visual design

This directory contains art briefs, character model specifications, concept art packages, and visual design guidelines for Puck.

## Design hubs and concept packages

| Document / Package | Scope | Description |
|---|---|---|
| [Armored chibi hero brief](armored-chibi-hero-brief.md) | Character design & motion | Aesthetic brief defining silhouette, chunky chibi anime armor, materials, and movement dynamics for the hero avatar within Puck's signed-distance renderer. |
| [Moth concept pack (2026-09-09)](moth-concept-pack-2026-09-09/README.md) | Concept art & model sheets | Curated visual study package: 6 high-resolution concept sheets, owner selection notes, prompt provenance, and wing/armor mechanical studies. |

## Authored avatars in-engine

Live avatar implementations, authored SDF primitives, and in-game controls live directly alongside the world assets:
- [Moth avatar studio guide](../../../src/Puck.World/Assets/worlds/avatars/moth.md) — Authored body geometry, vanity slots, controls, and live iteration recipes.
- [Moth courtyard studio guide](../../../src/Puck.World/Assets/worlds/moth-courtyard.md) — Playable environment combining authored terrain, lighting, and avatar locomotion.

## Rendering aesthetics

For the mathematical principles, lighting models, material palettes, and signed-distance rendering techniques used to present this art, see:
- [SDF Handbook](../../rendering/sdf/handbook/README.md) — Complete 9-chapter guide to signed-distance evaluation and shader pipelines.
- [SDF Wiki Reference](../../rendering/sdf/reference/README.md) — Compact reference on distance bounds, blending, and lighting formulations.
