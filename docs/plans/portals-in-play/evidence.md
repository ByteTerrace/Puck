# Portals in play: evidence

The longer evidence behind [the portals in play design](../portals-in-play.md):
what the code does today for each thing the plan did not know, the systems the
design considered before proposing anything new, and what other games did.
Code is cited by symbol. A statement marked **read** was established by reading
the source and has not been run.

## 1. What the code does today

### 1.1 Can a placement's pose follow a state cell?

A placement's position can follow a state cell, its orientation cannot, and a
door on such a placement does not work.

- **Position can.** `WorldPlacement.Position` is a `WorldPlacementTranslation`,
  which holds either a literal or a `state.<row>[.<key>]` reference to a text
  cell that holds `[x, y, z]`. The server binds such a reference to its live
  arena (`WorldStateDocumentValues.BindLive`), and
  `WorldPlacementFrameCompilation.Resolve` recompiles the frames on every ask
  when any row of the section reads a cell
  (`WorldPlacementFrameCompilation.HasLiveInputs`). The gallery's
  `shapes.puck` already places a row this way
  (`position: "state.galleryInputs.probe"`).
- **Orientation cannot.** `WorldPlacement.YawDegrees` is a plain number, and a
  placement has no pitch or roll at all. A face's tilt comes from the shape it
  names inside the creation (`WorldFaceCatalog`'s frame derivation).
- **A door's frame does not follow the cell (read).** `WorldFaceCatalog.For`
  derives every face frame once per definition object and keeps it, and
  `WorldDefinition.WithStateSection` hands the same catalog to the definition
  that replaces the state section. The catalog's own comment says it reads
  "never a state row", which stops being true when a row's position is a
  reference. A rule's cell write does not replace the definition at all: it
  ends in `WorldDocument.ReconcileStateConsumers`, which refreshes drive gates,
  field lattices and inhabit counts, and nothing that holds a face frame. So a
  door on a row with a state-bound position is drawn where the cell says and
  fires where the cell said when the catalog was first asked. The validator
  refuses a door on an attached, inhabited or animated placement for exactly
  this reason ("the door's frame would be stale every tick") and says nothing
  about a state-bound position.
- **`upsertPlacement` installs a literal row as a document mutation.**
  `WorldFactsCompiler.ResolveUpsertPlacement` wraps the authored row in a
  `WorldDocumentEffect`; firing it submits a `WorldMutation.UpsertPlacement`.
  That is a whole-document write: it is charged
  `WorldPlacementEffectCost.DocumentCost` for the revalidation, it is one of the
  mutations that recompile the solid consumers, and it makes a new definition
  that every session is sent.
- **A new door with a window is refused once the world is running (read).**
  Every face whose source shows something claims a screen slot, and the slots
  are reserved once at boot (`WorldDefinition.DerivedFaceScreens`,
  `WorldServer.BootDerivedFaceScreens`). The document's compose step refuses a
  candidate that claims more, and its refusal ends "restart the host to load a
  wider one". So the plan's earlier statement that a door a rule installs works
  at once holds only for a door that shows nothing, or one that replaces a row
  already holding a slot.

### 1.2 Do any queries cross a door?

No query the simulation runs crosses one.

| Query | Where it ends | Crosses a door |
|---|---|---|
| `$los`, a sensed target's sight, a target register's sight, flock sight, a decision's neighbours | `WorldPopulation.HasLineOfSight`, which asks the solid field for one straight segment (`IWorldQuery.LineOfSight`) | No |
| A tether's aim and a hold's reach | `IContactField.TryNearestSurfaceAlongDirection` and `TryHoldableSurfaceAlongDirection`; `WorldPortalHoleContactField` passes both to the field it wraps unchanged | No |
| Body against body contact, and collision events | One broadphase over world positions (`FixedSweepGrid`); a body halfway through a door has no presence on the far side | No |
| A body's sweep against the world | `WorldPortalHoleContactField` takes back the push of the wall behind the glass; nothing resolves the part of the body past the glass against what stands on the far side | No |
| Routes | `WorldDocumentNavigation` opens the wall behind a door for the walker's contact; the route search joins domains only where two grids share cells | No |
| The seat's pointer ray | It reaches the simulation as input (`WorldPointerRayCapture`, read by `$pointer` against a screen and `$board:pointer` against a table's plane); nothing casts it at the world | No |
| A pick through a window, the chase camera's boom | `WorldRoutedScene.TrySurface` and the camera carry do continue through the glass | Yes, presentation only |

`Puck.Physics`, `Puck.SignedDistance` and `Puck.Maths` do not contain the word
portal, so the kernels cannot know a door exists; the continuation has to be
added by the host that holds the doors.

### 1.3 How does the crossing sweep treat a door that moves?

It does not see the motion.

`WorldInstanceHost.ScanInstancePortals` tests the segment from each body's
previous position to its current one (`WorldBody.FixedPreviousPosition`,
`WorldBody.FixedPosition`) against an opening built on the face's frame
(`WorldFaceRegion.Sweep`). The frame comes from the catalog, so within one
definition it never moves. The only ways to move a door today are these:

- A document mutation moves it between two ticks as a jump. A body the glass
  jumps over has a zero-length segment, so it is caught only if it ends inside
  the opening's depth band, and that band is sized from the fastest *body* the
  document declares (`WorldFacePortalPolicy.CrossingFloor`), never from a door's
  speed. A door that jumps further than the band passes over the body without
  firing.
- A state-bound position moves the drawing and not the opening (1.1).
- An attached, inhabited or animated placement is refused.

The latch that makes a door fire once per entry (`WorldPortalOccupancy`) is
keyed by placement, face and body, and holds no door pose, so it cannot tell a
body walking in from a door moving over a body.

### 1.4 What does a door onto another world need, opened mid-play?

- **A destination the source world's author already declared.** A portal facet
  names a `destinations` row (`WorldPlacementPortal.Destination`), and
  `WorldDestination`'s own remarks call the section "Boot-authored document data
  only" with "no live mutation arm". A player cannot name a world the author
  did not list.
- **A face in the destination to pair with.** A see-through door needs
  `arrival: mapped` and a `counterpart` (the validator's session-source check),
  and the counterpart must exist in the destination's delivered document when a
  body crosses (`WorldPortalCounterpart.TryResolve`). So the destination must
  already hold a suitable face, or install one, which is a mutation of its own
  document under its own grants.
- **A pair that keeps world up.** `WorldFrameIsometry.PreservesWorldUp` refuses
  a pair that is not two walls or a floor and a ceiling, because a body's
  motion is stored as a heading, a planar velocity and a vertical velocity.
  A door on a floor paired with a door on a wall is refused.
- **An opener.** No door has one. The destination's scope is resolved from
  whoever is crossing (`WorldSessionResolver.TryDeriveScopeKey`, called from the
  scan for each hit), so a `user` scoped door leads each traveller to their own
  world, and a party of two identities is refused. "My door to my home, which my
  friend walks through" cannot be written.
- **Everything else exists.** Display is a session that observes the destination
  under the destination's admission (`WorldScreenSource.Session`); transfer is
  reserve then commit with a deadline (`WorldPortalDefaults.HoldSeconds`,
  `WorldPlacementPortal.Capacity`), in one process or across two
  ([federated transfer](../../architecture/worlds.md#federated-transfer)).

## 2. Systems considered

Rule 7 asks a design to name what already exists before it adds anything. Each
row is a mechanism this design reuses, extends or sets aside.

| What the design needs | What exists | Verdict |
|---|---|---|
| A thing whose pose is authority state, with its previous pose, velocity, a teleport-or-glide flag, checkpoint, replay, interpolation and disclosure | A body (`WorldBody`), posed by the `pose` and `poseCell` effects | Reused: a door that can move rides a body |
| A placement that follows a body | The attach facet (`WorldPlacementAttach`, resolved by `WorldPlacementAttachment.TryResolve`); its region, solid, emission and gravity area already follow | Extended: the portal face is the one facet left behind, and the resolve gains the body's full orientation |
| Bodies that come and go from a fixed budget | An inhabit count bound to a cell (`WorldPlacementInhabit.Count`), which the population reconciles | Reused: a closed door is a retired body |
| The swept test | `WorldFaceRegion.Sweep` over `FixedSegmentClip.Axis` | Reused unchanged, fed coordinates in the door's own frame |
| The map through a pair | `WorldFrameIsometry` (`MapPoint`, `MapRate`, `MapVector`) and `WorldBody.CrossInPlace` | Reused; the up-preserving restriction is lifted for bodies |
| An aim that is deterministic input | The seat's pointer ray (`SourceRay`, `WorldPointerRayCapture`), already quantized once and recorded in the tick's snapshot | Reused: it is the aim |
| A ray to a surface with a normal | `IContactField.TryNearestSurfaceAlongDirection` returning `FixedSurfaceAttachCandidate`; the tether's aim and a placement's `surface` projection both use it | Reused: the new rule-readable cast compiles onto it |
| "May a thing stand here" | A placement's `spatial` volumes with occupation, clearance and author-named influence channels (`WorldSpatialQueryIndex`); the `surface` facet's named targets and facing limit (`WorldPlacementSurface`); the grip facet's per-row surface trait (`WorldPlacementGrip`) | Reused for the admission vocabulary; the index is per definition, so it needs live rows for doors that move |
| Contact and drawing across a boundary through one isometry | The seam's neighbour field (`WorldAdjacencyContactField`), its bounded admission (`WorldPopulation.AdmitAdjacencyResponse`) and one responder per authority pair (`WorldCrossAuthoritySettlement`) | Reused: the far side of an open door is served the way a seam's neighbour is |
| A reservation the far side is bound by | The transfer lease and its checkpoint (`WorldTransferLeaseCheckpoint`), the peer call (`Reserve`, `Commit`, `Abort`) | Extended with a door lease on the same call path |
| Resolve once, then observe and enter | `WorldSessionResolver` and the model's `ResolvedWorldSession` | Extended: a door resolves at opening, by its opener |
| A view per visible glass, held when small, at a rate and quality by size | `WorldViewGraphHost.PortalSightOf`, `SdfUpdateRateTiers.ViewHeldPixels`, `WorldRenderSettings.NestedViewQuality` (`render.nested`), `views.nestingDepth` | Reused |
| Many views of one world from one residency | A session delivered everything its destination holds renders as a view of that world's one residency, and sessions the authority proves alike share one scene (`WorldSceneProof`, `WorldInstanceHost.TryWindowEndpoint`) | Reused; the design keeps doors inside what the proof covers |
| A gate on who may pass | The portal facet's doc comment reserves "a future fact-gate field" | Built as the comment describes |

Set aside, with the reason:

- **A literal `upsertPlacement` per door.** Refused past the boot reservation,
  a whole-document write per shot, and its pose cannot come from an aim.
  It stays the right tool for an author editing a world.
- **A placement whose pose is bound to cells.** It would need an orientation
  binding, a previous pose, a teleport flag and a place in every checkpoint:
  the state a body already carries, kept a second time in text cells.
- **A deal template** (`WorldPlacementDeal`), which stamps child placements
  from a keyed row. Its children are static rows of a new definition, and a
  dealt row refuses `faceSources`.
- **A new ray kernel that knows doors.** The kernels know no body and no
  document; the door layer wraps them, as the opening and the seam layers do.

## 3. What other games did

Each claim links the source it was read from. A statement marked *unverified*
has no source read behind it and should be checked before anything rests on
it.

**Narbacular Drop (DigiPen, the student game Portal grew from).** Two linked
portals placed with the pointer on any wall, floor or ceiling of natural
material; metal and other artificial surfaces refuse one
([Wikipedia](https://en.wikipedia.org/wiki/Narbacular_Drop)). The lesson Puck
takes: the admission rule was a property of the surface's material from the
first prototype, and it was the level designer's main tool.

**Portal and Portal 2 (Valve).**

- *Rendering.* Valve's lecture for Harvard's games course compares two ways to
  draw a portal: a texture per view, which "gets big fast" under recursion and
  "can't effectively use antialiasing", and a stencil pass that "renders entire
  frame in the back buffer; no texture memory needed". Only the first two
  levels of a portal seen through a portal are really drawn; deeper levels are
  copies of the second, which the notes call "not perfect"
  ([lecture notes](https://cs50.harvard.edu/games/2018/notes/11)).
- *A body in the glass.* The same notes: the engine duplicates the object's
  geometry at the other portal every frame and clips each copy by its portal's
  plane, and a beam is split into the segment into one portal and the segment
  out of the other. Valve's tools name an entity the engine creates around each
  portal for physics copies
  ([Physicsclonearea](https://developer.valvesoftware.com/wiki/Physicsclonearea)).
  That a physics copy of a straddling object stands on the far side so things
  there can hit it is *unverified* beyond that entity's existence.
- *Placement.* A portal goes only on a flat, stationary surface of the right
  material that is large enough; firing at an occupied spot bumps the new portal
  beside the old one; a portal cannot be placed while looking through one; a
  portal with no partner is a wall; momentum is conserved between the pair
  ([Portal Wiki](https://theportalwiki.com/wiki/Portals)). Mappers fence
  placement with brush volumes at the edges between admitting and refusing
  surfaces ([func_portal_bumper](https://developer.valvesoftware.com/wiki/Func_portal_bumper)).
- *Moving portals.* Portal 2 allows a portal on a moving surface in one level,
  behind a server switch, and Valve's wiki warns that "neither the player nor
  physics objects are ever expected to go through moving portals; thus, moving
  portal interactions tend to be very buggy and can completely break
  physics/momentum"
  ([Portals on Moving Surfaces](https://developer.valvesoftware.com/wiki/Portals_on_Moving_Surfaces);
  the passage is quoted from a search excerpt of that page, not from the page
  itself). The lesson: Valve's crossing was written for a still door, and
  a moving one was never given the relative sweep. Puck writes the relative
  sweep first.
- *Cut features.* The lecture lists portals inside portals and double flings
  among features dropped because "the cons outweighed the pros".

**Prey (2006, Human Head).** Fixed portals and walkable walls on a modified
id Tech 4; the player opens none
([Wikipedia](https://en.wikipedia.org/wiki/Prey_(2006_video_game))). An earlier
version built wholly around portals was abandoned, and its engine lead later
wrote that "portal tricks such as these should be used as tricks, not as an
engine paradigm"
([The Cutting Room Floor](https://tcrf.net/Prerelease:Prey_(2006))). Puck's
answer to that warning is the world model: a portal is a composition of a
destination, a display and a transfer, never the renderer's organizing idea.

**Splitgate (1047 Games).** An arena shooter where every player carries a
portal pair. Its lead describes shooting through a portal as a line trace
continued at the angle it hit; a feature on the game notes that the server has
to account for every place the battlefield can be seen from; and the sequel
moved to portals that appear at once on the client instead of waiting on the
round trip
([Niche Gamer interview](https://nichegamer.com/splitgate-2-interview-pax-east-2025),
[PCGamesN](https://www.pcgamesn.com/splitgate-arena-warfare/splitgate-halo-portal),
[GamingBolt interview](https://gamingbolt.com/splitgate-2-interview-post-launch-content-player-feedback-console-performance-and-more)).
Lessons: hits through a door are the same trace continued, which is this
design's one rule; interest has to follow what each door shows; and a placed
door wants a predicted picture before the authority answers.

**Antichamber.** Seamless, impossible connections made with portals; a paper
on impossible spaces notes that the implementation's limits sometimes showed
([Würzburg GTS proceedings](https://opus.bibliothek.uni-wuerzburg.de/files/24604/2021-GTS-Proceedings_05-paper.pdf)).
Its doors are authored and frameless, which Puck already plans as the
standard's `threshold` template. How it was built is *unverified*.

**Manifold Garden.** A world that repeats in every direction, seen through
portals with recursion. Its graphics programmer reports that "the portals only
really started working without flaws or limitations when we rewrote our
rendering pipeline"
([SIGGRAPH blog](https://blog.siggraph.org/2020/12/manifold-garden-takes-control-with-a-gpu-rendering-pipeline.html/)).
Puck's renderer already treats every view as an instance of one graph, which
is the position that team had to rebuild toward.

**Superliminal.** Its technical write-up covers forced perspective, done by
casting many rays from the camera past the held object, and does not describe
its doors ([PlayStation Blog](https://blog.playstation.com/?p=332742)). What
Puck takes from it is already built: a door pair of two sizes resizes what
crosses it. How its doors work is *unverified*.

Where Puck differs from all of them: every one runs a single authority and a
single world. None had to say what a door means when its far side belongs to
someone else, and that question is most of
[the design](../portals-in-play.md#4-a-door-onto-another-world-opened-in-play).
