# Art and visual design

This directory contains art briefs, character model specifications, concept art
packages, and visual design guidance for the reference game. Art pages describe
the intended experience and review evidence; the authored world and shader
documents remain the implementation sources.

## Design hubs and concept packages

| Document or package | Scope | Description |
|---|---|---|
| [Canyon discovery gallery](canyon-discovery-pack/README.md) | Major demo layout and interaction design | B-based portal court, sixteen-station shape workshop, Wonderland/link lab, six-game commons and studio/trail. |
| [Canyon workshop environment pack](canyon-workshop-pack/README.md) | Selected valley and gallery direction | B-based environment views, interior scenes, construction details, and modeller handoff. |
| [Valley and gallery visual references](valley-and-gallery.md) | Environment direction and real-life references | Source references, initial comparisons, and the selected B direction for a painterly, cartoony futuristic valley and gallery. |
| [Armored chibi hero implementation brief](armored-chibi-hero-brief.md) | Character design and motion | Aesthetic brief defining silhouette, chunky chibi anime armor, materials, and movement dynamics for the hero avatar within Puck's signed-distance renderer. |
| [Moth concept pack](moth-concept-pack/README.md) | Concept art and model sheets | The Moth's visual standard: the mech-suit turnaround, construction details, poses and expressions, flight, the off-suit Moth with her scale and fit inside the suit, the handheld at true scale, the flight-pack references and prompt provenance. |

## Concept art review

Concept art a modeller builds from carries no glaring mistake (the owner's
ruling). Every panel passes a written audit by both partners, the artist and the
checking partner, covering:

- attachment and continuity;
- joint range;
- physical coherence of the pose (thrust against motion, weight over the feet);
- agreement with the turnaround;
- the camera's perspective;
- compositing seams;
- the character's fixed facts.

A panel that fails is regenerated or removed, never kept with a caveat.

## Authored avatars

Live avatar implementations, authored SDF primitives, and in-game controls live directly alongside the world assets:

- [Moth flight studio](../../../worlds/debug/avatars/moth.md)—Authored body geometry, vanity slots, controls, and live iteration recipes.

- [Moth courtyard](../../../experimental/src/Puck.World/Assets/worlds/moth-courtyard.md)—Quarantined prior art: a playable environment combining authored terrain, lighting, and avatar locomotion.

## Rendering aesthetics

For the mathematical principles, lighting models, material palettes, and signed-distance rendering techniques used to present this art, see:

- [SDF handbook](../../rendering/sdf/handbook/README.md)—A nine-chapter guide to signed-distance evaluation and shader pipelines.

- [SDF technical reference](../../rendering/sdf/reference/README.md)—Focused references on distance bounds, blending, and lighting formulations.
