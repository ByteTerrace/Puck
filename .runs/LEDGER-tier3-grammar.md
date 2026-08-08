# Tier-3 grammar, shaped against the generated schema

Addendum to `LEDGER.md`. Written while the deletion wave is held. Assumes the in-flight
`puck schema` landing (JSON Schema for `puck.world.def.v1`, exported from the same
source-generated serializer context the loader parses through).

## The token is a document PATH, not a verb-family name

```
world.row.set    <path> <json>
world.row.remove <path> <key>
```

`<path>` is a dotted **document member path** in the document's own camelCase JSON names —
`kits`, `placements`, `hud.panels`, `views.layouts`, `groups.kinds`, `views.seatRig`. It is
never a hand-kept vocabulary: the admissible set is exactly the generated schema's property
tree, so the refusal for an unknown path can enumerate siblings from the schema, and the
schema pointer for any path is mechanical:

| shape | schema pointer | `world.row.set` means |
|---|---|---|
| keyed list | `#/properties/<path…>/items` | upsert ONE row, keyed |
| keyless list | `#/properties/<path…>` | replace the whole list |
| keyless object | `#/properties/<path…>` | replace the section |

That is the answer to the per-section verbs' last defence. "Each verb documents its own
payload shape" was true, and hand-restated 49 times; the schema now documents it once,
derived, and `--check` keeps it in sync. The general verb's description should cite
`puck schema` rather than restate a shape — same reason the layering block in
`docs/project-map.md` is generated.

## The one table that survives

Everything else collapses, but three facts per path cannot come from the schema and must be
data in ONE table (~24 rows, replacing 49 registration blocks):

1. **shape** — keyed list vs keyless list vs keyless object. `spawnPoints` and `kits` are both
   JSON arrays; one is replaced wholesale (order maps seat slots), the other upserted by key.
   The schema cannot know which.
2. **key member** — `name` / `id` / `index`, for the keyed-list arm and for `world.row.remove`.
3. **mutation constructors** — the `WorldMutation` upsert/remove pair.

Derived from source at base b5f14dcb (all 49 resolved; `world.host.set` builds
`SetHostDefaults` through a helper, which is why a naive window misses it):

| path | payload | upsert | remove |
|---|---|---|---|
| `kits` | `WorldKit` | `UpsertKit` | `RemoveKit` |
| `cameras` | `WorldCamera` | `UpsertCamera` | `RemoveCamera` |
| `screens` | `WorldScreen` | `UpsertScreen` | `RemoveScreen` |
| `speakers` | `WorldSpeaker` | `UpsertSpeaker` | `RemoveSpeaker` |
| `placements` | `WorldPlacement` | `UpsertPlacement` | `RemovePlacement` |
| `creations` | `WorldCreation` | `UpsertCreation` | `RemoveCreation` |
| `tunes` | `WorldTune` | `UpsertTune` | `RemoveTune` |
| `patches` | `WorldPatch` | `UpsertPatch` | `RemovePatch` |
| `links` | `WorldScreenLink` | `UpsertScreenLink` | `RemoveScreenLink` |
| `looks` | `WorldLook` | `UpsertLook` | `RemoveLook` |
| `addons` | `WorldAddonRow` | `UpsertAddon` | `RemoveAddon` |
| `bindingOverlays` | `WorldBindingOverlay` | `UpsertBindingOverlay` | `RemoveBindingOverlay` |
| `state` | `WorldStateRow` | `UpsertStateRow` | `RemoveStateRow` |
| `rules` | `WorldRule` | `UpsertWorldRule` | `RemoveWorldRule` |
| `grants` | *(token grammar — see exceptions)* | `UpsertGrant` | `RemoveGrant` |
| `hud.panels` | `WorldHudPanel` | `UpsertHudPanel` | `RemoveHudPanel` |
| `views.layouts` | `WorldViewLayout` | `UpsertViewLayout` | `RemoveViewLayout` |
| `groups.kinds` | `WorldGroupKind` | `UpsertGroupKind` | `RemoveGroupKind` |
| `interactions.interactions` | `WorldInteraction` | `UpsertInteraction` | `RemoveInteraction` |
| `properties.names` | *(bare name — see exceptions)* | `SetProperty(Remove:false)` | `SetProperty(Remove:true)` |
| `motion` | `WorldMotionDefaults` | `SetMotion` | — |
| `render` | `WorldRenderDefaults` | `SetRenderDefaults` | — |
| `audio` | `WorldAudioDefaults` | `SetAudioDefaults` | — |
| `authoring` | `WorldAuthoringDefaults` | `SetAuthoringDefaults` | — |
| `collision` | `WorldCollision` | `SetCollision` | — |
| `host` | `WorldHostDefaults` | `SetHostDefaults` | — |
| `inputHold` | `WorldInputHoldSettings` | `SetInputHold` | — |
| `hud.defaults` | `WorldHudDefaults` | `SetHudDefaults` | — |
| `spawnPoints` | `WorldSpawnPoint[]` | `SetSpawns` | — |
| `views.seatRig` | `WorldCameraRig` | *(see below)* | — |

## Count correction: tier 3 is 50, not 49

`world.view.rig` writes `views.seatRig` — a keyless nested object. The ledger classed it a
DOOR because no general form existed ("sugar-shaped but sole door"). Under a **path**-addressed
general verb it is no longer sole: `world.row.set views.seatRig <json>` expresses it exactly.
It joins tier 3.

| | killed | added | net | surface |
|---|---|---|---|---|
| tier 3 (revised) | 50 | 2 | **−48** | |
| **all three tiers** | **119** | **6** | **−113 (−32.0 %)** | **353 → 240** |

DOOR drops 189 → 188.

## Two exceptions the owner still has to rule on

- **`grants`** — `world.grant.set` takes the full `world.grant` token grammar
  (`<principal> <capability> <subject> [exclusive] [budget:n] …`), not inline JSON. Either it
  keeps a token arm inside the general verb, or the grant row gains a JSON form. The runtime
  pair `world.grant`/`world.revoke` is unaffected either way — verified in source, they submit
  `SubmitGrant`/`SubmitRevoke`, not document mutations.
- **`properties.names`** — a bare name, not a row. Worth noting it is *already* one mutation
  kind split across two verbs by a `Remove:` bool, which is the same door-not-type defect
  `world.state.cell.text` shows. It folds into `world.row.set`/`world.row.remove` more
  naturally than any other section.

Two more token-grammar verbs (`world.population.defaults`, `world.population.spawn`) target
`population` and would join tier 3 if the general verb is JSON-only-plus-exceptions rather
than JSON-only. Not counted above.

## What stays

`world.state.cell.set` / `.cell.remove` are a **finer grain than the row** — a narrow cell
write, not a whole-row RMW. They are not sugar and do not fold. (`world.state.cell.text` still
dies; it is a split by operand type, not by grain.)

## Proof plan under the schema

The `LEDGER.md` oracle extends cleanly and gets one new leg:

1. Author the payload **against `puck schema`** (`#/properties/<path>/items`).
2. Run the per-section verb in a fresh boot → `world.save X.json`.
3. Run `world.row.set <path> <payload>` in another fresh boot → `world.save Y.json`.
4. `X` and `Y` byte-identical, both echoing the same `[world.mutation: …]` kind, `wire.errors 0`.

Run E in `LEDGER.md` already proves the closing half of this loop: a `world.save` row fed back
through its own `.set` reproduces the document byte-for-byte, so a schema-valid payload and a
saved row are interchangeable inputs to the general verb.

A `--check`-style negative control belongs here too: a payload that violates the schema must
be refused **by the verb**, naming the schema pointer, and must not reach the mutation
substrate. That is the pre-submission validation surface the schema buys, and it is a strictly
new ability the 49 per-section verbs never had.

## Rebase reconciliation

`.runs/reconcile.sh` re-runs enumeration source (a) in both composition tiers and diffs
against the recorded baseline. Validated at base b5f14dcb: reproduces 213 headless / 353
windowed with empty added/removed sets and an empty subset-violation set. Run it at the
rebase; the delta will be named, not guessed.
