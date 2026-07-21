#:property EnforceCodeStyleInBuild=false
#:property AnalysisLevel=none
// proof.cs — Puck.World's PROOF SUITE + THE EXPO, one .NET 10 file-based app.
//
//   dotnet run src/Puck.World/scripts/proof.cs -- <subcommand> [options]
//
// Subcommands:
//   generate --kind parade|flood|flight|hop|expo [--population N] [--seed S] [--out PATH] [kind opts]
//       Emits a timed STDIN corpus (the PS-compatible @<t> text, plus the #sweep-at directive).
//   run [--corpus PATH | --kind K] [--headless] [--loop] [--quality low|medium|high]
//       [--width W] [--height H] [--no-build] [--tolerance T] [--yaw-tolerance Y]
//       [--min-fps FPS] [--log PATH] [--world-arg PATH]
//       The feeder: builds, launches Puck.World, paces the corpus into stdin over reader
//       threads, marks each sweep, asserts (closed-form #expect / band / separation where
//       present; report-only otherwise), and asserts the last rolling world.fps sample is at
//       least 60 avg/worst by default. Pass --min-fps 0 to disable the performance assertion.
//       --world-arg forwards a --world <value> argument to the launched child — a world file, or the
//       literal `baked` to run the in-code definition. DEFAULT kind when nothing is specified: expo.
//   compare --reference A --candidate B [--tolerance T] [--yaw-tolerance Y]
//       Rerun byte/near-identity of two transcripts' final sweeps + dispersion statistics.
//   screens [--width W] [--height H] [--no-build] [--rom PATH]
//       The diegetic-screen + engagement proof: a joypad-echo ROM boots onto a screen slab and the world's own
//       intent wire drives it. The route table is asserted through its REFUSALS (a non-machine slot, a passive
//       jumbotron, an out-of-range engage each name their own reason), then a within-radius engage lands, a run +
//       press reaches 0xC000 as the Up|A image the ROM echoes back, the ROM's 0xC001 counter advances (liveness),
//       and disengage clears the held buttons AND hands the avatar back its drive (a measured z displacement, not
//       an echo). A population reactivation must not inherit the stale route; eject reveals the declared view.
//   worlddoc [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The world-document proofs (puck.world.def.v1): (a) the save-idempotence
//       gate — EVERY checked-in Assets/worlds/*.world.json (default, kart-remap, expo, kiosk, planetoid) boots, saves,
//       and re-saves from its own output to the same bytes, so a save that folds session state stays
//       idempotent on a fresh boot (the saved file is never compared against the checked-in one —
//       R18); (b) the baked default boots, runs, and is deterministic — one checked-in-world run plus TWO
//       `--world baked` runs (the in-code definition, requested by name) of the same short hop corpus: all
//       three capture full pose coverage, the two baked runs compare byte-identical (determinism is a
//       per-document property), and the loud "[world] definition: baked default (...)" line appears only in
//       the baked runs. The checked-in-vs-baked delta is REPORTED, never asserted — they are different
//       documents (the shipped world declares solidity the in-code one does not).
//   mutate [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The mutation-vocabulary round-trip proof: (a) a scripted world.kit.tune / world.undo /
//       world.save round-trip over stdin, asserting the journal-length dirty counter at each step and the
//       server's loud accept/undo lines; (b) rejection honesty — world.kit.remove on the defaultSeatKit is
//       rejected loudly and the document is unchanged; (c) survival — relaunching with --world <the saved
//       file> boots it (the "[world] definition:" line) and the saved JSON carries the tuned value. The
//       protocol-version handshake is proven by the implementing session, not re-covered here (no scripted
//       Join path exists over stdin without inventing a debug verb).
//   grants [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The principals/capability-grants proof: (a) world.addon.set mounts an
//       autopilot WorldAddonRow (data-only — the driver mounts enabled addon rows only at BOOT) and world.save writes
//       it; (b) relaunching --world <the saved file> asserts the "[world.addon: mounted autopilot ...]" boot line,
//       then world.grant addon:autopilot drive body:<n> exclusive is asserted to move the granted body (two
//       player.where samples a second apart); (c) world.revoke mid-run asserts the edge-latched
//       "[world.grant denied: ...]" line and that the body then holds perfectly still (two more samples, identical);
//       (d) denied-mutation honesty — world.revoke console mutate section:kits makes world.kit.tune fail loudly with
//       world.status's dirty counter unchanged, and re-granting makes the same command apply; (e)/(f) exclusivity in
//       both orders plus the exclusive-wildcard outright rejection; (g) EXCLUSIVE SECTION ACQUISITION — the
//       seeded per-section Mutate defaults never block, so world.grant seat1 mutate section:scene exclusive succeeds
//       on a default table, the console's scene mutation is then denied at the grant boundary, and revoking the hold
//       restores it; (h) PROFILE SUBJECT — Edit is checked against the concrete profile:<id> subject: the
//       seeded Edit/all wildcard passes, revoking it denies profile.section, and a narrow profile:amber grant
//       restores exactly amber while cobalt stays denied. (h) writes the player store, so this proof now backs up +
//       restores the REAL world/ subtree like the bindings proof (the cleared store reseeds the deterministic
//       amber/cobalt catalog ids).
//   bindings [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The player-document + layered binding-resolution proof (engine default ⊕ world overlay ⊕ profile ⊕ session): (a) session A boots the
//       REAL local player-document store fresh, asserts the engine-default composed mapping (player.bindings), live-
//       rebinds keyboard.e -> player.forward (player.bind), and profile.save folds+persists it into the boot profile
//       (revision bumps, read back through profile.doc); (b) session B relaunches and asserts the rebind survived and
//       the revision did not bump again on a plain boot; (c) session C boots --world kart-remap.world.json and asserts
//       the world's bindingOverlays entry merges over the engine default (gamepad.buttonEast -> player.primary), then
//       world.bindings.remove live-recomposes it back to the engine default. There is no CLI override for the
//       player-document store path (see WorldProfileStore), so this proof backs up the REAL world/ subtree (the whole
//       directory, byte-for-byte) before every session and restores it in a finally — the real catalog is never destroyed.
//   storage [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The cloud-readiness proof, proven against the local backend only: (a) a fresh boot against the REAL
//       cleared local store asserts storage.status's honest baseline (tier local authoritative/cloud unwired,
//       identity declined, endpoint none, a present catalog revision + version token), then the cheapest
//       revision-bumping verb (profile.set speed 7 1) is asserted to bump the revision, change the version token, and
//       flip dirty on; the on-disk split layout (world/player.json + world/profiles/*.json + world/local.json) is
//       asserted present; (b) a relaunch against the same store asserts storage.status reports the same persisted
//       revision; (c) a boot with --user-id <a valid oid Guid> asserts the "identity explicit override" echo; (d) a
//       boot with --user-id not-a-guid asserts the declining echo. Backs up + restores the REAL world/ subtree
//       exactly like the bindings proof — the real catalog is never destroyed.
//   expo-author [--no-build] [--width W] [--height H] [--exit-after-seconds N] [--out PATH]
//       Regenerates the second world reproducibly. Boots a baked-default Puck.World, feeds the checked-in
//       scripts/expo-world.txt authoring session (a new kit row + retunings + a table policy, a warmer four-pillar scene,
//       staggered spawns, three asset-free screens) over stdin, then world.save-s to Assets/worlds/expo.world.json
//       (--out overrides) — the trailing save FOLDS the live render levers + census into the document. The
//       artifact and this script are the checked-in, reproducible pair; NEVER hand-edit the JSON.
//   record [--no-build] [--width W] [--height H] [--seconds S] [--out PATH]
//       Native-capture proof. Boots Puck.World (a real GPU window — the tap reads each captured
//       frame back to CPU pixels through the SDF engine, so a live present surface is required), asserts capture.status
//       reads idle, capture.start arms a RecordingSession (echoing the negotiated codec and any device declines — a
//       mic privacy denial is a PASS path), lets ~S seconds of the autonomous crowd move, capture.stop finalizes, and
//       then walks the produced container: the EBML doc type matches the negotiated codec (webm for AV1, matroska for the
//       H.264 fallback), the audio track is present (loopback in a headless-ish run may be silence — track presence, not
//       loudness, is asserted), a video track is present when video negotiated, the file is non-trivial, and capture.status
//       reads idle again. --out copies the artifact somewhere for a real player to open. Asserts the overlay row is
//       present in the recording document used (the capture-only text).
//   ui-floor [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The unified-overlay proof, run on BOTH backends (Direct3D 12 first, then --backend vulkan). Each session
//       boots, waits for the console, then world.screenshot-s three composed frames over the outermost-decorator
//       capture chain: (a) console panel ON — its stage region must be visibly SCRIM-DARKENED versus (b) a
//       world.console off control shot (mean-luminance drop, robust against the moving world beneath); (c) a
//       deliberately invalid mutation (world.kit.remove on the defaultSeatKit) must surface as the danger-hued
//       TOAST — asserted as a danger-red pixel population in the toast strip that the control shot lacks — beside
//       its loud '[world.mutation rejected: ...]' stderr line. Decodes the engine's own PNGs (filter-0 RGBA)
//       inline; no image dependency.
//   editor-mode [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The editor-mode proof, run on BOTH backends like ui-floor. Each session boots, asserts the mode
//       round trip over stdin (editor.enter/status/exit echoes; the seat's active binding GROUP flips play→editor
//       and back — a pointer-level switch, editor.status echoes group= + page=; player.control reads idle while
//       editing and the prior source after), asserts THE FIVE CHORD PAGES end to end from data
//       (player.signal synthesizes the trigger sweeps; the held chord turns the seat's active page — resting, LT,
//       RT, LT-then-RT, and the reverse RT-then-LT all resolving DISTINCTLY; a session-rebind chord row binds via player.bind chord:m+m and
//       echoes in player.bindings; the resting-page and undeclared-modifier rules reject loudly at the wire — the
//       one-meaning rule and the press latch across group flips are engine-gated in Puck.Post's binding-page
//       stage), asserts the CAMERA in pixels
//       (console panel off; a screenshot after editor.cam.pose must differ decisively from the seeded shot, while
//       the seeded shot ~= the pre-enter chase shot and the post-exit shot ~= it again — no pose pop on either
//       edge), then asserts the diversion honestly: the avatar drives again after exit (player.run + where delta),
//       two idle where samples match exactly (nothing held leaked across the mode flip), and a tape STILL drives
//       the avatar while editing (the player.control idle contract — script outranks idle). The sole-editor LAYOUT
//       policy is asserted positively: a second seat joins, seat 1 enters the editor, and the 50%→70% split seam
//       band must repaint. The roster is pinned to seat 1 first (dev-machine pads auto-seat extra players). Decodes
//       the engine's own PNGs inline like ui-floor.
//   editor-cameras [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The camera live-apply proof, run on BOTH backends like editor-mode. Each session boots the
//       baked default (two declared View screens → two registered camera views, witnessed by world.view-refresh's
//       count echo) and asserts: (a) an unanchored camera's pose/aim edit applies LIVE — the '[world.camera: ... pose
//       updated live]' reconcile line, and a stale "applies at next boot" narration must NOT appear; (b) the
//       entity-anchored camera's pose edit takes the same live lane; (c) a NEW camera row + a screen re-point (View→View) binds the
//       new camera and releases the orphaned one, the pool count holding; (d) a dimension change recreates the
//       offscreen view ('recreated live (WxH)'); (e) THE VIEW→NONE TRANSITION — re-sourcing the screen View→None unbinds
//       the slot AND releases the camera registration (view-refresh count drops, world.screens reads none/unbound —
//       no stale offscreen render); (f) world.camera.remove of the now-unreferenced row applies document-side.
//       The document side of every claim is read off the world.cameras TABLE, not off a narration string: the two
//       boot rows must parse (anchor keyword, a rig token from the closed chase|firstPerson|orbit|lookAt|dolly
//       vocabulary, render dimensions), the live 'birdseye' add must APPEAR in the table, and a world.undo must
//       take it back out. A third section boots EVERY shipped world and reads its camera table the same way,
//       requiring the segment count to match world.status's declared count — the instrument the camera
//       re-encoding across the shipped worlds never had.
//   editor-edit [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The selection/manipulation proof, run on BOTH backends like editor-mode. Each session pins the roster
//       (player.leave 2..4) AND the census (world.population 0 — a static world so pixel diffs read the highlight,
//       not the crowd), enters the editor, and asserts: (a) editor.place authors one scene row = EXACTLY one
//       world.status dirty increment; (b) THE HEADLINE — a grab + multi-step drag produces ZERO wire traffic (dirty
//       unchanged mid-drag) and release commits EXACTLY one more journal entry with the position moved (editor.status
//       echoes the committed center); the coalescing is driven over editor.drag, the typed twin of stick motion (the
//       same pending-row channel the analog latch feeds — the latch itself needs a physical pad and is hand-verified);
//       (c) world.undo restores the pre-drag position; (d) the look-ray pick names the aimed row (camera posed at
//       boulder-2, editor.pick echoes it); (e) the selection highlight is VISIBLE — select/deselect screenshots over a
//       static central band differ decisively while a deselected control pair does not; (f) capacity honesty —
//       flooding placements past the authoring headroom hits the loud '[world.mutation rejected: ...]' envelope line,
//       a further placement leaves dirty UNCHANGED, and the rejection surfaces as the danger-hued toast;
//       (g) every editor-local typed float surface rejects NaN/Infinity loudly; (h) exit or seat
//       departure mid-drag drops the pending row AND the selection (re-entry/rejoin starts clean, the abandoned drag
//       is uncommittable, dirty unchanged); (i) a frozen released preview resolves independently on ITS OWN result: apply
//       narrates 'retired: applied', a revoked-grant rejection batched WITH an unrelated console mutation narrates
//       'retired: rejected' (never an apply/deadline retire — the unrelated delivery leaves the preview alone) and
//       the row snaps back to its document pose; (j) the candidate ring is bounded and narrated (cap 16
//       engages near the flooded scene, an empty ring far away, editor.status carries the policy). Decodes the
//       engine's own PNGs inline like ui-floor. A second NARROW pair of sessions (640x480, four seats, two editors)
//       is the seat-clip proof: the seat-1 HUD paints inside its own 320px viewport and does NOT bleed past the
//       2x2 seam into seat 2 (control-bounded pixel bands on both backends).
//   placements [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The creations/placements proof, run on BOTH backends like editor-mode. Each session pins the roster
//       (player.leave 2..4) and census (world.population 0), aims the editor camera at empty grass (so the ONLY pixel
//       motion in the asserted band is the placement under test), and asserts: (a) editor.import of a committed
//       PROOF-AUTHORED probe creation (Demo content never ships as World content — the proof owns
//       its art) crosses the strict canonicalizer and lands as EXACTLY one UpsertCreation journal entry; (b) the STAMP is visible — editor.place'ing the imported creation repaints a static central
//       band decisively versus a control pair's noise floor; (c) THE HASH PIN — world.creation.set with a corrupted
//       hash rejects loudly naming the canonical sha256, dirty unchanged (and the load-time half is covered by the
//       validator: a tampered saved file falls back loudly at boot); (d) the drag channel works on placements —
//       grab + multi-step editor.drag moves the dirty counter NOT AT ALL mid-drag and release commits EXACTLY one
//       more journal entry ('retired: applied'); (e) world.undo restores the pre-drag position (editor.select echoes
//       the original coordinates); (f) RemoveCreation with live placements rejects loudly (the no-cascade ruling);
//       (g) the proof-authored ANIMATED probe (4 frames) walks its timeline — two shots 0.7 s apart over its band
//       differ decisively while a static-control pair does not; (h) capacity honesty — flooding placements past
//       the reserved headroom hits the loud '[world.mutation rejected: ...] ... exceed the probed render envelope'
//       ceiling and a further placement leaves dirty unchanged; (i) THE OUROBOROS WITH CREATIONS — world.save of the
//       furnished world, a relaunch --world <that file>, and a second save compare byte-identical (the inline-
//       canonical embeds and the world.save hash recompute are byte-stable). Decodes the engine's own PNGs
//       inline like ui-floor.
//   population [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The crowd-shape proof (looks, kit hot-swap, spawn policy, inhabitation), one windowed session on the default
//       backend. It PINS its stage first — seat 1 alone, every entity on the runner kit, a 24-strong idle census — so no
//       count below is inherited from boot, and asserts: (a) THE LOOK TABLE both ways — two authored rows plus a
//       `table stocky stocky tiny` assignment make world.looks read the exact 17/8 split the cycle implies (a number the
//       hash policy it replaced does not produce), and a world.look.tune of the majority row's render SCALE repaints the
//       crowd decisively over a static control pair (the pixel half — a look table nothing renders passes every count and
//       changes no frame), with the no-cascade removal of a still-referenced row rejecting loudly and the journal holding;
//       (b) THE KIT HOT-SWAP — a body warped OFF its spawn footprint FIRST (a body still at its spawn would satisfy
//       'pose survives' even against a rebuild), then a live world.kit.tune, then the pose read back bit-for-bit;
//       (c) THE SPAWN POLICY both halves — world.population.spawn points leaves the standing body exactly where it is,
//       and the next activation (census drained to 0 and readmitted) lands ON the authored spawn point, tens of units from
//       the phyllotaxis default; (d) INHABITATION END TO END — an imported proof-authored creation placed, its kit given
//       an attend flavor, world.placement.inhabit claiming a body at the highest free slot (world.inhabitants names it),
//       the ATTEND producer measurably closing the range to seat 1, the derived-face census plus a world.placement.face
//       override (one journal entry; an unknown face rejects loudly), detach freeing the slot, and world.undo
//       reconstructing the registration; (e) THE TWO REFUSAL LANES — an inhabit facet naming an undeclared kit is a
//       DOCUMENT refusal (loud, journal unchanged) while a claim against a table filled by a 124-peer census is a RUNTIME
//       admission refusal (the document applies; the server says loudly that no slot was free and nobody is admitted).
//       Every round settles wire.errors against its own deliberate refusals and the session ends asserting zero.
//   sculpt [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The creation sub-editor proof, on BOTH backends: sculpt a creation from nothing over stdin
//       (editor.sculpt.* primitives, palette, a chain/IK pose), commit it as ONE canonicalized UpsertCreation,
//       stamp it at the bench origin, and demand PIXEL IDENTITY between the workbench preview and the committed
//       stamp. Then the two undo domains assert as distinct, a 2-frame timeline's stamp ANIMATES, re-sculpting
//       live-refreshes the placement, an imported carrier's cameras/behavior/extensions survive a model
//       round-trip, the easel verb wires a bench camera onto a screen row, and the furnished save reloads stably.
//   audio [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The audio document-side proof. Session A (baked default) authors the furnished audio set over stdin —
//       patches/tune through the HASH-PIN handshake (a bogus hash rejects loudly NAMING the canonical sha256, which
//       the proof then submits — the pin proven and satisfied in one gesture), every speaker $type (fixed stereo
//       pair, a bed, an entityLeaf-anchored row on a published role token, a placement-anchored row), a scene-row
//       emission facet, the audio defaults, and a sound-bearing proof-authored creation placed — asserting the
//       journal dirty counter at every step and the audio.emitters derivation listing. Then the VALIDATOR REJECTION
//       TABLE (unknown patch/tune ids, an undeclared screen, a bad leaf token, an unknown placement anchor, the bed
//       radius rule, the gain ceiling, a bogus listener policy, an emission facet naming no patch — each loud, dirty
//       unchanged), the NO-CASCADE GUARDS (RemoveTune/RemovePatch naming their dependents; RemovePlacement while a
//       speaker anchors to it), an undo round (the departed speaker leaves the derivation), and a grants round
//       (revoking console mutate section:speakers denies the mutation loudly; re-granting restores it). world.save
//       compacts; session B relaunches --world <the saved file>, PINS the fresh-boot audio.emitters listing exactly
//       (stable ids in document order), and a second save byte-compares — the ouroboros with audio sections.
//       The cue/speaker/volume rounds ride the same sessions: THE CUE TABLE (authored as data, its validator table, the producer
//       lanes — an applied mutation's at-site chime, a grant denial, gait-derived footsteps under player.run, the
//       binder's screen.fault lane — each asserted through speaker.state's live transient tail), speakers through
//       the editor (select/grab/drag/release/undo + the editor.speaker.* numeric twins, dirty-count disciplined),
//       the world.volume session lever (document read → lever engage → 'audio' drift → the save FOLDS it into
//       audio.masterGain), and the ouroboros now carries the cue table + folded volume — with the rebooted cue
//       table still FIRING (the behavioral half). speaker.state itself is exercised throughout.
//   collision [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The solidity/contact + host-section proof (Arc 1, Arc 2, Beat A), run on BOTH backends because the host
//       read's RESOLVED column names the one actually hosting. Every claim is measured on a pose or a counter, never on
//       an echo alone: (a) SOLIDITY IS DATA — a proof-authored slab across the run lane stops the body dead (z 11.13
//       against a face at 11.5) and zeroes its planar speed while the tape still commands full forward, and the
//       IDENTICAL segment carries it to z 26 once world.scene.solid drops the facet from the same row (the world.contacts
//       census tracking the row count both ways); (b) THE RESPONSE TABLE SHAPES MOMENTUM — the same 0.6 s segment over
//       empty ground travels 5.44 u through an empty table and 0.55 u through an authored engageRate-1.5 row: the DELTA
//       is the feature; (c) THE KIT FACETS — a volumeless kit (world.kit.collider none) is not solved at all and walks
//       through the solid wall, world.kit.model grounded/free flips whether the up channel integrates (y 0.00 vs 9.00 on
//       one player.fly segment), and world.collision.off/on drops and restores solving while the rows keep their facets;
//       (d) THE FIELD PROVIDER — world.collision.probe answers three different gradients around a floating solid sphere
//       (+Y at the pole, +Z and +X on the flanks) and a walk over it sheds 12.9 u of height while the RADIUS from its
//       center holds to 0.00 u — the planetoid signature the analytic provider cannot produce (the identical pose and
//       segment diverge by 25 u); (e) THE REJECTION TABLE — an out-of-range slope, a gradient under the analytic
//       provider, a probe with no field, a capsule shorter than its diameter, and the two host-tune grammars, each loud;
//       (f) THE HOST SECTION — the three-column read (document 1280x800 vs the CLI-resolved size vs the live levers), a
//       tune that moves the DOCUMENT and so marks 'host' drift, a world.undo that clears it, the section's grant denial,
//       and the SERIALIZATION PIN: world.save writes presentMode PascalCase ("Mailbox") beside the lowercase
//       backend/surfaceFormat tokens, and a relaunch --world <that file> reads it back as mailbox (the reader half).
//   expodoc [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The second world + session write-back proof: (a) --world expo.world.json boots the loud
//       "[world] definition: <expo path>" line; (b) a distinguishing world.status fact — expo's kit/screen counts differ
//       from the default's (kits 6 screens 3 vs kits 5 screens 5), a visibly different game with zero code; (c) the
//       write-back SLICE not covered by the mutate proof — a live SESSION lever (the world.population peer-source
//       default) is flipped to idle, the world is saved to a temp copy, and a relaunch --world <that copy> boots with
//       that folded behavior (the saved JSON carries defaultPeerSource, world.population echoes it) while the
//       networkPlayers admission cap stays durable (R-C: a census raise is transient, never folded); (d) the third fold dimension positively — a
//       runtime screen.insert of a real ROM makes world.status name the 'screens' drift and world.save fold the live
//       machine into that screen row's Machine source. Expo's own ouroboros is covered by worlddoc.
//   replay [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The deterministic-replay proof (the replay.* family). Two sessions, each PINNING its stage first — census 0 and
//       seat 1 alone, so the live world is still AT its definition boot image and a boot-anchored capture is possible.
//       Session A asserts: (a) an idle boot-anchored capture MATCHes and replay.status counts the ticks accumulating;
//       (b) a DRIVEN boot-anchored capture (a 1-second forward tape, then two seconds of settling so the sampled tail
//       is a resting pose) MATCHes, its body displacement measured on player.where, and its tail hash DIFFERS from the
//       idle one — an offline drive that dropped the recorded stream could not have produced both; (c) the documented
//       fidelity boundary: a MID-SESSION capture honestly MISMATCHes, with its recorded side still the live tail the
//       driven round left (the mismatch is the boot-image start, not a re-drive compared against itself); (d) THE
//       DISCRIMINATION — a tape doctored by one flipped byte (byte 8, the low byte of the stored reference hash)
//       MISMATCHes beside a byte-for-byte control copy that MATCHes, both re-drives recomputing the SAME replayed
//       tail; (e) FOUR structurally broken tapes (not a recording, a two-byte stub, a retired shape, and doctored
//       length prefixes) plus one absent name are each named and refused AND THE HOST STILL ANSWERS — the exit-82
//       crash's regression test; (f) the tape lifecycle off replay.list (a cancelled recording leaves no file) plus
//       the busy/not-recording/path-escaping-name refusals. Session B re-runs session A's identical prefix in a FRESH
//       process, and the two live tail hashes must be BIT-IDENTICAL — the determinism claim itself, asserted as a
//       number two processes independently sampled, never against a stored baseline (R18).
//   screen-sources [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The rest of the screen.* family — what the screens proof (insert/eject/peek/state around one cartridge) leaves
//       untouched. One pinned session asserts: (a) SOURCE BINDING measured on the slot, never on the setter's echo —
//       screen.eject refuses a slot with no live producer and succeeds after screen.desktop and screen.capture bind
//       one (the webcam is driven too, its outcome NAMED and its settle count following the branch actually taken,
//       since a machine may have no camera); (b) THE MAGAZINE authored as data (a whole-row world.screen.set = exactly
//       one journal entry) then driven — screen.state's entry=N/M moves next/prev/absolute and WRAPS, and the selected
//       source is really APPLIED (the `none` entry drops the slot to unbound, the wrapped-to view binds it again), with
//       an out-of-range entry and a magazineless screen refused and the selector unmoved; (c) THE ONE-LIVE-CONSOLE
//       CEILING — a second declared console source is rejected naming both indices, the journal unchanged; (d) THE
//       LIVE DEVICE SWAP — screen.options walks a RUNNING machine dmg->cgb->agb, proven on the machine's own options
//       read-back AND on the cartridge's work-RAM liveness counter still advancing across each swap (no reboot, no
//       lost progress), with an unknown token refused and the running model untouched; (e) THE CABLE GROUP — a link
//       over two running machines is recorded DORMANT with today's exact reason (the queued gaming-brick host has no
//       live-link path; the reason is asserted verbatim so the day it lands this check demands the live half),
//       membership carried in screen.state on both members, unlink severing it, and the four link refusals
//       (already-linked member, duplicate member, one-member arity, absent group). The cartridge is proof-authored
//       (ScreensProof's joypad-echo ROM) — no ROM ships with the world.
//   wire [--no-build] [--width W] [--height H] [--exit-after-seconds N]
//       The console-wire contract proof — the three things every OTHER suite silently rests on, run in one
//       --world baked session (the in-code document declares no solidity, so the scripted run lane is clear
//       ground) plus four boot-only launches. (a) world.wait, asserted BEHAVIOURALLY: the same
//       drive-then-read burst run twice with a wait must land on the same pose bit-for-bit and a real
//       distance from the start, while the identical burst WITHOUT the wait travels under a quarter as far —
//       the stable-vs-racy contrast is the check, so a gate that stopped holding collapses the waited rounds
//       onto the control and reddens both halves; the release-tick echo must also equal the requested span
//       past the tick the wire was on. (b) WorldJsonPayload, the one inline-JSON parse seam: four
//       union-taking verb families (world.look.set, world.screen.set, world.camera.set, world.scene.row.set)
//       x four discriminator malformations (absent, unknown, duplicate, misplaced $type) — sixteen payloads
//       that must each be NAMED and REFUSED with the host still answering afterwards, because an absent
//       discriminator once threw NotSupportedException past every catch (JsonException) and took the process
//       down. (c) The loud boot assertions: a bogus --world and a bogus --recording path each exit 1 naming
//       themselves, a real --world path boots, and --world baked boots the in-code document by name. Every
//       round settles wire.errors — zero for (a), exactly sixteen for (b), then cleared.
//
// Puck.World simulation is fixed-point and host-ticked. These are World-owned live proofs (paced console journeys and
// closed-form tableaux), while the shared fixed-step/snapshot/numerics contracts are enforced by Puck.Post Tier A.
//
// Zero NuGet dependencies; invariant culture everywhere numbers are formatted/parsed; the child
// process is never orphaned (Ctrl+C + ProcessExit + try/finally all kill it).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

return ProofApp.Run(args: args);

// ============================================================================================
// Entry + argument plumbing
// ============================================================================================

static class ProofApp {
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The full 6DOF where echo: p<N> pos=(x, y, z) yaw=ddd° pitch=ddd° roll=ddd°. The three-number
    // pos never matches the roster's planar two-number world.players glance, so only the sweep is captured.
    public static readonly Regex WhereEcho = new(
        options: RegexOptions.Compiled,
        pattern: @"p(\d+) pos=\((-?[0-9.]+), (-?[0-9.]+), (-?[0-9.]+)\) yaw=(\d+)\D+pitch=(\d+)\D+roll=(\d+)");

    public static int Run(string[] args) {
        if (args.Length == 0) {
            Console.Error.WriteLine(value: "usage: proof.cs <generate|run|compare> [options]  (run --help for subcommands)");

            return 2;
        }

        var subcommand = args[0];
        var opts = new ArgMap(args: args.AsSpan(start: 1).ToArray());

        try {
            return Guarded(code: subcommand switch {
                "generate" => Generators.RunGenerate(opts: opts),
                "run" => Feeder.RunFeeder(opts: opts),
                "compare" => Comparer.RunCompare(opts: opts),
                "screens" => ScreensProof.RunScreens(opts: opts),
                "worlddoc" => WorldDocProof.RunWorldDoc(opts: opts),
                "mutate" => MutateProof.RunMutate(opts: opts),
                "grants" => GrantsProof.RunGrants(opts: opts),
                "bindings" => BindingsProof.RunBindings(opts: opts),
                "storage" => StorageProof.RunStorage(opts: opts),
                "expo-author" => ExpoProof.RunExpoAuthor(opts: opts),
                "expodoc" => ExpoProof.RunExpoDoc(opts: opts),
                "record" => RecordProof.RunRecord(opts: opts),
                "ui-floor" => UiFloorProof.RunUiFloor(opts: opts),
                "editor-mode" => EditorModeProof.RunEditorMode(opts: opts),
                "editor-edit" => EditorEditProof.RunEditorEdit(opts: opts),
                "editor-cameras" => EditorCamerasProof.RunEditorCameras(opts: opts),
                "placements" => PlacementsProof.RunPlacements(opts: opts),
                "population" => PopulationProof.RunPopulation(opts: opts),
                "sculpt" => SculptProof.RunSculpt(opts: opts),
                "audio" => AudioProof.RunAudio(opts: opts),
                "collision" => CollisionProof.RunCollision(opts: opts),
                "replay" => ReplayProof.RunReplay(opts: opts),
                "screen-sources" => ScreenSourcesProof.RunScreenSources(opts: opts),
                "wire" => WireProof.RunWire(opts: opts),
                "--help" or "-h" or "help" => PrintHelp(),
                _ => Fail(message: $"unknown subcommand '{subcommand}' (expected generate|run|compare|screens|screen-sources|worlddoc|mutate|grants|bindings|storage|expo-author|expodoc|record|ui-floor|editor-mode|editor-edit|editor-cameras|placements|population|sculpt|audio|collision|replay|wire)"),
            });
        }
        catch (ArgException ex) {
            Console.Error.WriteLine(value: $"[proof] argument error: {ex.Message}");

            return 2;
        }
    }

    // Fails an otherwise-green run in which a driven session never settled `wire.errors`. A refused step does not fail
    // on its own — it manifests as a timeout only if the suite happened to await something that step would have caused,
    // so a refused setup verb before an assertion satisfied by other means reads as a pass. The settle is what turns
    // that silence into a verdict, and this is the one place every suite funnels through, so the obligation lands on
    // all of them at once — including any session a future suite adds.
    static int Guarded(int code) {
        var unsettled = OutputCollector.Sessions.Where(predicate: session => (session.Driven && !session.Settled)).ToList();

        if (unsettled.Count == 0) {
            return code;
        }

        var labels = string.Join(separator: ", ", values: unsettled.Select(selector: session => session.Label).Order(comparer: StringComparer.Ordinal).Distinct(comparer: StringComparer.Ordinal));

        Console.Error.WriteLine(value: $"[proof]   FAIL wire-settle: {unsettled.Count} driven session(s) ended without settling `wire.errors` ({labels}) — the wire's own refused-line count is the only place a handler's refusal, a denied grant, or a deferred rejection is visible, so every pass in those sessions is unproven.");

        return ((code == 0) ? 1 : code);
    }

    static int PrintHelp() {
        Console.WriteLine(value: "proof.cs — Puck.World proof suite");
        Console.WriteLine(value: "  generate --kind parade|flood|flight|hop|expo [--population N] [--seed S] [--out PATH]");
        Console.WriteLine(value: "  run [--corpus PATH | --kind K] [--headless] [--loop] [--quality low|medium|high]");
        Console.WriteLine(value: "      [--width W] [--height H] [--no-build] [--tolerance T] [--yaw-tolerance Y]");
        Console.WriteLine(value: "      [--min-fps FPS] [--log PATH] [--world-arg PATH]  (default min-fps 60; 0 disables it)");
        Console.WriteLine(value: "  compare --reference A --candidate B [--tolerance T] [--yaw-tolerance Y]");
        Console.WriteLine(value: "  screens [--width W] [--height H] [--no-build] [--rom PATH]");
        Console.WriteLine(value: "  worlddoc [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  mutate [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  grants [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  bindings [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  storage [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  expo-author [--no-build] [--width W] [--height H] [--exit-after-seconds N] [--out PATH]");
        Console.WriteLine(value: "  expodoc [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  record [--no-build] [--width W] [--height H] [--seconds S] [--out PATH]");
        Console.WriteLine(value: "  ui-floor [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  editor-mode [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  editor-edit [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  editor-cameras [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  placements [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  population [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  sculpt [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  audio [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  collision [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  replay [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  screen-sources [--no-build] [--width W] [--height H] [--exit-after-seconds N]");
        Console.WriteLine(value: "  wire [--no-build] [--width W] [--height H] [--exit-after-seconds N]");

        return 0;
    }

    public static int Fail(string message) {
        Console.Error.WriteLine(value: $"[proof] {message}");

        return 2;
    }

    // Invariant number formatting — the ONE place doubles become text: a period decimal separator, no thousands
    // separator, and up to `format`'s digit count with trailing zeros trimmed, so output is stable across locales
    // and diffable byte-for-byte between runs.
    public static string F(double value, string format = "0.###") {
        return value.ToString(format: format, provider: Inv);
    }

    // The script's own source path at compile time, so the repo root resolves regardless of CWD.
    public static string ScriptPath([CallerFilePath] string path = "") {
        return path;
    }

    // repoRoot = .../src/Puck.World/scripts/proof.cs -> up three dirs. Falls back to CWD-walk if the
    // caller path is unavailable (some publish scenarios), searching for src/Puck.World/Puck.World.csproj.
    public static string RepoRoot() {
        var scriptPath = ScriptPath();

        if (!string.IsNullOrEmpty(value: scriptPath) && File.Exists(path: scriptPath)) {
            var scriptsDir = Path.GetDirectoryName(path: scriptPath)!;

            return Path.GetFullPath(path: Path.Combine(path1: scriptsDir, path2: "..", path3: "..", path4: ".."));
        }

        var dir = new DirectoryInfo(path: Directory.GetCurrentDirectory());

        while (dir is not null) {
            if (File.Exists(path: Path.Combine(path1: dir.FullName, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj"))) {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
sealed class ArgException(string message) : Exception(message);

// A hand-rolled parser: "--key value" pairs plus boolean "--flag" switches.
sealed class ArgMap {
    static readonly HashSet<string> BooleanFlags = new(comparer: StringComparer.Ordinal) {
        "--headless", "--loop", "--no-build",
    };
    readonly Dictionary<string, string> m_values = new(comparer: StringComparer.Ordinal);
    readonly HashSet<string> m_flags = new(comparer: StringComparer.Ordinal);

    public ArgMap(string[] args) {
        for (var i = 0; (i < args.Length); i++) {
            var token = args[i];

            if (!token.StartsWith(comparisonType: StringComparison.Ordinal, value: "--")) {
                throw new ArgException(message: $"expected an option (got '{token}')");
            }

            if (BooleanFlags.Contains(item: token)) {
                _ = m_flags.Add(item: token);

                continue;
            }

            if ((i + 1) >= args.Length) {
                throw new ArgException(message: $"option '{token}' expects a value");
            }

            m_values[token] = args[++i];
        }
    }

    public bool Flag(string name) {
        return m_flags.Contains(item: name);
    }
    public string? Get(string name) {
        return (m_values.TryGetValue(key: name, value: out var value) ? value : null);
    }
    public string GetRequired(string name) {
        return (Get(name: name) ?? throw new ArgException(message: $"missing required option '{name}'"));
    }
    public int GetInt(string name, int fallback) {
        var raw = Get(name: name);

        if (raw is null) {
            return fallback;
        }

        return (int.TryParse(provider: ProofApp.Inv, result: out var value, s: raw, style: NumberStyles.Integer)
            ? value
            : throw new ArgException(message: $"option '{name}' expects an integer (got '{raw}')"));
    }
    public double GetDouble(string name, double fallback) {
        var raw = Get(name: name);

        if (raw is null) {
            return fallback;
        }

        return (double.TryParse(provider: ProofApp.Inv, result: out var value, s: raw, style: NumberStyles.Float)
            ? value
            : throw new ArgException(message: $"option '{name}' expects a number (got '{raw}')"));
    }
}

// ============================================================================================
// Corpus model + parser
// ============================================================================================

readonly record struct TimedCommand(double T, string Command, bool InLoop);
readonly record struct PoseExpect(int N, double X, double Y, double Z, int Yaw, int Pitch, int Roll);
readonly record struct BandExpect(int N, char Axis, double Lo, double Hi, string Class);
readonly record struct Separation(char Axis, string ClassA, string ClassB, double MinGap);

// One timed pose-readback point: the player.where lines at time T, plus whatever assertions attach.
sealed class Sweep {
    public double T;
    public string Name = "final";
    public readonly List<PoseExpect> PoseExpects = new();
    public readonly List<BandExpect> BandExpects = new();
    public readonly List<Separation> Separations = new();

    public bool HasAsserts => ((PoseExpects.Count > 0) || (BandExpects.Count > 0) || (Separations.Count > 0));
}
sealed class Corpus {
    public readonly List<TimedCommand> Commands = new();
    public readonly List<Sweep> Sweeps = new();
    public double CycleEnd;
    public double LoopStartT = double.MaxValue;
    public bool HasLoop;
}
static class CorpusParser {
    static readonly Regex TimedLine = new(options: RegexOptions.Compiled, pattern: @"^@([0-9.]+)\s+(.+)$");
    static readonly Regex ExpectLine = new(
        options: RegexOptions.Compiled,
        pattern: @"^#expect\s+p(\d+)\s+(-?[0-9.]+)\s+(-?[0-9.]+)\s+(-?[0-9.]+)\s+(\d+)\s+(\d+)\s+(\d+)$");
    static readonly Regex BandLine = new(
        options: RegexOptions.Compiled,
        pattern: @"^#expect-band\s+p(\d+)\s+([xyz])\s+(-?[0-9.]+)\s+(-?[0-9.]+)(?:\s+(\S+))?$");
    static readonly Regex SeparationLine = new(
        options: RegexOptions.Compiled,
        pattern: @"^#expect-separation\s+([xyz])\s+(\S+)\s+(\S+)\s+(-?[0-9.]+)$");
    static readonly Regex SweepAtLine = new(options: RegexOptions.Compiled, pattern: @"^#sweep-at\s+([0-9.]+)(?:\s+(\S+))?$");
    static readonly Regex CycleEndLine = new(options: RegexOptions.Compiled, pattern: @"^#cycle-end\s+([0-9.]+)");

    static double D(string text) {
        return double.Parse(provider: ProofApp.Inv, s: text, style: NumberStyles.Float);
    }
    static int I(string text) {
        return int.Parse(provider: ProofApp.Inv, s: text, style: NumberStyles.Integer);
    }

    public static Corpus Parse(IEnumerable<string> lines) {
        var corpus = new Corpus();
        var loopSeen = false;

        // Assertions attach to the OPEN sweep (from a #sweep-at). Before the first #sweep-at they land in
        // this default bucket, adopted by the synthesized final sweep (parade/flight/flood compatibility).
        var defaultPoses = new List<PoseExpect>();
        var defaultBands = new List<BandExpect>();
        var defaultSeparations = new List<Separation>();
        Sweep? current = null;

        foreach (var raw in lines) {
            var line = raw.Trim();

            if (line.Length == 0) {
                continue;
            }

            var timed = TimedLine.Match(input: line);

            if (timed.Success) {
                var t = D(text: timed.Groups[1].Value);

                corpus.Commands.Add(item: new TimedCommand(T: t, Command: timed.Groups[2].Value.Trim(), InLoop: loopSeen));

                if (loopSeen && (t < corpus.LoopStartT)) {
                    corpus.LoopStartT = t;
                }

                continue;
            }

            var sweepAt = SweepAtLine.Match(input: line);

            if (sweepAt.Success) {
                current = new Sweep {
                    T = D(text: sweepAt.Groups[1].Value),
                    Name = (sweepAt.Groups[2].Success ? sweepAt.Groups[2].Value : "sweep"),
                };
                corpus.Sweeps.Add(item: current);

                continue;
            }

            var expect = ExpectLine.Match(input: line);

            if (expect.Success) {
                var pose = new PoseExpect(
                    N: I(text: expect.Groups[1].Value),
                    X: D(text: expect.Groups[2].Value),
                    Y: D(text: expect.Groups[3].Value),
                    Z: D(text: expect.Groups[4].Value),
                    Yaw: I(text: expect.Groups[5].Value),
                    Pitch: I(text: expect.Groups[6].Value),
                    Roll: I(text: expect.Groups[7].Value));

                (current?.PoseExpects ?? defaultPoses).Add(item: pose);

                continue;
            }

            var band = BandLine.Match(input: line);

            if (band.Success) {
                var bandExpect = new BandExpect(
                    N: I(text: band.Groups[1].Value),
                    Axis: band.Groups[2].Value[0],
                    Lo: D(text: band.Groups[3].Value),
                    Hi: D(text: band.Groups[4].Value),
                    Class: (band.Groups[5].Success ? band.Groups[5].Value : "default"));

                (current?.BandExpects ?? defaultBands).Add(item: bandExpect);

                continue;
            }

            var separation = SeparationLine.Match(input: line);

            if (separation.Success) {
                var sep = new Separation(
                    Axis: separation.Groups[1].Value[0],
                    ClassA: separation.Groups[2].Value,
                    ClassB: separation.Groups[3].Value,
                    MinGap: D(text: separation.Groups[4].Value));

                (current?.Separations ?? defaultSeparations).Add(item: sep);

                continue;
            }

            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "#loop-start")) {
                loopSeen = true;

                continue;
            }

            var cycleEnd = CycleEndLine.Match(input: line);

            if (cycleEnd.Success) {
                corpus.CycleEnd = D(text: cycleEnd.Groups[1].Value);
            }

            // Any other #... line is a comment.
        }

        corpus.HasLoop = loopSeen;

        // No explicit #sweep-at but there ARE player.where lines: synthesize ONE final sweep at the last
        // where time and hand it the default assertion bucket (the parade/flight/flood single-sweep shape).
        if (corpus.Sweeps.Count == 0) {
            var whereTimes = corpus.Commands
                .Where(predicate: c => c.Command.StartsWith(comparisonType: StringComparison.Ordinal, value: "player.where"))
                .Select(selector: c => c.T)
                .ToList();

            if ((whereTimes.Count > 0) || (defaultPoses.Count > 0)) {
                var sweep = new Sweep {
                    T = ((whereTimes.Count > 0) ? whereTimes.Max() : double.MaxValue),
                    Name = "final",
                };

                sweep.PoseExpects.AddRange(collection: defaultPoses);
                sweep.BandExpects.AddRange(collection: defaultBands);
                sweep.Separations.AddRange(collection: defaultSeparations);
                corpus.Sweeps.Add(item: sweep);
            }
        }
        else {
            // Explicit sweeps present but some assertions arrived before the first #sweep-at (unusual) — fold
            // them into the earliest sweep so nothing is silently dropped.
            if ((defaultPoses.Count > 0) || (defaultBands.Count > 0) || (defaultSeparations.Count > 0)) {
                var first = corpus.Sweeps.OrderBy(keySelector: s => s.T).First();

                first.PoseExpects.AddRange(collection: defaultPoses);
                first.BandExpects.AddRange(collection: defaultBands);
                first.Separations.AddRange(collection: defaultSeparations);
            }
        }

        corpus.Sweeps.Sort(comparison: (a, b) => a.T.CompareTo(value: b.T));

        if (corpus.CycleEnd <= 0.0) {
            corpus.CycleEnd = ((corpus.Commands.Count > 0) ? (corpus.Commands.Max(selector: c => c.T) + 1.0) : 0.0);
        }

        return corpus;
    }
}

// ============================================================================================
// Generators — one method per kind
// ============================================================================================

readonly record struct GenOptions(int Population, int Seed, double DurationSeconds, int ControlRate,
    double ArenaRadius, double CorrectionInterval);
static class Generators {
    // WorldBody defaults (profileless population stand-ins ride these) — the ONE source of the derived constants.
    public const double MoveSpeed = 4.0;
    public const double TurnSpeed = 2.5;

    public static int RunGenerate(ArgMap opts) {
        var kind = (opts.Get(name: "--kind") ?? "expo");
        var genOptions = new GenOptions(
            Population: Math.Clamp(opts.GetInt(fallback: 124, name: "--population"), 1, 124),
            Seed: opts.GetInt(fallback: 128, name: "--seed"),
            DurationSeconds: opts.GetDouble(fallback: 60.0, name: "--duration"),
            ControlRate: Math.Clamp(opts.GetInt(fallback: 5, name: "--control-rate"), 2, 20),
            ArenaRadius: opts.GetDouble(fallback: 40.0, name: "--arena"),
            CorrectionInterval: opts.GetDouble(fallback: 6.0, name: "--correction-interval"));

        var lines = Generate(kind: kind, o: genOptions);
        var outPath = opts.Get(name: "--out");

        if (outPath is not null) {
            File.WriteAllLines(contents: lines, path: outPath);
            Console.WriteLine(value: $"[generate] {kind} corpus written: {outPath} ({lines.Count} lines, population {genOptions.Population})");
        }
        else {
            foreach (var line in lines) {
                Console.WriteLine(value: line);
            }
        }

        return 0;
    }
    public static List<string> Generate(string kind, GenOptions o) {
        return kind switch {
            "parade" => Parade(population: o.Population),
            "flood" => Flood(o: o),
            "flight" => Flight(population: o.Population),
            "hop" => Hop(population: o.Population),
            "expo" => ExpoBuilder.Build(population: o.Population, seed: o.Seed),
            _ => throw new ArgException(message: $"unknown kind '{kind}' (expected parade|flood|flight|hop|expo)"),
        };
    }

    // --- shared math (the codebase-wide dir(yaw) = (-sin yaw, -cos yaw) convention) ---

    // Yaw (degrees, [0,360)) whose facing direction is the given unit direction: yaw = atan2(-dx, -dz).
    public static double YawDegrees(double dirX, double dirZ) {
        var degrees = (Math.Atan2(x: -dirZ, y: -dirX) * (180.0 / Math.PI));

        return ((degrees < 0.0) ? (degrees + 360.0) : degrees);
    }

    // An angle in degrees normalized to [0,360) then rounded — matches the where echo's CompassDegrees + '0'.
    public static int CompassInt(double degrees) {
        var norm = (((degrees % 360.0) + 360.0) % 360.0);

        return (((int)Math.Round(a: norm)) % 360);
    }

    static void Emit(List<string> lines, double t, string command) {
        lines.Add(item: $"@{ProofApp.F(format: "0.0#", value: t)} {command}");
    }

    // ----------------------------------------------------------------------------------------
    // PARADE — the byte-exact machinery proof.
    // ----------------------------------------------------------------------------------------
    public static List<string> Parade(int population) {
        var lines = new List<string>();

        const double marchAt = 4.0, marchSeconds = 4.0;
        const double ringAt = 10.0, ringSeconds = 8.0;
        const double teamsAt = 20.5, teamsSeconds = 5.0;
        const double evidenceAt = 27.0, sweepAt = 28.0, endAt = 29.0;

        lines.Add(item: "# puck.world corpus v1 — the 128-player STDIN proof (generated by proof.cs generate --kind parade)");
        lines.Add(item: $"# population {population}; one cycle spans {ProofApp.F(format: "0.0", value: endAt)}s");
        Emit(command: "world.timing on", lines: lines, t: 0.0);
        Emit(command: $"world.population {population} idle", lines: lines, t: 0.2);
        lines.Add(item: "#loop-start");

        // Wave A: THE MARCH — 12-wide phalanx, rows every 2 u from z=-14, marching +Z (yaw 180).
        const int columns = 12;

        for (var i = 0; (i < population); i++) {
            var n = (i + 5);
            var column = (i % columns);
            var row = (i / columns);
            var rowWidth = Math.Min(val1: columns, val2: (population - (row * columns)));
            var x = ((column - ((rowWidth - 1) / 2.0)) * 2.0);
            var z = (-14.0 - (2.0 * row));

            Emit(lines, marchAt, $"player.warp {ProofApp.F(x)} {ProofApp.F(z)} {n}");
            Emit(command: $"player.face 180 {n}", lines: lines, t: marchAt);
            Emit(lines, marchAt, $"player.run 1 0 0 {ProofApp.F(marchSeconds)} {n}");
        }

        Emit(command: "world.fps", lines: lines, t: ((marchAt + marchSeconds) + 1.0));

        // Wave B: THE RING — radius 22, tangent heading, orbital turn = -(v/r)/turnSpeed.
        const double ringRadius = 22.0;
        var orbitTurn = -((MoveSpeed / ringRadius) / TurnSpeed);

        for (var i = 0; (i < population); i++) {
            var n = (i + 5);
            var phi = (((i * 2.0) * Math.PI) / population);
            var x = (ringRadius * Math.Cos(d: phi));
            var z = (ringRadius * Math.Sin(a: phi));
            var yaw = YawDegrees(dirX: -Math.Sin(a: phi), dirZ: Math.Cos(d: phi));

            Emit(lines, ringAt, $"player.warp {ProofApp.F(x)} {ProofApp.F(z)} {n}");
            Emit(lines, ringAt, $"player.face {ProofApp.F(format: "0.##", value: yaw)} {n}");
            Emit(lines, ringAt, $"player.run 1 0 {ProofApp.F(orbitTurn)} {ProofApp.F(ringSeconds)} {n}");
        }

        Emit(command: "world.fps", lines: lines, t: ((ringAt + ringSeconds) + 1.0));

        // Wave C: THE CONVERGENCE — four wedge teams at 45/135/225/315°, radius 30 -> 10 (closed-form).
        const double outerRadius = 30.0;
        var innerRadius = (outerRadius - (4.0 * teamsSeconds));
        var perTeam = (int)Math.Ceiling(a: (population / 4.0));
        var expects = new List<string>();

        for (var i = 0; (i < population); i++) {
            var n = (i + 5);
            var team = (i / perTeam);
            var member = (i % perTeam);
            var teamSize = Math.Min(val1: perTeam, val2: (population - (team * perTeam)));
            var angleDegrees = ((45.0 + (90.0 * team)) + ((member - ((teamSize - 1) / 2.0)) * 2.0));
            var angle = (angleDegrees * (Math.PI / 180.0));
            var x = (outerRadius * Math.Cos(d: angle));
            var z = (outerRadius * Math.Sin(a: angle));
            var yaw = YawDegrees(dirX: -Math.Cos(d: angle), dirZ: -Math.Sin(a: angle));

            Emit(lines, teamsAt, $"player.warp {ProofApp.F(x)} {ProofApp.F(z)} {n}");
            Emit(lines, teamsAt, $"player.face {ProofApp.F(format: "0.##", value: yaw)} {n}");
            Emit(lines, teamsAt, $"player.run 1 0 0 {ProofApp.F(teamsSeconds)} {n}");

            var expectedX = (innerRadius * Math.Cos(d: angle));
            var expectedZ = (innerRadius * Math.Sin(a: angle));

            expects.Add(item: ($"#expect p{n} {ProofApp.F(format: "0.00", value: expectedX)} 0.00 {ProofApp.F(format: "0.00", value: expectedZ)} " +
                $"{ProofApp.F(((int)Math.Round(a: yaw) % 360), "0")} 0 0"));
        }

        Emit(command: "world.fps", lines: lines, t: evidenceAt);
        Emit(command: "screen.state 4", lines: lines, t: (evidenceAt + 0.1));
        Emit(command: "world.gpu", lines: lines, t: (evidenceAt + 0.2));

        for (var i = 0; (i < population); i++) {
            Emit(command: $"player.where {(i + 5)}", lines: lines, t: sweepAt);
        }

        lines.AddRange(collection: expects);
        lines.Add(item: $"#cycle-end {ProofApp.F(format: "0.0", value: endAt)}");

        return lines;
    }

    // ----------------------------------------------------------------------------------------
    // FLOOD — the realism twin: a seeded offline human mixture model.
    // ----------------------------------------------------------------------------------------
    public static List<string> Flood(GenOptions o) {
        var lines = new List<string>();

        lines.Add(item: "# puck.world corpus v1 — THE FLOOD (generated by proof.cs generate --kind flood)");
        lines.Add(item: $"# population {o.Population} | {ProofApp.F(format: "0", value: o.DurationSeconds)}s | {o.ControlRate} Hz control | seed {o.Seed} | arena r={ProofApp.F(o.ArenaRadius)}");
        lines.Add(item: "# stochastic crowd: no #expect lines — the correctness bar is rerun near-identity (proof.cs compare)");
        lines.Add(item: "@0.0 world.timing on");
        lines.Add(item: $"@0.2 world.population {o.Population} idle");
        lines.Add(item: "@0.3 wire.ack quiet");
        lines.Add(item: "#loop-start");

        var timed = new List<TimedCommand>();
        var config = MixtureModel.Arena(radius: o.ArenaRadius);

        for (var i = 0; (i < o.Population); i++) {
            MixtureModel.GenerateStream(timed, index: i, entity: (i + 5), seed: o.Seed, config: config,
                streamStart: 1.0, durationSeconds: o.DurationSeconds, controlRate: o.ControlRate,
                bufferSeconds: 3.0, correctionInterval: o.CorrectionInterval, correctionStagger: (i % 24),
                withJumps: false, warpAt: 0.5, finalReconcile: false);
        }

        foreach (var item in timed.OrderBy(keySelector: t => t.T)) {
            lines.Add(item: $"@{ProofApp.F(format: "0.0##", value: item.T)} {item.Command}");
        }

        var evidenceAt = ((1.0 + o.DurationSeconds) + 2.5);
        var sweepAt = (evidenceAt + 1.0);

        lines.Add(item: $"@{ProofApp.F(format: "0.0", value: evidenceAt)} world.fps");
        lines.Add(item: $"@{ProofApp.F(format: "0.0", value: (evidenceAt + 0.2))} world.gpu");

        for (var i = 0; (i < o.Population); i++) {
            lines.Add(item: $"@{ProofApp.F(format: "0.0", value: sweepAt)} player.where {(i + 5)}");
        }

        lines.Add(item: $"#cycle-end {ProofApp.F(format: "0.0", value: (sweepAt + 1.0))}");

        return lines;
    }

    // ----------------------------------------------------------------------------------------
    // FLIGHT — the 6DOF proof: ascent, barrel roll, closed-form dive.
    // ----------------------------------------------------------------------------------------
    public static List<string> Flight(int population) {
        var lines = new List<string>();

        const double motionAt = 0.5;
        const double ascentAt = 3.0, ascentSeconds = 3.0;
        const double rollAt = 8.0, rollSeconds = 1.5;
        const double diveAt = 11.5, diveSeconds = 4.0;
        const double evidenceAt = 17.0, sweepAt = 18.0, endAt = 19.0;
        const int columns = 12;

        lines.Add(item: "# puck.world corpus v1 — FULL RANGE OF MOTION, the 6DOF flight proof (generated by proof.cs generate --kind flight)");
        lines.Add(item: $"# population {population}; one cycle spans {ProofApp.F(format: "0.0", value: endAt)}s");
        Emit(command: "world.timing on", lines: lines, t: 0.0);
        Emit(command: $"world.population {population} idle", lines: lines, t: 0.2);
        lines.Add(item: "#loop-start");

        for (var i = 0; (i < population); i++) {
            Emit(command: $"player.motion free {(i + 5)}", lines: lines, t: motionAt);
        }

        // Act A: THE ASCENT — ground lattice, each row pitched steeper, flies forward to climb.
        for (var i = 0; (i < population); i++) {
            var n = (i + 5);
            var column = (i % columns);
            var row = (i / columns);
            var x = ((column - ((columns - 1) / 2.0)) * 3.0);
            var z = (-10.0 - (2.0 * row));
            var climbDeg = (10.0 + ((row % 6) * 4.0));

            Emit(lines, ascentAt, $"player.pose {ProofApp.F(format: "0.##", value: x)} 0 {ProofApp.F(format: "0.##", value: z)} 0 {ProofApp.F(format: "0.#", value: climbDeg)} 0 {n}");
            Emit(lines, ascentAt, $"player.fly 1 0 0 0 0 0 {ProofApp.F(ascentSeconds)} {n}");
        }

        Emit(command: "world.fps", lines: lines, t: ((ascentAt + ascentSeconds) + 1.0));

        // Act B: THE ROLL — level at altitude, pure roll (a barrel roll).
        for (var i = 0; (i < population); i++) {
            var n = (i + 5);
            var column = (i % columns);
            var row = (i / columns);
            var x = ((column - ((columns - 1) / 2.0)) * 3.0);
            var z = (-10.0 - (2.0 * row));

            Emit(lines, rollAt, $"player.pose {ProofApp.F(format: "0.##", value: x)} 18 {ProofApp.F(format: "0.##", value: z)} 0 0 0 {n}");
            Emit(lines, rollAt, $"player.fly 0 0 0 0 0 1 {ProofApp.F(rollSeconds)} {n}");
        }

        Emit(command: "world.fps", lines: lines, t: ((rollAt + rollSeconds) + 1.0));

        // Act C: THE DIVE — ring at altitude aimed inward + down, straight forward fly (closed-form).
        const double ringRadius = 30.0, altitude = 22.0, divePitchDeg = -25.0;
        var expects = new List<string>();

        for (var i = 0; (i < population); i++) {
            var n = (i + 5);
            var phi = (((i * 2.0) * Math.PI) / population);
            var poseX = (ringRadius * Math.Cos(d: phi));
            var poseZ = (ringRadius * Math.Sin(a: phi));
            var yawDeg = YawDegrees(dirX: -Math.Cos(d: phi), dirZ: -Math.Sin(a: phi));
            var rollDeg = (20.0 * Math.Cos(d: phi));

            Emit(lines, diveAt, $"player.pose {ProofApp.F(format: "0.###", value: poseX)} {ProofApp.F(altitude)} {ProofApp.F(format: "0.###", value: poseZ)} {ProofApp.F(format: "0.##", value: yawDeg)} {ProofApp.F(format: "0.#", value: divePitchDeg)} {ProofApp.F(format: "0.##", value: rollDeg)} {n}");
            Emit(lines, diveAt, $"player.fly 1 0 0 0 0 0 {ProofApp.F(diveSeconds)} {n}");

            var (endX, endY, endZ) = FreeStraightFly(pitchDeg: divePitchDeg, travel: (MoveSpeed * diveSeconds), x: poseX, y: altitude, yawDeg: yawDeg, z: poseZ);

            expects.Add(item: ($"#expect p{n} {ProofApp.F(format: "0.00", value: endX)} {ProofApp.F(format: "0.00", value: endY)} {ProofApp.F(format: "0.00", value: endZ)} " +
                $"{CompassInt(degrees: yawDeg)} {CompassInt(degrees: divePitchDeg)} {CompassInt(degrees: rollDeg)}"));
        }

        Emit(command: "world.fps", lines: lines, t: evidenceAt);
        Emit(command: "world.gpu", lines: lines, t: (evidenceAt + 0.2));

        for (var i = 0; (i < population); i++) {
            Emit(command: $"player.where {(i + 5)}", lines: lines, t: sweepAt);
        }

        lines.AddRange(collection: expects);
        lines.Add(item: $"#cycle-end {ProofApp.F(format: "0.0", value: endAt)}");

        return lines;
    }

    // Closed-form end of a STRAIGHT forward fly (no angular input) from a pose anchor: pos = anchor +
    // facing*travel, facing = (-sin yaw cos pitch, sin pitch, -cos yaw cos pitch). Roll does not affect facing.
    public static (double X, double Y, double Z) FreeStraightFly(double x, double y, double z, double yawDeg, double pitchDeg, double travel) {
        var yaw = (yawDeg * (Math.PI / 180.0));
        var pitch = (pitchDeg * (Math.PI / 180.0));
        var facingX = (-Math.Sin(a: yaw) * Math.Cos(d: pitch));
        var facingY = Math.Sin(a: pitch);
        var facingZ = (-Math.Cos(d: yaw) * Math.Cos(d: pitch));

        return ((x + (facingX * travel)), (y + (facingY * travel)), (z + (facingZ * travel)));
    }

    // ----------------------------------------------------------------------------------------
    // HOP — the jump action-lane proof. Two #sweep-at sweeps: the mid-air band (a) + variable-height
    // separation (c), and the landed tableau (b).
    // ----------------------------------------------------------------------------------------
    public static List<string> Hop(int population) {
        var lines = new List<string>();

        // The jump kit, matching WorldBody's constants — the bands are DERIVED from these, never hard-coded.
        const double actionScale = 0.5;
        var jumpSpeed = (11.0 * actionScale);   // 5.5 u/s launch
        var riseGravity = (28.0 * actionScale); // 14 u/s^2 rising

        const double anchorAt = 0.5, jumpAt = 2.0, runSeconds = 0.5, midDelay = 0.20;
        var midSweepAt = (jumpAt + midDelay);
        var landedSweepAt = (jumpAt + 1.4);
        var evidenceAt = (landedSweepAt + 0.4);
        var endAt = (landedSweepAt + 1.4);

        const int columns = 12;
        const double fullHoldSeconds = 1.0, tapHoldSeconds = 0.008;

        var fullApex = ((jumpSpeed * jumpSpeed) / (2.0 * riseGravity));

        // Generous mid-air bands (wall-clock delivery jitter shifts the sample a few ms along a shallow arc).
        const double fullBandLo = 0.45, fullBandHi = 1.15;
        const double shortBandLo = 0.03, shortBandHi = 0.48;
        const double separation = 0.15;

        lines.Add(item: "# puck.world corpus v1 — THE HOPSCOTCH, the JUMP action-lane proof (generated by proof.cs generate --kind hop)");
        lines.Add(item: $"# population {population}; full-arc apex ~ {ProofApp.F(format: "0.00", value: fullApex)} u; one pass spans {ProofApp.F(format: "0.0", value: endAt)}s");
        Emit(command: "world.timing on", lines: lines, t: 0.0);

        // Pin the kit draw BEFORE the census: the default world's "hash" policy hands ~2 of 5 entities a flyer/swimmer
        // kit (model Free, no primaryAction), which structurally cannot jump — the bands below would then be asserted
        // against bodies that never leave the ground. The table policy cycles the three grounded jumping kits.
        Emit(command: "world.kit.assign table jumper runner kart", lines: lines, t: 0.1);
        Emit(command: $"world.population {population} idle", lines: lines, t: 0.2);

        var plans = new (int N, double AnchorX, double AnchorZ, bool Runs, bool FullHold, double LandedX, double LandedZ)[population];

        for (var i = 0; (i < population); i++) {
            var column = (i % columns);
            var row = (i / columns);
            var anchorX = ((column - ((columns - 1) / 2.0)) * 3.0);
            var anchorZ = (-8.0 - (4.0 * row));
            var runs = ((i % 4) >= 2);
            var fullHold = ((i % 2) == 0);
            var landedZ = (runs ? (anchorZ - (MoveSpeed * runSeconds)) : anchorZ);

            plans[i] = ((i + 5), anchorX, anchorZ, runs, fullHold, anchorX, landedZ);
        }

        // Anchor every entity on the grid (grounded default; warp lands it at rest on y=0) and face -Z.
        foreach (var p in plans) {
            Emit(lines, anchorAt, $"player.warp {ProofApp.F(format: "0.##", value: p.AnchorX)} {ProofApp.F(format: "0.##", value: p.AnchorZ)} {p.N}");
            Emit(command: $"player.face 0 {p.N}", lines: lines, t: anchorAt);
        }

        // The launch: a subset starts a forward run at the SAME instant it jumps (lane/tape independence).
        foreach (var p in plans) {
            if (p.Runs) {
                Emit(lines, jumpAt, $"player.run 1 0 0 {ProofApp.F(format: "0.##", value: runSeconds)} {p.N}");
            }

            var hold = (p.FullHold ? fullHoldSeconds : tapHoldSeconds);

            Emit(lines, jumpAt, $"player.press primary {ProofApp.F(format: "0.###", value: hold)} {p.N}");
        }

        // The two timed sweeps.
        foreach (var p in plans) {
            Emit(command: $"player.where {p.N}", lines: lines, t: midSweepAt);
        }

        foreach (var p in plans) {
            Emit(command: $"player.where {p.N}", lines: lines, t: landedSweepAt);
        }

        Emit(command: "world.fps", lines: lines, t: evidenceAt);
        Emit(command: "world.gpu", lines: lines, t: (evidenceAt + 0.2));

        // (a) MID-AIR band + (c) variable-height separation attach to the mid sweep.
        lines.Add(item: $"#sweep-at {ProofApp.F(format: "0.0#", value: midSweepAt)} mid-air");

        foreach (var p in plans) {
            if (p.FullHold) {
                lines.Add(item: $"#expect-band p{p.N} y {ProofApp.F(format: "0.00", value: fullBandLo)} {ProofApp.F(format: "0.00", value: fullBandHi)} full");
            }
            else {
                lines.Add(item: $"#expect-band p{p.N} y {ProofApp.F(format: "0.00", value: shortBandLo)} {ProofApp.F(format: "0.00", value: shortBandHi)} short");
            }
        }

        lines.Add(item: $"#expect-separation y full short {ProofApp.F(format: "0.00", value: separation)}");

        // (b) LANDED tableau attaches to the landed sweep: y exactly 0, x/z closed-form, grounded 0/0/0.
        lines.Add(item: $"#sweep-at {ProofApp.F(format: "0.0#", value: landedSweepAt)} landed");

        foreach (var p in plans) {
            lines.Add(item: $"#expect p{p.N} {ProofApp.F(format: "0.00", value: p.LandedX)} 0.00 {ProofApp.F(format: "0.00", value: p.LandedZ)} 0 0 0");
        }

        lines.Add(item: $"#cycle-end {ProofApp.F(format: "0.0", value: endAt)}");

        return lines;
    }
}

// ============================================================================================
// Behavior models — the offline generators whose intent streams the engine integrates. A seeded
// state machine + accel ramps + boundary steering, with an offline pose mirror (integrated exactly
// the way the grounded body model does), so every server correction targets a pose the sim
// agrees with — the intents-forward/corrections-back shape.
// ============================================================================================

// The zone the boundary steering herds an entity within: a disc (InnerRadius 0) or an annulus.
readonly record struct MixtureConfig(double InnerRadius, double OuterRadius, double SoftInner, double SoftOuter);
static class MixtureModel {
    const double MoveSpeed = Generators.MoveSpeed;
    const double TurnSpeed = Generators.TurnSpeed;

    // State table: target forward deflection + mean dwell seconds + selection weight (deflections mapped onto the
    // body's 4 u/s scale; sprint:run ratio held at 1.6).
    static readonly (string Name, double Deflection, double MeanDwell, int Weight)[] States = {
        ("Idle", 0.0, 1.2, 15),
        ("Walk", 0.35, 2.5, 30),
        ("Run", 0.625, 3.0, 35),
        ("Sprint", 1.0, 2.0, 20),
    };

    const double AccelDeflectionRate = (90.0 / 8.0);   // ground accel / max speed (deflection units/s toward target)
    const double DecelDeflectionRate = (110.0 / 8.0);

    public static MixtureConfig Arena(double radius) {
        return new MixtureConfig(InnerRadius: 0.0, OuterRadius: radius, SoftInner: 0.0, SoftOuter: (radius * 0.85));
    }
    public static MixtureConfig Annulus(double inner, double outer) {
        var span = (outer - inner);

        return new MixtureConfig(
            InnerRadius: inner,
            OuterRadius: outer,
            SoftInner: (inner + (span * 0.2)),
            SoftOuter: (inner + (span * 0.8)));
    }

    static string F3(double v) {
        return ProofApp.F(format: "0.###", value: v);
    }

    // Generate one entity's whole intent stream into <paramref name="timed"/> (send-time, line). Movement segments
    // are emitted bufferSeconds ahead (tape pre-buffer); reconcile corrections are sent AT their play time. The
    // offline mirror integrates exactly the way IntegrateGrounded will (turn THEN step along the new facing with
    // forward+strafe), so a correction snaps the sim to a pose it already agrees with.
    public static void GenerateStream(List<TimedCommand> timed, int index, int entity, int seed, MixtureConfig config,
        double streamStart, double durationSeconds, int controlRate, double bufferSeconds,
        double correctionInterval, int correctionStagger, bool withJumps, double warpAt, bool finalReconcile) {

        var rng = new Random(Seed: ((seed * 1000) + entity));
        var dt = (1.0 / controlRate);
        var ticks = (int)Math.Round(a: (durationSeconds * controlRate));

        // Area-uniform spawn within [inner, outer], random initial yaw.
        var u = rng.NextDouble();
        var spawnAngle = ((rng.NextDouble() * 2.0) * Math.PI);
        var spawnRadius = Math.Sqrt(d: ((config.InnerRadius * config.InnerRadius) +
            (u * ((config.OuterRadius * config.OuterRadius) - (config.InnerRadius * config.InnerRadius)))));
        var px = (spawnRadius * Math.Cos(d: spawnAngle));
        var pz = (spawnRadius * Math.Sin(a: spawnAngle));
        var pYaw = ((rng.NextDouble() * 2.0) * Math.PI);

        timed.Add(item: new TimedCommand(warpAt, $"player.warp {ProofApp.F(format: "0.##", value: px)} {ProofApp.F(format: "0.##", value: pz)} {entity}", true));
        timed.Add(item: new TimedCommand(warpAt, $"player.face {ProofApp.F(format: "0.#", value: (pYaw * (180.0 / Math.PI)))} {entity}", true));

        var state = States[1];   // everyone opens walking; the first dwell roll diversifies immediately
        var dwellLeft = 0.0;
        var turnBias = 0.0;
        var deflection = 0.0;

        var phase = (rng.NextDouble() * dt);
        var nextCorrection = (correctionInterval + (correctionStagger * 0.25));
        var nextJump = (withJumps ? (2.0 + (rng.NextDouble() * 2.0)) : double.MaxValue);

        for (var k = 0; (k < ticks); k++) {
            if (dwellLeft <= 0.0) {
                var totalWeight = 0;

                foreach (var candidate in States) {
                    totalWeight += candidate.Weight;
                }

                var roll = (rng.NextDouble() * totalWeight);

                foreach (var candidate in States) {
                    roll -= candidate.Weight;

                    if (roll <= 0.0) {
                        state = candidate;

                        break;
                    }
                }

                dwellLeft = (-state.MeanDwell * Math.Log(d: (1.0 - rng.NextDouble())));
                var headingRoll = rng.NextDouble();

                turnBias = ((headingRoll < 0.6) ? 0.0
                    : ((headingRoll < 0.9) ? ((rng.NextDouble() * 0.5) - 0.25)
                    : ((rng.NextDouble() * 1.2) - 0.6)));
            }

            dwellLeft -= dt;

            // Accel: deflection eases toward the state target at the Demo-derived rate.
            var target = state.Deflection;
            var rate = ((target > deflection) ? AccelDeflectionRate : DecelDeflectionRate);
            var maxDelta = (rate * dt);
            var delta = (target - deflection);

            if (Math.Abs(value: delta) > maxDelta) {
                delta = (maxDelta * Math.Sign(value: delta));
            }

            deflection += delta;

            // Turn = state bias + per-tick jitter + boundary steering (herd back inside the zone).
            var turn = (turnBias + ((rng.NextDouble() * 0.1) - 0.05));
            var radius = Math.Sqrt(d: ((px * px) + (pz * pz)));

            if (deflection > 0.01) {
                // dir(yaw) = (-sin, -cos); the signed angle from heading to the desired direction decides the sign.
                var dirX = -Math.Sin(a: pYaw);
                var dirZ = -Math.Cos(d: pYaw);

                if ((radius > config.SoftOuter) && (config.SoftOuter > 0.0)) {
                    turn += SteerToward(dirX, dirZ, (-px / radius), (-pz / radius),
                        urgency: Math.Min(val1: 1.0, val2: ((radius - config.SoftOuter) / Math.Max(val1: 1e-6, val2: (config.OuterRadius - config.SoftOuter)))));
                }
                else if ((config.InnerRadius > 0.0) && (radius < config.SoftInner) && (radius > 1e-6)) {
                    turn += SteerToward(dirX, dirZ, (px / radius), (pz / radius),
                        urgency: Math.Min(val1: 1.0, val2: ((config.SoftInner - radius) / Math.Max(val1: 1e-6, val2: (config.SoftInner - config.InnerRadius)))));
                }
            }

            turn = Math.Clamp(max: 1.0, min: -1.0, value: turn);

            var playAt = ((streamStart + phase) + (k * dt));

            // A jump tap on its schedule (report-only liveliness for the platformer archetype) — sent at play time.
            if (playAt >= nextJump) {
                var hold = ((rng.NextDouble() < 0.5) ? 0.008 : 0.6);   // a mix of hops and leaps

                timed.Add(item: new TimedCommand(playAt, $"player.press primary {ProofApp.F(format: "0.###", value: hold)} {entity}", true));
                nextJump = ((playAt + 2.5) + (rng.NextDouble() * 2.5));
            }

            // Server correction: re-anchor the sim to the offline mirror pose (ONE reconcile line).
            if (playAt >= nextCorrection) {
                var mirrorYawDegrees = ((pYaw * (180.0 / Math.PI)) % 360.0);

                timed.Add(item: new TimedCommand(playAt, $"player.reconcile {ProofApp.F(format: "0.##", value: px)} {ProofApp.F(format: "0.##", value: pz)} {ProofApp.F(format: "0.#", value: mirrorYawDegrees)} {entity}", true));
                nextCorrection += correctionInterval;
            }

            var sendAt = Math.Max(val1: (warpAt + 0.1), val2: (playAt - bufferSeconds));

            timed.Add(item: new TimedCommand(sendAt, $"player.run {F3(v: deflection)} 0 {F3(v: turn)} {F3(v: dt)} {entity}", true));

            // Advance the offline mirror the way IntegrateGrounded will: turn THEN step along the new facing.
            pYaw += ((turn * TurnSpeed) * dt);
            px += (((-Math.Sin(a: pYaw) * deflection) * MoveSpeed) * dt);
            pz += (((-Math.Cos(d: pYaw) * deflection) * MoveSpeed) * dt);
        }

        // A final correction AFTER the last segment pins the entity at a deterministic resting pose so the final
        // report-only sweep reads an identical pose across reruns (the tight rerun-envelope guarantee).
        if (finalReconcile) {
            var mirrorYawDegrees = ((pYaw * (180.0 / Math.PI)) % 360.0);
            var pinAt = ((streamStart + durationSeconds) + 0.7);

            timed.Add(item: new TimedCommand(pinAt, $"player.reconcile {ProofApp.F(format: "0.##", value: px)} {ProofApp.F(format: "0.##", value: pz)} {ProofApp.F(format: "0.#", value: mirrorYawDegrees)} {entity}", true));
        }
    }

    // The turn contribution that steers a heading toward a desired inward/outward unit direction. Returns 0 when the
    // heading already points close enough. In dir(yaw)=(-sin,-cos), a +yaw turn rotates toward the "left" vector and
    // desired·left = -cross, so a NEGATIVE cross means turn left (+yaw). Magnitude scales with urgency (0.8 gain).
    static double SteerToward(double dirX, double dirZ, double toX, double toZ, double urgency) {
        var cross = ((dirX * toZ) - (dirZ * toX));
        var dot = ((dirX * toX) + (dirZ * toZ));

        if (dot >= 0.985) {
            return 0.0;
        }

        return ((urgency * 0.8) * ((cross < 0.0) ? 1.0 : -1.0));
    }
}

// ============================================================================================
// The KART model — a grounded racer around an oval ring. A heading controller chases a target that
// orbits the oval (accel toward a cruise speed, corner slowdown on the two hairpins, strafe-led drift
// so facing decouples from velocity). An offline mirror integrates the emitted intents exactly, and a
// reconcile every interval pins the sim to it — the correctness bar is rerun near-identity.
// ============================================================================================

static class KartModel {
    const double MoveSpeed = Generators.MoveSpeed;
    const double TurnSpeed = Generators.TurnSpeed;

    static string F3(double v) {
        return ProofApp.F(format: "0.###", value: v);
    }
    static double WrapPi(double angle) {
        while (angle > Math.PI) {
            angle -= (2.0 * Math.PI);
        }

        while (angle < -Math.PI) {
            angle += (2.0 * Math.PI);
        }

        return angle;
    }

    // Generate one kart's stream. rx/rz = oval radii (rx>rz gives long straights + two hairpins at the X-axis ends).
    public static void GenerateStream(List<TimedCommand> timed, int entity, int lane, int kartCount,
        double rx, double rz, double streamStart, double durationSeconds, int controlRate, double bufferSeconds,
        double correctionInterval, int correctionStagger, double warpAt) {

        var dt = (1.0 / controlRate);
        var ticks = (int)Math.Round(a: (durationSeconds * controlRate));
        const double omega = 0.10;   // target angular rate (rad/s) — tuned so the target's linear speed ~ kart cruise
        const double cruise = 0.9;
        const double accelRate = 2.0; // deflection units/s toward the cruise target

        // Stagger the field around the oval; each kart starts on the oval at its phase, facing the path tangent.
        var phase = (((lane / (double)kartCount) * 2.0) * Math.PI);
        var px = (rx * Math.Cos(d: phase));
        var pz = (rz * Math.Sin(a: phase));
        // Tangent of the oval at phase: d/dphi (rx cos, rz sin) = (-rx sin, rz cos).
        var pYaw = Math.Atan2(-(-rx * Math.Sin(a: phase)), -(rz * Math.Cos(d: phase)));   // yaw = atan2(-tx, -tz)
        var fwd = 0.0;
        var targetPhase = phase;
        var nextCorrection = (correctionInterval + (correctionStagger * 0.25));

        timed.Add(item: new TimedCommand(warpAt, $"player.warp {ProofApp.F(format: "0.##", value: px)} {ProofApp.F(format: "0.##", value: pz)} {entity}", true));
        timed.Add(item: new TimedCommand(warpAt, $"player.face {ProofApp.F(format: "0.#", value: (pYaw * (180.0 / Math.PI)))} {entity}", true));

        for (var k = 0; (k < ticks); k++) {
            var playAt = (streamStart + (k * dt));

            // Advance the orbiting target along the oval and steer the kart's heading toward it.
            targetPhase += (omega * dt);
            var tx = (rx * Math.Cos(d: targetPhase));
            var tz = (rz * Math.Sin(a: targetPhase));
            var toX = (tx - px);
            var toZ = (tz - pz);
            var desiredYaw = Math.Atan2(x: -toZ, y: -toX);
            var yawErr = WrapPi(angle: (desiredYaw - pYaw));
            var turn = Math.Clamp(max: 1.0, min: -1.0, value: (yawErr / (TurnSpeed * dt)));

            // Corner slowdown: curvature peaks at the hairpins (phase 0 / pi, where cos^2 = 1).
            var cornerFactor = (1.0 - ((0.35 * Math.Cos(d: targetPhase)) * Math.Cos(d: targetPhase)));
            var targetFwd = (cruise * cornerFactor);
            var fwdDelta = (targetFwd - fwd);
            var maxFwdDelta = (accelRate * dt);

            if (Math.Abs(value: fwdDelta) > maxFwdDelta) {
                fwdDelta = (maxFwdDelta * Math.Sign(value: fwdDelta));
            }

            fwd += fwdDelta;

            // Strafe-led drift into the hairpins (leads with the steering sign) — facing decouples from velocity.
            var strafe = Math.Clamp((((0.3 * Math.Cos(d: targetPhase)) * Math.Cos(d: targetPhase)) * Math.Sign(value: turn)), -1.0, 1.0);

            if (playAt >= nextCorrection) {
                var mirrorYawDegrees = ((pYaw * (180.0 / Math.PI)) % 360.0);

                timed.Add(item: new TimedCommand(playAt, $"player.reconcile {ProofApp.F(format: "0.##", value: px)} {ProofApp.F(format: "0.##", value: pz)} {ProofApp.F(format: "0.#", value: mirrorYawDegrees)} {entity}", true));
                nextCorrection += correctionInterval;
            }

            var sendAt = Math.Max(val1: (warpAt + 0.1), val2: (playAt - bufferSeconds));

            timed.Add(item: new TimedCommand(sendAt, $"player.run {F3(v: fwd)} {F3(v: strafe)} {F3(v: turn)} {F3(v: dt)} {entity}", true));

            // Advance the offline mirror (grounded: turn THEN step forward+strafe along the new facing/right).
            pYaw += ((turn * TurnSpeed) * dt);
            var fx = -Math.Sin(a: pYaw);
            var fz = -Math.Cos(d: pYaw);
            var rxv = Math.Cos(d: pYaw);
            var rzv = -Math.Sin(a: pYaw);

            px += ((((fx * fwd) + (rxv * strafe)) * MoveSpeed) * dt);
            pz += ((((fz * fwd) + (rzv * strafe)) * MoveSpeed) * dt);
        }

        // Pin to the final mirror pose after the last segment so the report-only sweep is rerun-identical.
        var finalYawDegrees = ((pYaw * (180.0 / Math.PI)) % 360.0);
        var pinAt = ((streamStart + durationSeconds) + 0.7);

        timed.Add(item: new TimedCommand(pinAt, $"player.reconcile {ProofApp.F(format: "0.##", value: px)} {ProofApp.F(format: "0.##", value: pz)} {ProofApp.F(format: "0.#", value: finalYawDegrees)} {entity}", true));
    }
}

// ============================================================================================
// THE EXPO — the primary mixed-genre corpus. Five archetypes in LAYERED ZONES of ONE shared
// scene, an even split of the population, a ~90 s loop-capable cycle:
//   KARTS      grounded  oval ring (rx 38 / rz 30)   offline kart model + reconcile   rerun-envelope
//   PLATFORMERS grounded+jump  central plaza (r<=12)  run/hop show + a re-anchored     closed-form landed
//                                                     hop showcase + landed finale     + mid-air band
//   SHIPS      free      airspace band y 18..30       formation pass + barrel roll +   closed-form tableau
//                                                     a closed-form dive finale
//   SUBMARINES free      low band y 4..10             slow damped glide + pitch under-  closed-form tableau
//                                                     ulation + straight finale
//   WALKERS    grounded  plaza-rim annulus (r 14..22) the flood human mixture, confined  rerun-envelope
// ============================================================================================

static class ExpoBuilder {
    const double MoveSpeed = Generators.MoveSpeed;

    // --- timeline (seconds from cycle start) ---
    const double MotionAt = 0.5;
    const double StreamStart = 2.0;
    const double StreamDuration = 80.0;          // karts + walkers run the whole show
    const double PlatformerShowDuration = 50.0;  // platformers stream then re-anchor for the showcase
    const double MidDelay = 0.2;
    const double PlatformerAnchorAt = 56.5;
    const double PlatformerJumpAt = 57.3;
    const double ShipRollAt = 8.0;
    const double ShipShowAt = 3.0;
    const double SubShowAt = 4.0;
    const double MidSweepAt = (PlatformerJumpAt + MidDelay);   // 57.5
    const double EndAt = 90.0;
    const double EvidenceAt = 84.0;
    const double EvidenceMidAt = 40.0;
    const double FinalSweepAt = 88.0;
    const double PlatformerFinaleAt = 78.0;
    const double ShipFinaleAt = 70.0;
    const double SubFinaleAt = 72.0;

    // Jump-kit bands (WorldBody constants, ActionScale 0.5) — DERIVED, never hard-coded.
    const double FullBandLo = 0.45, FullBandHi = 1.15;
    const double MidSeparation = 0.15;
    const double ShortBandLo = 0.03, ShortBandHi = 0.48;

    public static List<string> Build(int population, int seed) {
        // Even archetype split (contiguous blocks): perTeam = ceil(P/5), arch = min(4, i/perTeam).
        var perTeam = ((population + 4) / 5);
        var karts = new List<int>();
        var platformers = new List<int>();
        var ships = new List<int>();
        var subs = new List<int>();
        var walkers = new List<int>();

        for (var i = 0; (i < population); i++) {
            var arch = Math.Min(val1: 4, val2: (i / perTeam));
            var entity = (i + 5);

            switch (arch) {
                case 0: karts.Add(item: entity); break;
                case 1: platformers.Add(item: entity); break;
                case 2: ships.Add(item: entity); break;
                case 3: subs.Add(item: entity); break;
                default: walkers.Add(item: entity); break;
            }
        }

        var timed = new List<TimedCommand>();       // the in-loop @t body (sorted by send time on output)
        var midDirectives = new List<string>();      // the mid-air sweep block (#sweep-at 57.5 + bands + separation)
        var finaleExpects = new List<string>();      // the finale sweep's closed-form pose #expects (ships/subs/platformers)

        // Ships + subs go 6DOF for the whole cycle.
        foreach (var n in ships.Concat(second: subs)) {
            timed.Add(item: new TimedCommand(Command: $"player.motion free {n}", InLoop: true, T: MotionAt));
        }

        BuildKarts(karts: karts, timed: timed);
        BuildWalkers(seed: seed, timed: timed, walkers: walkers);
        BuildPlatformers(finaleExpects: finaleExpects, midDirectives: midDirectives, platformers: platformers, seed: seed, timed: timed);
        BuildShips(directives: finaleExpects, ships: ships, timed: timed);
        BuildSubs(directives: finaleExpects, subs: subs, timed: timed);

        // Evidence reads (mid-show + cycle end).
        timed.Add(item: new TimedCommand(Command: "world.fps", InLoop: true, T: EvidenceMidAt));
        timed.Add(item: new TimedCommand(Command: "world.fps", InLoop: true, T: EvidenceAt));
        timed.Add(item: new TimedCommand(Command: "screen.state 4", InLoop: true, T: (EvidenceAt + 0.1)));
        timed.Add(item: new TimedCommand(Command: "world.gpu", InLoop: true, T: (EvidenceAt + 0.2)));

        // The final tableau sweep: EVERY entity reads back (ships/subs/platformers assert closed-form; karts/
        // walkers are captured report-only for the rerun-envelope compare).
        foreach (var n in AllEntities(population: population)) {
            timed.Add(item: new TimedCommand(Command: $"player.where {n}", InLoop: true, T: FinalSweepAt));
        }

        // Assemble the directive block IN ATTACH ORDER: each #sweep-at opens a context its following #expect*
        // lines attach to. Mid-air block first (its bands/separation), then the finale sweep + its pose #expects.
        var directives = new List<string>();

        directives.AddRange(collection: midDirectives);
        directives.Add(item: $"#sweep-at {ProofApp.F(format: "0.0#", value: FinalSweepAt)} finale");
        directives.AddRange(collection: finaleExpects);

        // --- assemble ---
        var lines = new List<string>();

        lines.Add(item: "# puck.world corpus v1 — THE EXPO, the mixed-genre render proof (generated by proof.cs generate --kind expo)");
        lines.Add(item: $"# population {population} | karts {karts.Count} / platformers {platformers.Count} / ships {ships.Count} / subs {subs.Count} / walkers {walkers.Count} | seed {seed} | one cycle spans {ProofApp.F(format: "0.0", value: EndAt)}s");
        lines.Add(item: "@0.0 world.timing on");
        // The platformer block asserts jump bands, and archetypes are index blocks — not kit draws. Pin the kit table
        // before the census so no platformer index lands on flyer/swimmer (model Free, no primaryAction, cannot jump).
        lines.Add(item: "@0.1 world.kit.assign table jumper runner kart");
        lines.Add(item: $"@0.2 world.population {population} idle");
        lines.Add(item: "@0.3 wire.ack quiet");
        lines.Add(item: "#loop-start");

        foreach (var item in timed.OrderBy(keySelector: t => t.T)) {
            lines.Add(item: $"@{ProofApp.F(format: "0.0##", value: item.T)} {item.Command}");
        }

        lines.AddRange(collection: directives);
        lines.Add(item: $"#cycle-end {ProofApp.F(format: "0.0", value: EndAt)}");

        return lines;
    }

    static IEnumerable<int> AllEntities(int population) {
        for (var i = 0; (i < population); i++) {
            yield return (i + 5);
        }
    }

    // KARTS — the oval ring race. Report-only (rerun envelope via the final reconcile pin).
    static void BuildKarts(List<TimedCommand> timed, List<int> karts) {
        for (var lane = 0; (lane < karts.Count); lane++) {
            KartModel.GenerateStream(timed, entity: karts[lane], lane: lane, kartCount: karts.Count,
                rx: 38.0, rz: 30.0, streamStart: StreamStart, durationSeconds: StreamDuration, controlRate: 5,
                bufferSeconds: 3.0, correctionInterval: 6.0, correctionStagger: (lane % 24), warpAt: 1.5);
        }
    }

    // WALKERS — the flood human mixture confined to the plaza-rim annulus. Report-only (rerun envelope).
    static void BuildWalkers(List<TimedCommand> timed, List<int> walkers, int seed) {
        var config = MixtureModel.Annulus(inner: 14.0, outer: 22.0);

        for (var w = 0; (w < walkers.Count); w++) {
            MixtureModel.GenerateStream(timed, index: w, entity: walkers[w], seed: (seed + 1), config: config,
                streamStart: StreamStart, durationSeconds: StreamDuration, controlRate: 5, bufferSeconds: 3.0,
                correctionInterval: 6.0, correctionStagger: (w % 24), withJumps: false, warpAt: 1.5, finalReconcile: true);
        }
    }

    // PLATFORMERS — a stochastic run/hop show in the plaza, then a re-anchored mid-air hop SHOWCASE (band +
    // separation asserts) and a re-anchored closed-form LANDED finale.
    static void BuildPlatformers(List<TimedCommand> timed, List<string> midDirectives, List<string> finaleExpects, List<int> platformers, int seed) {
        var config = MixtureModel.Arena(radius: 11.0);   // a plaza disc

        for (var p = 0; (p < platformers.Count); p++) {
            MixtureModel.GenerateStream(timed, index: p, entity: platformers[p], seed: (seed + 2), config: config,
                streamStart: StreamStart, durationSeconds: PlatformerShowDuration, controlRate: 5, bufferSeconds: 3.0,
                correctionInterval: 6.0, correctionStagger: (p % 24), withJumps: true, warpAt: 1.5, finalReconcile: false);
        }

        // --- the mid-air hop showcase: re-anchor on a plaza grid, launch a full/short split, sweep at apex ---
        const int cols = 5;

        midDirectives.Add(item: $"#sweep-at {ProofApp.F(format: "0.0#", value: MidSweepAt)} platformer-air");

        for (var p = 0; (p < platformers.Count); p++) {
            var n = platformers[p];
            var col = (p % cols);
            var row = (p / cols);
            var x = ((col - 2.0) * 2.5);
            var z = ((row - 2.0) * 2.5);
            var fullHold = ((p % 2) == 0);
            var runs = ((p % 4) >= 2);   // a subset runs mid-jump (lane/tape independence)

            timed.Add(item: new TimedCommand(PlatformerAnchorAt, $"player.warp {ProofApp.F(format: "0.##", value: x)} {ProofApp.F(format: "0.##", value: z)} {n}", true));
            timed.Add(item: new TimedCommand(Command: $"player.face 0 {n}", InLoop: true, T: PlatformerAnchorAt));

            if (runs) {
                timed.Add(item: new TimedCommand(Command: $"player.run 1 0 0 0.5 {n}", InLoop: true, T: PlatformerJumpAt));
            }

            var hold = (fullHold ? 1.0 : 0.008);

            timed.Add(item: new TimedCommand(PlatformerJumpAt, $"player.press primary {ProofApp.F(format: "0.###", value: hold)} {n}", true));
            timed.Add(item: new TimedCommand(Command: $"player.where {n}", InLoop: true, T: MidSweepAt));

            if (fullHold) {
                midDirectives.Add(item: $"#expect-band p{n} y {ProofApp.F(format: "0.00", value: FullBandLo)} {ProofApp.F(format: "0.00", value: FullBandHi)} full");
            }
            else {
                midDirectives.Add(item: $"#expect-band p{n} y {ProofApp.F(format: "0.00", value: ShortBandLo)} {ProofApp.F(format: "0.00", value: ShortBandHi)} short");
            }
        }

        midDirectives.Add(item: $"#expect-separation y full short {ProofApp.F(format: "0.00", value: MidSeparation)}");

        // --- the landed finale: re-anchor on a plaza grid, run a tape-timed segment to a closed-form rest ---
        const double runSeconds = 1.0;

        for (var p = 0; (p < platformers.Count); p++) {
            var n = platformers[p];
            var col = (p % cols);
            var row = (p / cols);
            var x = ((col - 2.0) * 2.4);
            var z = ((row - 2.0) * 2.0);
            var landedZ = (z - (MoveSpeed * runSeconds));   // face 0 -> dir(-Z), run moves -Z; x unchanged, y=0

            timed.Add(item: new TimedCommand(PlatformerFinaleAt, $"player.warp {ProofApp.F(format: "0.##", value: x)} {ProofApp.F(format: "0.##", value: z)} {n}", true));
            timed.Add(item: new TimedCommand(Command: $"player.face 0 {n}", InLoop: true, T: PlatformerFinaleAt));
            timed.Add(item: new TimedCommand(PlatformerFinaleAt, $"player.run 1 0 0 {ProofApp.F(format: "0.##", value: runSeconds)} {n}", true));

            finaleExpects.Add(item: $"#expect p{n} {ProofApp.F(format: "0.00", value: x)} 0.00 {ProofApp.F(format: "0.00", value: landedZ)} 0 0 0");
        }
    }

    // SHIPS — a formation pass with a banked turn + a synchronized barrel roll (report-only), then a closed-form
    // dive finale (pose on a ring at altitude, aimed inward + down, a STRAIGHT fly to a descending shell).
    static void BuildShips(List<TimedCommand> timed, List<string> directives, List<int> ships) {
        var count = ships.Count;

        // Show: a banked formation pass across the arena.
        for (var s = 0; (s < count); s++) {
            var n = ships[s];
            var x = ((s - ((count - 1) / 2.0)) * 3.0);

            timed.Add(item: new TimedCommand(ShipShowAt, $"player.pose {ProofApp.F(format: "0.##", value: x)} 24 -32 0 0 0 {n}", true));
            timed.Add(item: new TimedCommand(Command: $"player.fly 1 0 0 0.3 0 0.3 3 {n}", InLoop: true, T: ShipShowAt));
        }

        // Show: the synchronized barrel roll.
        for (var s = 0; (s < count); s++) {
            var n = ships[s];
            var x = ((s - ((count - 1) / 2.0)) * 3.0);

            timed.Add(item: new TimedCommand(ShipRollAt, $"player.pose {ProofApp.F(format: "0.##", value: x)} 26 -18 0 0 0 {n}", true));
            timed.Add(item: new TimedCommand(Command: $"player.fly 0 0 0 0 0 1 1.5 {n}", InLoop: true, T: ShipRollAt));
        }

        // Finale: a ring dive (closed-form). Attitude unchanged by the straight fly; position = anchor + facing*travel.
        const double ring = 32.0, altitude = 26.0, pitch = -18.0, seconds = 4.0;

        for (var s = 0; (s < count); s++) {
            var n = ships[s];
            var phi = ((count > 0) ? (((s * 2.0) * Math.PI) / count) : 0.0);
            var poseX = (ring * Math.Cos(d: phi));
            var poseZ = (ring * Math.Sin(a: phi));
            var yawDeg = Generators.YawDegrees(dirX: -Math.Cos(d: phi), dirZ: -Math.Sin(a: phi));
            var rollDeg = (20.0 * Math.Cos(d: phi));

            timed.Add(item: new TimedCommand(ShipFinaleAt, $"player.pose {ProofApp.F(format: "0.###", value: poseX)} {ProofApp.F(altitude)} {ProofApp.F(format: "0.###", value: poseZ)} {ProofApp.F(format: "0.##", value: yawDeg)} {ProofApp.F(format: "0.#", value: pitch)} {ProofApp.F(format: "0.##", value: rollDeg)} {n}", true));
            timed.Add(item: new TimedCommand(ShipFinaleAt, $"player.fly 1 0 0 0 0 0 {ProofApp.F(seconds)} {n}", true));

            var (endX, endY, endZ) = Generators.FreeStraightFly(pitchDeg: pitch, travel: (MoveSpeed * seconds), x: poseX, y: altitude, yawDeg: yawDeg, z: poseZ);
            directives.Add(item: $"#expect p{n} {ProofApp.F(format: "0.00", value: endX)} {ProofApp.F(format: "0.00", value: endY)} {ProofApp.F(format: "0.00", value: endZ)} {Generators.CompassInt(degrees: yawDeg)} {Generators.CompassInt(degrees: pitch)} {Generators.CompassInt(degrees: rollDeg)}");
        }
    }

    // SUBMARINES — a slow damped glide with gentle pitch undulation (report-only), then a closed-form straight
    // finale on the low band. A damped-glide tempo: small deflections (<=0.4), lazy motion.
    static void BuildSubs(List<TimedCommand> timed, List<string> directives, List<int> subs) {
        var count = subs.Count;
        const int cols = 6;

        // Show: a gentle glide with a pitch-up rate (undulation).
        for (var s = 0; (s < count); s++) {
            var n = subs[s];
            var col = (s % cols);
            var row = (s / cols);
            var x = ((col - ((cols - 1) / 2.0)) * 4.0);
            var z = (-28.0 + (row * 4.0));

            timed.Add(item: new TimedCommand(SubShowAt, $"player.pose {ProofApp.F(format: "0.##", value: x)} 7 {ProofApp.F(format: "0.##", value: z)} 0 0 0 {n}", true));
            timed.Add(item: new TimedCommand(Command: $"player.fly 0.3 0 0 0 0.15 0 4 {n}", InLoop: true, T: SubShowAt));
        }

        // Finale: level on the low band, a straight forward glide (closed-form; y unchanged, yaw/pitch/roll 0).
        const double altitude = 7.0, seconds = 3.0;

        for (var s = 0; (s < count); s++) {
            var n = subs[s];
            var col = (s % cols);
            var row = (s / cols);
            var x = ((col - ((cols - 1) / 2.0)) * 4.0);
            var z = (-6.0 + (row * 4.0));
            var endZ = (z - (MoveSpeed * seconds));   // yaw 0 pitch 0 -> facing (0,0,-1); moves -Z

            timed.Add(item: new TimedCommand(SubFinaleAt, $"player.pose {ProofApp.F(format: "0.##", value: x)} {ProofApp.F(altitude)} {ProofApp.F(format: "0.##", value: z)} 0 0 0 {n}", true));
            timed.Add(item: new TimedCommand(SubFinaleAt, $"player.fly 1 0 0 0 0 0 {ProofApp.F(seconds)} {n}", true));

            directives.Add(item: $"#expect p{n} {ProofApp.F(format: "0.00", value: x)} {ProofApp.F(format: "0.00", value: altitude)} {ProofApp.F(format: "0.00", value: endZ)} 0 0 0");
        }
    }
}

// ============================================================================================
// Output collection — a reader thread per stream, timestamped (no event-queue ceiling under a
// 37k-line flood burst).
// ============================================================================================

sealed class OutputCollector {
    // One collector per driven session, and the session ledger every suite's verdict funnels through. THE HOUSE RULE:
    // a session a suite pipes lines into must SETTLE the wire's own refused-line counter (`wire.errors`) against the
    // refusals that session deliberately provoked. That counter is the only complete account of a refusal — it counts
    // an unknown verb, a parse failure, a HANDLER's error result (a retired payload, a bad value, a denied grant) and
    // a DEFERRED rejection raised a tick after the line was accepted. Reading the transcript for the registry's
    // "[wire.reject:" sigil sees only the first two, so a refused setup verb whose assertion was satisfied by other
    // means used to read green. A driven session that never settles is that same hole with no evidence at all, which
    // is why the omission is a FAILURE here rather than a silence.
    static readonly ConcurrentBag<OutputCollector> s_sessions = [];

    /// <summary>Every session collector this process has created, in no particular order.</summary>
    public static IReadOnlyCollection<OutputCollector> Sessions => s_sessions;

    readonly ConcurrentQueue<string> m_lines = new();

    int m_driven;
    int m_settled;

    public OutputCollector([CallerMemberName] string label = "") {
        Label = label;

        s_sessions.Add(item: this);
    }

    /// <summary>The launching method's name — how an unsettled session names itself in the central verdict.</summary>
    public string Label { get; }

    /// <summary>True once a line has been piped into this session (a settle is then owed).</summary>
    public bool Driven => (Volatile.Read(location: ref m_driven) != 0);

    /// <summary>True once <c>wire.errors</c> has been settled against this session's deliberate refusals.</summary>
    public bool Settled => (Volatile.Read(location: ref m_settled) != 0);

    public int Count => m_lines.Count;

    public void NoteDriven() {
        Volatile.Write(location: ref m_driven, value: 1);
    }
    public void NoteSettled() {
        Volatile.Write(location: ref m_settled, value: 1);
    }
    public void Start(TextReader reader, Stopwatch stopwatch) {
        _ = Task.Run(action: () => {
            string? line;

            while ((line = reader.ReadLine()) is not null) {
                m_lines.Enqueue(item: string.Format(arg0: stopwatch.Elapsed.TotalSeconds, arg1: line, format: "[{0,7:0.00}s] {1}", provider: ProofApp.Inv));
            }
        });
    }
    public string[] Snapshot() {
        return m_lines.ToArray();
    }
}
readonly record struct Pose(double X, double Y, double Z, int Yaw, int Pitch, int Roll);

// ============================================================================================
// Shared player-document store paths + directory backup — used by BindingsProof and StorageProof, the two
// suites that boot against the REAL local player-document store (WorldProfileStore has no CLI override; no
// --user-id, no env var). Both back up the FULL world/ subtree before touching anything and restore it in a
// finally, so the real catalog is never left mutated.
// ============================================================================================

// The fixed local-store identity WorldProfileStore addresses, and the per-profile split layout's paths
// under it, matching WorldProfileStore.cs's private address table.
static class PlayerStorePaths {
    static readonly Guid LocalProfilesId = new(g: "b1d5c0de-0002-4000-8000-000000000001");

    public static string StoreRoot() {
        return Path.Combine(
            Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData),
            "Puck", "World", LocalProfilesId.ToString());
    }
    // The current split layout: world/player.json (catalog), world/local.json (machine-local sidecar), and one
    // world/profiles/<id>.json blob per catalog entry.
    public static string WorldDir() {
        return Path.Combine(path1: StoreRoot(), path2: "world");
    }
    public static string CatalogPath() {
        return Path.Combine(path1: WorldDir(), path2: "player.json");
    }
    public static string LocalPath() {
        return Path.Combine(path1: WorldDir(), path2: "local.json");
    }
    public static string ProfilesDir() {
        return Path.Combine(path1: WorldDir(), path2: "profiles");
    }
}

readonly record struct DirectorySnapshot(string Root, IReadOnlyList<(string RelativePath, byte[] Bytes)> Files);

// Whole-subtree snapshot/clear/restore, byte-for-byte — the store's per-profile blob count is not known ahead of
// time, so a proof clearing/restoring individual filenames would miss any real profile beyond the
// four seeded defaults. Restoring means "the directory looks EXACTLY like it did" (including deleting anything the
// proof itself created that did not exist before).
static class DirectoryBackup {
    public static DirectorySnapshot Snapshot(string dir) {
        if (!Directory.Exists(path: dir)) {
            return new DirectorySnapshot(Root: dir, Files: []);
        }

        var files = Directory.EnumerateFiles(path: dir, searchPattern: "*", searchOption: SearchOption.AllDirectories)
            .Select(selector: file => (Path.GetRelativePath(relativeTo: dir, path: file), File.ReadAllBytes(path: file)))
            .ToList();

        return new DirectorySnapshot(Root: dir, Files: files);
    }

    public static void Clear(string dir) {
        try {
            if (Directory.Exists(path: dir)) {
                Directory.Delete(path: dir, recursive: true);
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[proof] WARNING: could not clear '{dir}' ({exception.Message})");
        }
    }

    public static void Restore(DirectorySnapshot snapshot) {
        try {
            if (Directory.Exists(path: snapshot.Root)) {
                Directory.Delete(path: snapshot.Root, recursive: true);
            }

            if (snapshot.Files.Count == 0) {
                return;
            }

            _ = Directory.CreateDirectory(path: snapshot.Root);

            foreach (var (relativePath, bytes) in snapshot.Files) {
                var path = Path.Combine(snapshot.Root, relativePath);
                var directory = Path.GetDirectoryName(path: path);

                if (!string.IsNullOrEmpty(value: directory)) {
                    _ = Directory.CreateDirectory(path: directory);
                }

                File.WriteAllBytes(path: path, bytes: bytes);
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[proof] WARNING: could not restore '{snapshot.Root}' ({exception.Message}) — the real player-document store may need manual repair.");
        }
    }
}

// ============================================================================================
// Feeder — build, launch, pace the corpus into stdin over the reader threads, mark each sweep, assert
// (closed-form / band / separation where present; report-only otherwise), evidence + transcript + code.
// ============================================================================================

static class Feeder {
    static readonly Regex FpsEcho = new(
        options: RegexOptions.Compiled,
        pattern: @"\[world\.fps: avg=([0-9]+(?:\.[0-9]+)?) worst=([0-9]+(?:\.[0-9]+)?) over \d+ frames");
    static readonly Regex QueuedMachineEcho = new(
        options: RegexOptions.Compiled,
        pattern: @"\[screen\.state: 4 assigned advanced-gaming-brick (?:bound|unbound) frames=(\d+) pending=(\d+)/(\d+) backpressure=(\d+)");

    public static int RunFeeder(ArgMap opts) {
        var headless = opts.Flag(name: "--headless");
        var loop = opts.Flag(name: "--loop");
        var noBuild = opts.Flag(name: "--no-build");
        var quality = (opts.Get(name: "--quality") ?? "low");
        var width = opts.GetInt(fallback: 2560, name: "--width");
        var height = opts.GetInt(fallback: 1440, name: "--height");
        var tolerance = opts.GetDouble(fallback: 0.12, name: "--tolerance");
        var yawTolerance = opts.GetDouble(fallback: 1.0, name: "--yaw-tolerance");
        var minimumFps = opts.GetDouble(fallback: 60.0, name: "--min-fps");
        var worldArg = opts.Get(name: "--world-arg");

        if (!double.IsFinite(d: minimumFps) || (minimumFps < 0.0)) {
            throw new ArgException(message: "--min-fps must be zero (disabled) or a finite positive frame rate");
        }

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        // --- corpus ---
        List<string> corpusLines;
        var corpusPath = opts.Get(name: "--corpus");
        var kind = (opts.Get(name: "--kind") ?? "expo");

        if (corpusPath is not null) {
            corpusLines = File.ReadAllLines(path: corpusPath).ToList();
        }
        else {
            var genOptions = new GenOptions(
                Population: Math.Clamp(opts.GetInt(fallback: 124, name: "--population"), 1, 124),
                Seed: opts.GetInt(fallback: 128, name: "--seed"),
                DurationSeconds: opts.GetDouble(fallback: 60.0, name: "--duration"),
                ControlRate: Math.Clamp(opts.GetInt(fallback: 5, name: "--control-rate"), 2, 20),
                ArenaRadius: opts.GetDouble(fallback: 40.0, name: "--arena"),
                CorrectionInterval: opts.GetDouble(fallback: 6.0, name: "--correction-interval"));

            corpusLines = Generators.Generate(kind: kind, o: genOptions);
        }

        var corpus = CorpusParser.Parse(lines: corpusLines);

        // The quality tier is a run parameter, not choreography — inject it right after setup (non-loop).
        corpus.Commands.Add(item: new TimedCommand(Command: $"world.quality {quality}", InLoop: false, T: 0.4));

        var maxT = ((corpus.Commands.Count > 0) ? corpus.Commands.Max(selector: c => c.T) : 0.0);
        var exitAfter = (headless ? (int)Math.Ceiling(a: (maxT + 6.0)) : 0);

        // --- build ---
        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var logPath = (opts.Get(name: "--log") ?? DefaultLogPath(kind: kind));

        // --- launch ---
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfter.ToString(provider: ProofApp.Inv));

        if (worldArg is not null) {
            psi.ArgumentList.Add(item: "--world");
            psi.ArgumentList.Add(item: worldArg);
        }

        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var started = false;
        var allPassed = true;

        // The child owns a GPU device and must NEVER be orphaned: Ctrl+C and process-exit both kill it, and the
        // finally below is the primary path.
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} --exit-after-seconds {exitAfter}{((worldArg is not null) ? $" --world {worldArg}" : "")}");
        Console.WriteLine(value: $"[proof] kind {kind} | mode {(headless ? "headless" : (loop ? "live + loop" : "live (one pass)"))} | quality {quality} | {width}x{height} | min FPS {(minimumFps > 0.0 ? ProofApp.F(value: minimumFps) : "disabled")} | log {logPath}");
        Console.WriteLine(value: $"[proof] corpus: {corpus.Commands.Count} commands, {corpus.Sweeps.Count} sweep(s)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);
            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new FeedContext(Collector: collector, Process: process, Stdin: stdin, Stopwatch: stopwatch, Tolerance: tolerance, YawTolerance: yawTolerance);

            var (alive, passed) = RunCycle(ctx, corpus.Commands, corpus.Sweeps, offset: 0.0, passLabel: "pass 1");
            allPassed &= passed;

            // The corpus is choreography, not a rejection table: every one of its lines is meant to apply. A refused
            // line no-ops silently and the sweep that follows it can still land within tolerance on the crowd's other
            // motion, so the count is the only witness.
            if (alive) {
                allPassed &= ComposedShotKit.SettleWireErrors(stdin: stdin, collector: collector, name: "corpus-refused-nothing", expected: 0);
            }

            if (alive && loop && !headless) {
                var inLoopCommands = corpus.Commands.Where(predicate: c => c.InLoop).ToList();
                var inLoopSweeps = corpus.Sweeps.Where(predicate: s => (s.T >= corpus.LoopStartT)).ToList();
                var cycleLength = ((corpus.CycleEnd - corpus.LoopStartT) + 2.0);
                var pass = 2;

                Console.WriteLine(value: $"[proof] looping choreography every {ProofApp.F(format: "0.0", value: cycleLength)}s — close the window to stop");

                while (!process.HasExited) {
                    var offset = ((pass - 1) * cycleLength);

                    (alive, passed) = RunCycle(ctx: ctx, events: inLoopCommands, offset: offset, passLabel: $"pass {pass}", sweeps: inLoopSweeps);
                    allPassed &= passed;

                    if (!alive) {
                        break;
                    }

                    pass++;
                }
            }
            else if (alive && !headless) {
                Console.WriteLine(value: "[proof] corpus complete — window is live, seats 1-4 are yours (close the window to end)");
            }

            if (started && !headless) {
                process.WaitForExit();
            }
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }

            Thread.Sleep(millisecondsTimeout: 300);
            var snapshot = collector.Snapshot();

            try {
                Directory.CreateDirectory(path: Path.GetDirectoryName(path: logPath)!);
                File.WriteAllLines(contents: snapshot, path: logPath);
            }
            catch (Exception ex) {
                Console.WriteLine(value: $"[proof] (could not write transcript: {ex.Message})");
            }

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === evidence block (world.fps / world.gpu) ===");

            foreach (var item in snapshot) {
                if (Regex.IsMatch(input: item, pattern: @"\] \[(world\.(fps|gpu|population|quality)|screen\.state: 4 )")) {
                    Console.WriteLine(value: $"[proof] {item}");
                }
            }

            allPassed &= AssertFrameRate(lines: snapshot, minimumFps: minimumFps);
            allPassed &= AssertQueuedMachineBacklog(lines: snapshot);

            Console.WriteLine(value: $"[proof] full transcript: {logPath}");
        }

        return (allPassed ? 0 : 1);
    }

    static bool AssertFrameRate(IReadOnlyList<string> lines, double minimumFps) {
        if (!(minimumFps > 0.0)) {
            Console.WriteLine(value: "[proof] FPS assertion disabled (--min-fps 0)");

            return true;
        }

        Match? last = null;

        foreach (var line in lines) {
            var match = FpsEcho.Match(input: line);

            if (match.Success) {
                last = match;
            }
        }

        if (last is null) {
            Console.WriteLine(value: $"[proof] FPS FAIL — no world.fps sample was emitted (need avg and worst >= {ProofApp.F(value: minimumFps)})");

            return false;
        }

        var average = double.Parse(s: last.Groups[1].ValueSpan, provider: ProofApp.Inv);
        var worst = double.Parse(s: last.Groups[2].ValueSpan, provider: ProofApp.Inv);
        var passed = ((average >= minimumFps) && (worst >= minimumFps));

        Console.WriteLine(value: $"[proof] FPS {(passed ? "PASS" : "FAIL")} — last rolling sample avg={ProofApp.F(format: "0.0", value: average)}, worst={ProofApp.F(format: "0.0", value: worst)}; require both >= {ProofApp.F(value: minimumFps)}");

        return passed;
    }

    // When a corpus asks for the default fifth screen's state, prove render decoupling did not merely hide an emulator
    // escaping its finite admitted pending window.
    static bool AssertQueuedMachineBacklog(IReadOnlyList<string> lines) {
        Match? last = null;

        foreach (var line in lines) {
            var match = QueuedMachineEcho.Match(input: line);

            if (match.Success) {
                last = match;
            }
        }

        if (last is null) {
            return true;
        }

        var completed = long.Parse(s: last.Groups[1].ValueSpan, provider: ProofApp.Inv);
        var pending = long.Parse(s: last.Groups[2].ValueSpan, provider: ProofApp.Inv);
        var capacity = int.Parse(s: last.Groups[3].ValueSpan, provider: ProofApp.Inv);
        var backpressure = long.Parse(s: last.Groups[4].ValueSpan, provider: ProofApp.Inv);
        var passed = ((completed > 0L) && (capacity > 0) && (pending <= capacity));

        Console.WriteLine(value: $"[proof] AGB queue {(passed ? "PASS" : "FAIL")} — completed={completed}, pending={pending}/{capacity}, backpressure={backpressure}; require completed > 0 and pending <= capacity");

        return passed;
    }

    sealed record FeedContext(Process Process, StreamWriter Stdin, Stopwatch Stopwatch, OutputCollector Collector,
        double Tolerance, double YawTolerance);

    // One cycle: send choreography, pausing at each sweep to mark, send its where lines, read poses, assert.
    static (bool Alive, bool Passed) RunCycle(FeedContext ctx, IReadOnlyList<TimedCommand> events,
        IReadOnlyList<Sweep> sweeps, double offset, string passLabel) {

        bool IsWhere(TimedCommand c) => c.Command.StartsWith(comparisonType: StringComparison.Ordinal, value: "player.where");

        var others = events.Where(predicate: c => !IsWhere(c: c)).OrderBy(keySelector: c => c.T).ToList();
        var sortedSweeps = sweeps.OrderBy(keySelector: s => s.T).ToList();
        var passed = true;

        if (sortedSweeps.Count == 0) {
            var alive = SendBatch(ctx, events.OrderBy(keySelector: c => c.T).ToList(), offset);

            return (alive, passed);
        }

        var otherIndex = 0;

        foreach (var sweep in sortedSweeps) {
            var batch = new List<TimedCommand>();

            while ((otherIndex < others.Count) && (others[otherIndex].T < sweep.T)) {
                batch.Add(item: others[otherIndex++]);
            }

            if (!SendBatch(batch: batch, ctx: ctx, offset: offset)) {
                return (false, passed);
            }

            var sweepWhere = events.Where(predicate: c => (IsWhere(c: c) && (Math.Abs(value: (c.T - sweep.T)) <= 0.3))).OrderBy(keySelector: c => c.T).ToList();
            var mark = ctx.Collector.Count;
            var alive = SendBatch(batch: sweepWhere, ctx: ctx, offset: offset);
            var poses = ReadPoses(collector: ctx.Collector, count: sweepWhere.Count, sinceIndex: mark);

            passed &= AssertSweep(passLabel: passLabel, poses: poses, sweep: sweep, tolerance: ctx.Tolerance, yawTolerance: ctx.YawTolerance);

            if (!alive) {
                return (false, passed);
            }
        }

        var tail = new List<TimedCommand>();

        while (otherIndex < others.Count) {
            tail.Add(item: others[otherIndex++]);
        }

        var tailAlive = SendBatch(batch: tail, ctx: ctx, offset: offset);

        return (tailAlive, passed);
    }

    // Group-write: every line already due is joined and written in one pipe write (per-line writes lag a flood burst,
    // starving the tape pre-buffer).
    static bool SendBatch(FeedContext ctx, List<TimedCommand> batch, double offset) {
        var builder = new StringBuilder();
        var index = 0;

        while (index < batch.Count) {
            var wait = ((batch[index].T + offset) - ctx.Stopwatch.Elapsed.TotalSeconds);

            if (wait > 0.0) {
                Thread.Sleep(millisecondsTimeout: (int)(wait * 1000.0));
            }

            if (ctx.Process.HasExited) {
                return false;
            }

            _ = builder.Clear();
            var now = ctx.Stopwatch.Elapsed.TotalSeconds;

            while ((index < batch.Count) && ((batch[index].T + offset) <= (now + 0.005))) {
                _ = builder.Append(value: batch[index].Command).Append(value: '\n');
                index++;
            }

            ctx.Collector.NoteDriven();

            try {
                ctx.Stdin.Write(value: builder.ToString());
            }
            catch (IOException) {
                return false;
            }
            catch (ObjectDisposedException) {
                return false;
            }
        }

        return true;
    }

    // Poll the collector for the player.where echoes that landed after <paramref name="sinceIndex"/> until <paramref
    // name="count"/> distinct poses are in (or a deadline passes). The LAST echo per player wins (a stable re-scan).
    static Dictionary<int, Pose> ReadPoses(OutputCollector collector, int sinceIndex, int count) {
        var poses = new Dictionary<int, Pose>();
        var deadline = DateTime.UtcNow.AddSeconds(value: 15);
        var previousCount = -1;

        while (true) {
            poses.Clear();
            var snapshot = collector.Snapshot();

            for (var i = sinceIndex; (i < snapshot.Length); i++) {
                var match = ProofApp.WhereEcho.Match(input: snapshot[i]);

                if (match.Success) {
                    poses[int.Parse(match.Groups[1].Value, ProofApp.Inv)] = new Pose(
                        X: double.Parse(match.Groups[2].Value, ProofApp.Inv),
                        Y: double.Parse(match.Groups[3].Value, ProofApp.Inv),
                        Z: double.Parse(match.Groups[4].Value, ProofApp.Inv),
                        Yaw: int.Parse(match.Groups[5].Value, ProofApp.Inv),
                        Pitch: int.Parse(match.Groups[6].Value, ProofApp.Inv),
                        Roll: int.Parse(match.Groups[7].Value, ProofApp.Inv));
                }
            }

            // Known target (asserted corpus) -> wait for it; unknown (report-only) -> wait until stable across two polls.
            var satisfied = ((count > 0)
                ? (poses.Count >= count)
                : ((poses.Count > 0) && (poses.Count == previousCount)));

            if (satisfied || (DateTime.UtcNow >= deadline)) {
                break;
            }

            previousCount = poses.Count;
            Thread.Sleep(millisecondsTimeout: 200);
        }

        return poses;
    }
    static double AngleDiff(int got, int want) {
        return Math.Abs(value: (((((got - want) % 360) + 540) % 360) - 180));
    }

    // Assert one sweep: closed-form pose #expects, per-entity #expect-band, and cross-class #expect-separation.
    // A sweep with NO asserts is report-only (captured for compare). Returns whether every present assertion passed.
    static bool AssertSweep(Sweep sweep, Dictionary<int, Pose> poses, double tolerance, double yawTolerance, string passLabel) {
        if (!sweep.HasAsserts) {
            Console.WriteLine(value: $"[proof] {passLabel} sweep '{sweep.Name}': report-only — {poses.Count} poses captured for compare");

            return true;
        }

        var allPassed = true;

        // --- closed-form pose tableau ---
        if (sweep.PoseExpects.Count > 0) {
            var failures = new List<string>();
            var passedCount = 0;

            foreach (var want in sweep.PoseExpects.OrderBy(keySelector: p => p.N)) {
                if (!poses.TryGetValue(key: want.N, value: out var got)) {
                    failures.Add(item: $"p{want.N}: no player.where echo captured");

                    continue;
                }

                var ok = ((Math.Abs(value: (got.X - want.X)) <= tolerance)
                    && (Math.Abs(value: (got.Y - want.Y)) <= tolerance)
                    && (Math.Abs(value: (got.Z - want.Z)) <= tolerance)
                    && (AngleDiff(got: got.Yaw, want: want.Yaw) <= yawTolerance)
                    && (AngleDiff(got: got.Pitch, want: want.Pitch) <= yawTolerance)
                    && (AngleDiff(got: got.Roll, want: want.Roll) <= yawTolerance));

                if (ok) {
                    passedCount++;
                }
                else {
                    failures.Add(item: string.Format(ProofApp.Inv,
                        "p{0}: expected ({1:0.00}, {2:0.00}, {3:0.00}) yaw={4} pitch={5} roll={6} got ({7:0.00}, {8:0.00}, {9:0.00}) yaw={10} pitch={11} roll={12}",
                        want.N, want.X, want.Y, want.Z, want.Yaw, want.Pitch, want.Roll,
                        got.X, got.Y, got.Z, got.Yaw, got.Pitch, got.Roll));
                }
            }

            var verdict = ((failures.Count == 0) ? "PASS" : "FAIL");

            Console.WriteLine(value: string.Format(ProofApp.Inv,
                "[proof] {0} sweep '{1}': tableau {2} — {3}/{4} entities exact (pos ±{5} u, yaw/pitch/roll ±{6}°)",
                passLabel, sweep.Name, verdict, passedCount, sweep.PoseExpects.Count, tolerance, yawTolerance));

            foreach (var failure in failures.Take(count: 10)) {
                Console.WriteLine(value: $"[proof]   {failure}");
            }

            if (failures.Count > 10) {
                Console.WriteLine(value: $"[proof]   ... and {(failures.Count - 10)} more");
            }

            allPassed &= (failures.Count == 0);
        }

        // --- per-entity bands + the class buckets the separation reads ---
        var classValues = new Dictionary<string, List<double>>(comparer: StringComparer.Ordinal);

        if (sweep.BandExpects.Count > 0) {
            var bandFailures = new List<string>();
            var bandPassed = 0;

            foreach (var band in sweep.BandExpects.OrderBy(keySelector: b => b.N)) {
                if (!poses.TryGetValue(key: band.N, value: out var got)) {
                    bandFailures.Add(item: $"p{band.N}: no where echo (band)");

                    continue;
                }

                var value = band.Axis switch { 'x' => got.X, 'z' => got.Z, _ => got.Y };

                if (!classValues.TryGetValue(key: band.Class, value: out var bucket)) {
                    bucket = new List<double>();
                    classValues[band.Class] = bucket;
                }

                bucket.Add(item: value);

                if ((value >= band.Lo) && (value <= band.Hi)) {
                    bandPassed++;
                }
                else {
                    bandFailures.Add(item: string.Format(ProofApp.Inv,
                        "p{0} ({1}): {2} {3:0.00} outside band [{4:0.00}, {5:0.00}]", band.N, band.Class, band.Axis, value, band.Lo, band.Hi));
                }
            }

            var verdict = ((bandFailures.Count == 0) ? "PASS" : "FAIL");

            Console.WriteLine(value: string.Format(ProofApp.Inv,
                "[proof] {0} sweep '{1}': bands {2} — {3}/{4} in band", passLabel, sweep.Name, verdict, bandPassed, sweep.BandExpects.Count));

            foreach (var failure in bandFailures.Take(count: 10)) {
                Console.WriteLine(value: $"[proof]   {failure}");
            }

            allPassed &= (bandFailures.Count == 0);
        }

        // --- cross-class separation (the variable-height proof: min(A) - max(B) > gap) ---
        foreach (var sep in sweep.Separations) {
            if (classValues.TryGetValue(key: sep.ClassA, value: out var a) && classValues.TryGetValue(key: sep.ClassB, value: out var b)
                && (a.Count > 0) && (b.Count > 0)) {
                var minA = a.Min();
                var maxB = b.Max();
                var gap = (minA - maxB);
                var ok = (gap > sep.MinGap);

                Console.WriteLine(value: string.Format(ProofApp.Inv,
                    "[proof] {0} sweep '{1}': separation {2} — min {3}({4}) {5:0.00} vs max {6}({7}) {8:0.00} (gap {9:0.00}, need > {10:0.00})",
                    passLabel, sweep.Name, (ok ? "PASS" : "FAIL"), sep.Axis, sep.ClassA, minA, sep.Axis, sep.ClassB, maxB, gap, sep.MinGap));
                allPassed &= ok;
            }
            else {
                Console.WriteLine(value: $"[proof] {passLabel} sweep '{sweep.Name}': separation SKIP — not enough {sep.ClassA}/{sep.ClassB} poses");
                allPassed = false;
            }
        }

        return allPassed;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static string DefaultLogPath(string kind) {
        var dir = Path.Combine(
            path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData),
            path2: "Puck", path3: "World", path4: "proof-logs");

        return Path.Combine(path1: dir, path2: $"proof-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us, but a race on exit is not an error.
        }
    }
}

// ============================================================================================
// Comparer — rerun byte/near-identity of two transcripts' final sweeps + dispersion statistics
// (the correctness bar for the stochastic karts/walkers/flood).
// ============================================================================================

static class Comparer {
    public static int RunCompare(ArgMap opts) {
        var referencePath = opts.GetRequired(name: "--reference");
        var candidatePath = opts.GetRequired(name: "--candidate");
        var tolerance = opts.GetDouble(fallback: 0.0, name: "--tolerance");
        var yawTolerance = opts.GetDouble(fallback: 0.0, name: "--yaw-tolerance");

        var reference = ReadSweep(path: referencePath);
        var candidate = ReadSweep(path: candidatePath);

        Console.WriteLine(value: $"[compare] reference: {reference.Count} poses | candidate: {candidate.Count} poses");

        var mismatches = new List<string>();
        var maxPositionDelta = 0.0;
        var maxAngleDelta = 0;

        foreach (var n in reference.Keys.OrderBy(keySelector: k => k)) {
            if (!candidate.TryGetValue(key: n, value: out var c)) {
                mismatches.Add(item: $"p{n}: missing from candidate");

                continue;
            }

            var r = reference[n];
            var positionDelta = Math.Sqrt(
                d: ((((r.Pose.X - c.Pose.X) * (r.Pose.X - c.Pose.X)) +
                ((r.Pose.Y - c.Pose.Y) * (r.Pose.Y - c.Pose.Y))) +
                ((r.Pose.Z - c.Pose.Z) * (r.Pose.Z - c.Pose.Z))));
            var angleDelta = Math.Max(val1: Math.Max(
                val1: AngleDiff(a: r.Pose.Yaw, b: c.Pose.Yaw),
                val2: AngleDiff(a: r.Pose.Pitch, b: c.Pose.Pitch)),
                val2: AngleDiff(a: r.Pose.Roll, b: c.Pose.Roll));

            maxPositionDelta = Math.Max(val1: maxPositionDelta, val2: positionDelta);
            maxAngleDelta = Math.Max(val1: maxAngleDelta, val2: angleDelta);

            var withinTolerance = ((tolerance <= 0.0)
                ? (c.Text == r.Text)
                : ((positionDelta <= tolerance) && (angleDelta <= yawTolerance)));

            if (!withinTolerance) {
                mismatches.Add(item: $"p{n}: '{r.Text}' vs '{c.Text}' (Δpos={Math.Round(digits: 3, value: positionDelta).ToString(provider: ProofApp.Inv)} Δang={angleDelta})");
            }
        }

        foreach (var n in candidate.Keys.Where(predicate: k => !reference.ContainsKey(key: k))) {
            mismatches.Add(item: $"p{n}: missing from reference");
        }

        var bar = ((tolerance <= 0.0) ? "byte-identity" : $"near-identity (±{ProofApp.F(tolerance)} u, ±{ProofApp.F(yawTolerance)}°)");

        if (mismatches.Count == 0) {
            Console.WriteLine(value: $"[compare] RERUN {bar} PASS — all {reference.Count} poses (observed max Δpos={Math.Round(digits: 3, value: maxPositionDelta).ToString(provider: ProofApp.Inv)} u, max Δang={maxAngleDelta}°)");
        }
        else {
            Console.WriteLine(value: $"[compare] RERUN {bar} FAIL — {mismatches.Count} mismatches (max Δpos={Math.Round(digits: 3, value: maxPositionDelta).ToString(provider: ProofApp.Inv)} u, max Δang={maxAngleDelta}°):");

            foreach (var mismatch in mismatches.Take(count: 10)) {
                Console.WriteLine(value: $"[compare]   {mismatch}");
            }
        }

        PrintDispersion(reference: reference);

        return ((mismatches.Count == 0) ? 0 : 1);
    }

    static void PrintDispersion(Dictionary<int, (Pose Pose, string Text)> reference) {
        if (reference.Count <= 1) {
            return;
        }

        var xs = reference.Values.Select(selector: v => v.Pose.X).ToList();
        var zs = reference.Values.Select(selector: v => v.Pose.Z).ToList();
        var cx = xs.Average();
        var cz = zs.Average();
        var radii = reference.Values
            .Select(selector: v => Math.Sqrt(d: (((v.Pose.X - cx) * (v.Pose.X - cx)) + ((v.Pose.Z - cz) * (v.Pose.Z - cz)))))
            .OrderBy(keySelector: r => r)
            .ToList();
        var rms = Math.Sqrt(d: (radii.Select(selector: r => (r * r)).Sum() / radii.Count));

        double Q(double p) => radii[(int)Math.Floor(d: ((radii.Count - 1) * p))];

        Console.WriteLine(value: string.Format(ProofApp.Inv,
            "[compare] dispersion: centroid=({0:0.0}, {1:0.0}) | radius-from-centroid q25/q50/q75/max = {2:0.0}/{3:0.0}/{4:0.0}/{5:0.0} | rms {6:0.0}",
            cx, cz, Q(p: 0.25), Q(p: 0.5), Q(p: 0.75), radii[^1], rms));
        Console.WriteLine(value: "[compare] (uniform disc of radius R has q50 ≈ 0.71R, rms ≈ 0.71R — compare against the corpus arena radius)");
    }
    static int AngleDiff(int a, int b) {
        return Math.Abs(value: (((((a - b) % 360) + 540) % 360) - 180));
    }

    // The last where echo per player across the whole transcript — the final sweep.
    static Dictionary<int, (Pose Pose, string Text)> ReadSweep(string path) {
        var poses = new Dictionary<int, (Pose, string)>();

        foreach (var line in File.ReadLines(path: path)) {
            var match = ProofApp.WhereEcho.Match(input: line);

            if (match.Success) {
                var n = int.Parse(match.Groups[1].Value, ProofApp.Inv);
                var pose = new Pose(
                    X: double.Parse(match.Groups[2].Value, ProofApp.Inv),
                    Y: double.Parse(match.Groups[3].Value, ProofApp.Inv),
                    Z: double.Parse(match.Groups[4].Value, ProofApp.Inv),
                    Yaw: int.Parse(match.Groups[5].Value, ProofApp.Inv),
                    Pitch: int.Parse(match.Groups[6].Value, ProofApp.Inv),
                    Roll: int.Parse(match.Groups[7].Value, ProofApp.Inv));
                // The exact text (last occurrence wins) — the byte-identity discriminator.
                var text = $"p{n} ({match.Groups[2].Value}, {match.Groups[3].Value}, {match.Groups[4].Value}) {match.Groups[5].Value} {match.Groups[6].Value} {match.Groups[7].Value}";

                poses[n] = (pose, text);
            }
        }

        return poses;
    }
}

// ============================================================================================
// WORLDDOC — the world-document proofs (puck.world.def.v1):
//   (a) the save-idempotence gate: every checked-in Assets/worlds/*.world.json boots, saves, and
//       re-saves from its own output to the same bytes (the writer is a fixed point). The saved
//       file is NOT compared against the checked-in one — a shipped world's JSON gaining a key is
//       fine (R18), so byte identity against a repo file is not acceptance criteria.
//   (b) baked-default parity: booting from the checked-in file and booting from a missing
//       --world path (the loud baked-default fallback) must simulate byte-identically over the
//       same short corpus, with the "[world] definition: baked default (...)" line present only
//       in the fallback run. Reuses the Feeder/Comparer machinery rather than reimplementing it.
// ============================================================================================

static class WorldDocProof {
    public static int RunWorldDoc(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 12, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");
        var checkedInPath = Path.Combine(path1: projectPath, path2: "Assets", path3: "worlds", path4: "default.world.json");

        if (!File.Exists(path: checkedInPath)) {
            return ProofApp.Fail(message: $"checked-in world file not found: {checkedInPath}");
        }

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        Console.WriteLine(value: "[proof] === worlddoc (a): the save-idempotence gate (every checked-in world) ===");

        // Cover EVERY checked-in world: a save that folds session state must be a fixed point of the writer on a
        // fresh boot for each. Missing files are skipped with a note rather than failing the gate.
        var worldsDir = Path.Combine(path1: projectPath, path2: "Assets", path3: "worlds");
        var ouroborosPassed = true;

        foreach (var worldName in new[] { "default.world.json", "kart-remap.world.json", "expo.world.json", "kiosk.world.json", "planetoid.world.json" }) {
            var worldPath = Path.Combine(path1: worldsDir, path2: worldName);

            if (!File.Exists(path: worldPath)) {
                Console.WriteLine(value: $"[proof]   (skip) {worldName} not present — author it first (proof.cs expo-author)");

                continue;
            }

            Console.WriteLine(value: $"[proof]   -- {worldName} --");
            ouroborosPassed &= RunOuroboros(checkedInPath: worldPath, exe: exe, exitAfterSeconds: exitAfterSeconds, height: height, repoRoot: repoRoot, width: width);
        }

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === worlddoc (b): the baked default boots, runs, and is deterministic ===");
        var parityPassed = RunBakedDefaultParity(checkedInPath: checkedInPath);

        var passed = (ouroborosPassed && parityPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] worlddoc proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // world.save(checked-in) must be a fixed point: saving again from that output reproduces it exactly. The
    // checked-in bytes are REPORTED, never asserted — a shipped world may legitimately gain a key (R18).
    static bool RunOuroboros(string exe, string repoRoot, string checkedInPath, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var temp1 = Path.Combine(Path.GetTempPath(), $"puck-world-ouroboros-1-{pid}.json");
        var temp2 = Path.Combine(Path.GetTempPath(), $"puck-world-ouroboros-2-{pid}.json");

        if (!LaunchAndSave(exe: exe, repoRoot: repoRoot, worldArg: checkedInPath, savePath: temp1, width: width, height: height, exitAfterSeconds: exitAfterSeconds)) {
            return false;
        }

        var checkedInBytes = File.ReadAllBytes(path: checkedInPath);
        var temp1Bytes = File.ReadAllBytes(path: temp1);
        var checkedInHash = Convert.ToHexStringLower(SHA256.HashData(source: checkedInBytes));
        var temp1Hash = Convert.ToHexStringLower(SHA256.HashData(source: temp1Bytes));
        var matchesCheckedIn = string.Equals(a: checkedInHash, b: temp1Hash, comparisonType: StringComparison.Ordinal);

        Console.WriteLine(value: $"[proof]   (note) checked-in vs world.save(checked-in): {(matchesCheckedIn ? "identical" : "DIFFERS — re-save the file if you want it current")} | {checkedInBytes.Length} vs {temp1Bytes.Length} bytes | sha256 {checkedInHash[..12]} vs {temp1Hash[..12]} ({temp1})");

        if (!LaunchAndSave(exe: exe, repoRoot: repoRoot, worldArg: temp1, savePath: temp2, width: width, height: height, exitAfterSeconds: exitAfterSeconds)) {
            return false;
        }

        var temp2Bytes = File.ReadAllBytes(path: temp2);
        var temp2Hash = Convert.ToHexStringLower(SHA256.HashData(source: temp2Bytes));
        var stage2Ok = string.Equals(a: temp1Hash, b: temp2Hash, comparisonType: StringComparison.Ordinal);

        Console.WriteLine(value: $"[proof]   {(stage2Ok ? "PASS" : "FAIL")} world.save is a fixed point — world.save(checked-in) == world.save(world.save(checked-in)): {temp1Bytes.Length} vs {temp2Bytes.Length} bytes | sha256 {temp1Hash[..12]} vs {temp2Hash[..12]} ({temp2})");

        return stage2Ok;
    }

    // Launch Puck.World with --world <worldArg>, wait for the console to be routing commands, issue
    // world.save <savePath>, and confirm the echo reports a write (not an error). The child window is small and
    // bounded by --exit-after-seconds; the whole call is a one-shot boot/save/exit.
    static bool LaunchAndSave(string exe, string repoRoot, string worldArg, string savePath, int width, int height, int exitAfterSeconds) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        psi.ArgumentList.Add(item: "--world");
        psi.ArgumentList.Add(item: worldArg);
        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        var process = new Process { StartInfo = psi };
        var collector = new OutputCollector();
        var stopwatch = new Stopwatch();
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            var mark = collector.Count;

            Send(ctx: ctx, line: $"world.save {savePath}");

            var line = Await(collector: collector, mark: mark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);
            var saved = Check(name: "world.save", ok: ((line is not null) && !line.Contains(value: "could not write")), detail: (line?.Trim() ?? "(no world.save echo)"));

            return (saved & ComposedShotKit.SettleWireErrors(stdin: stdin, collector: collector, name: "save-round-refused-nothing", expected: 0));
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }
    }

    // Three feeder runs of the SAME short deterministic corpus (kind hop, small population): one against the checked-in
    // world file, then TWO against `--world baked` — the EXPLICIT request for the in-code definition. (The baked run
    // used to be forced with a nonexistent path, which stopped working the day a missing --world path became a loud
    // boot failure; the proof was depending on the bug.)
    //
    // What is asserted, and what is only reported:
    //   * The in-code document BOOTS and RUNS the corpus — full pose coverage on both baked runs, and the loud
    //     "baked default" line present in exactly those two and never in the checked-in run.
    //   * DETERMINISM, which is a per-document property: the two baked runs must compare byte-identical. Same
    //     document + same input -> the same final sweep, which is the claim worth gating.
    //   * The checked-in vs baked comparison is a NOTE. Assets/worlds/default.world.json is a DIFFERENT world from
    //     WorldDefinition.Default — it declares solidity facets and a collision response table the in-code document
    //     does not — so their entities legitimately land in different places. Asserting identity there would only
    //     pressure someone to re-golden a shipped world to keep a number stable (R18).
    //
    // The gate does NOT require the feeder's own in-flight #expect-band assertion to pass: that assertion samples a
    // ~0.2s mid-air window and is sensitive to host render throughput (it can fail independently of the world
    // document — see the worlddoc report). The pose-coverage check guards the compare so a crashed/empty transcript
    // cannot vacuously pass an empty comparison.
    static bool RunBakedDefaultParity(string checkedInPath) {
        const int population = 8;
        var pid = Environment.ProcessId;
        var logA = Path.Combine(Path.GetTempPath(), $"puck-world-worlddoc-checked-in-{pid}.log");
        var logB = Path.Combine(Path.GetTempPath(), $"puck-world-worlddoc-baked-default-{pid}.log");
        var logC = Path.Combine(Path.GetTempPath(), $"puck-world-worlddoc-baked-default-rerun-{pid}.log");

        Console.WriteLine(value: "[proof]   run A: --world <checked-in file>");
        var codeA = RunHopCorpus(logPath: logA, population: population, worldArg: checkedInPath);

        Console.WriteLine();
        Console.WriteLine(value: "[proof]   run B: --world baked (the in-code definition, requested by name)");
        var codeB = RunHopCorpus(logPath: logB, population: population, worldArg: "baked");

        Console.WriteLine();
        Console.WriteLine(value: "[proof]   run C: --world baked again (the determinism rerun)");
        var codeC = RunHopCorpus(logPath: logC, population: population, worldArg: "baked");

        Console.WriteLine();
        Console.WriteLine(value: $"[proof]   (info) feeder exit codes — run A={codeA}, run B={codeB}, run C={codeC} (their in-flight #expect-band assertion is NOT the worlddoc gate; see comment above)");

        var posesA = DistinctPoseCount(logPath: logA);
        var posesB = DistinctPoseCount(logPath: logB);
        var posesC = DistinctPoseCount(logPath: logC);
        var coveragePassed = ((posesA == population) && (posesB == population) && (posesC == population));

        Console.WriteLine(value: $"[proof]   {(coveragePassed ? "PASS" : "FAIL")} pose coverage — run A captured {posesA}/{population}, run B {posesB}/{population}, run C {posesC}/{population}");

        var determinismPassed = (Comparer.RunCompare(opts: new ArgMap(args: ["--reference", logB, "--candidate", logC])) == 0);

        Console.WriteLine(value: $"[proof]   {(determinismPassed ? "PASS" : "FAIL")} baked-document determinism — run B vs run C final sweeps byte-identical");
        Console.WriteLine(value: "[proof]   (note) run A vs run B — a different DOCUMENT, not a rerun; the shipped default declares solidity the in-code one does not, so a pose delta here is content, never a regression:");
        _ = Comparer.RunCompare(opts: new ArgMap(args: ["--reference", logA, "--candidate", logB]));

        var bakedInA = File.ReadLines(path: logA).Any(predicate: l => l.Contains(value: "baked default"));
        var bakedInB = File.ReadLines(path: logB).Any(predicate: l => l.Contains(value: "baked default"));
        var bakedInC = File.ReadLines(path: logC).Any(predicate: l => l.Contains(value: "baked default"));
        var loudLinePassed = (!bakedInA && bakedInB && bakedInC);

        Console.WriteLine(value: $"[proof]   {(loudLinePassed ? "PASS" : "FAIL")} loud baked-default line — run A (checked-in) baked-default={bakedInA} (want false), runs B/C (--world baked) baked-default={bakedInB}/{bakedInC} (want true)");
        Console.WriteLine(value: $"[proof]   transcripts: {logA} | {logB} | {logC}");

        return (coveragePassed && determinismPassed && loudLinePassed);
    }

    // One feeder pass of the shared short hop corpus against a given --world value.
    static int RunHopCorpus(string logPath, int population, string worldArg) {
        return Feeder.RunFeeder(opts: new ArgMap(args: [
            "--kind", "hop", "--population", population.ToString(provider: ProofApp.Inv), "--headless", "--no-build",
            "--quality", "low", "--width", "640", "--height", "480", "--min-fps", "0",
            "--log", logPath, "--world-arg", worldArg,
        ]));
    }

    // The count of distinct players whose final pose landed in the transcript (the last player.where echo per
    // player wins, matching Comparer.ReadSweep) — a crash or an empty run reads back 0, never a silent vacuous pass.
    static int DistinctPoseCount(string logPath) {
        var seen = new HashSet<int>();

        foreach (var line in File.ReadLines(path: logPath)) {
            var match = ProofApp.WhereEcho.Match(input: line);

            if (match.Success) {
                _ = seen.Add(item: int.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv));
            }
        }

        return seen.Count;
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    // Wait for the router to be live: player.stop is idempotent at boot, so its echo is the readiness signal, matching
    // ScreensProof.WaitForConsole — cold shader compilation can exceed an individual assertion's deadline.
    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }
    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// EXPO — the second world (a genre-neutrality proof artifact) + session write-back:
//   expo-author regenerates Assets/worlds/expo.world.json THE HONEST WAY (a scripted stdin
//     session of mutations + live session levers fed to a booted baked-default world, then
//     world.save folds the render levers + census into the saved document).
//   expodoc proves the artifact: (a) --world expo boots the loud definition line; (b) a
//     distinguishing world.status fact (expo's kit/screen counts differ from the default's,
//     zero code); (c) the write-back slice not covered by MutateProof — the peer-source-default
//     session lever (world.population idle) survives world.save + a relaunch, while the
//     networkPlayers admission cap stays durable (R-C). Expo's own ouroboros is in worlddoc.
// ============================================================================================
static class ExpoProof {
    // The distinguishing world.status counts the authoring session produces (5 default kits + "glider" = 6; 5 default
    // screens minus indices 4 and 3 = 3) — a visibly different world with zero code.
    const string ExpoStatusNeedle = "kits 6 screens 3";
    // The authored remote-admission CAP the authoring session bakes in (world.population.defaults network 32). Under R-C
    // networkPlayers is a durable cap, NOT the live census count — a census raise never folds into it, so the write-back
    // slice proves it stays put.
    const int ExpoAuthoredNetworkPlayers = 32;
    // The census raise the write-back requests PAST the cap — proves both the clamp and that the transient running count
    // is never persisted (R-C: the live census is session-only).
    const int WriteBackCensusRequest = 48;
    // The peer-source default the write-back flips (from the authored 'wander') — the session lever world.save DOES fold,
    // so it survives save + relaunch. Its verb token and its serialized JSON enum name.
    const string WriteBackPeerSource = "idle";
    const string WriteBackPeerSourceJson = "Idle";

    public static int RunExpoAuthor(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");
        var scriptPath = Path.Combine(path1: projectPath, path2: "scripts", path3: "expo-world.txt");
        var outPath = (opts.Get(name: "--out") ?? Path.Combine(path1: projectPath, path2: "Assets", path3: "worlds", path4: "expo.world.json"));

        if (!File.Exists(path: scriptPath)) {
            return ProofApp.Fail(message: $"authoring script not found: {scriptPath}");
        }

        if (!EnsureBuilt(noBuild: noBuild, projectPath: projectPath, exe: out var exe)) {
            return 1;
        }

        var commands = File.ReadAllLines(path: scriptPath)
            .Select(selector: static line => line.Trim())
            .Where(predicate: static line => ((line.Length > 0) && !line.StartsWith(value: '#')))
            .ToArray();

        Console.WriteLine(value: $"[proof] authoring {outPath} from {commands.Length} script commands (baked-default boot)...");

        var authored = Author(exe: exe, repoRoot: repoRoot, commands: commands, outPath: outPath, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        if (authored) {
            var bytes = File.ReadAllBytes(path: outPath);

            Console.WriteLine(value: $"[proof] expo-author PASS — {outPath} ({bytes.Length} bytes, sha256 {Convert.ToHexStringLower(SHA256.HashData(source: bytes))[..12]})");

            return 0;
        }

        Console.WriteLine(value: "[proof] expo-author FAIL");

        return 1;
    }

    public static int RunExpoDoc(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");
        var expoPath = Path.Combine(path1: projectPath, path2: "Assets", path3: "worlds", path4: "expo.world.json");

        if (!File.Exists(path: expoPath)) {
            return ProofApp.Fail(message: $"expo world not found: {expoPath} — author it first (proof.cs expo-author)");
        }

        if (!EnsureBuilt(noBuild: noBuild, projectPath: projectPath, exe: out var exe)) {
            return 1;
        }

        // The authored artifact must carry the folded census (world.population 32) — the honest write-back baked into the
        // checked-in file, read straight from the JSON.
        var authoredNetwork = ExtractNetworkPlayers(json: File.ReadAllText(path: expoPath));
        var authoredOk = Check(name: "authored-census-folded", ok: (authoredNetwork == ExpoAuthoredNetworkPlayers),
            detail: $"expo networkPlayers = {(authoredNetwork?.ToString(provider: ProofApp.Inv) ?? "?")} (want {ExpoAuthoredNetworkPlayers})");

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === expodoc (a+b): expo boots, loud + visibly different ===");
        var tempSave = Path.Combine(Path.GetTempPath(), $"puck-world-expo-writeback-{Environment.ProcessId}.json");
        var bootOk = RunBootAndWriteBack(exe: exe, repoRoot: repoRoot, expoPath: expoPath, tempSave: tempSave, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === expodoc (c): the write-back slice survives a relaunch ===");
        var survivalOk = (File.Exists(path: tempSave) && RunWriteBackSurvival(exe: exe, repoRoot: repoRoot, savedPath: tempSave, width: width, height: height, exitAfterSeconds: exitAfterSeconds));

        if (!File.Exists(path: tempSave)) {
            Console.WriteLine(value: $"[proof]   FAIL survival: {tempSave} was never written (write-back save did not complete)");
        }

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === expodoc (d): a runtime screen.insert folds into the screen's Machine source ===");
        var insertOk = RunScreenInsertFold(exe: exe, repoRoot: repoRoot, expoPath: expoPath, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        var passed = (authoredOk && bootOk && survivalOk && insertOk);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] expodoc proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // Boot the baked default, drive the authoring script over stdin, world.save to the artifact path. The mutation verbs
    // are Simulation-routed (buffered, drained in FIFO order behind the stdin barrier); world.save is Immediate, so the
    // barrier holds it until every buffered mutation has applied — the same read-after-write the mutate proof relies on.
    static bool Author(string exe, string repoRoot, string[] commands, string outPath, int width, int height, int exitAfterSeconds) {
        var psi = BaseStartInfo(exe: exe, repoRoot: repoRoot);

        AddArg(psi: psi, name: "--width", value: width);
        AddArg(psi: psi, name: "--height", value: height);
        AddArg(psi: psi, name: "--exit-after-seconds", value: exitAfterSeconds);

        var process = new Process { StartInfo = psi };
        var collector = new OutputCollector();
        var stopwatch = new Stopwatch();
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            foreach (var command in commands) {
                Send(ctx: ctx, line: command);
            }

            var mark = collector.Count;

            Send(ctx: ctx, line: $"world.save {outPath}");

            var line = Await(collector: collector, mark: mark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 45.0);
            var saved = Check(name: "authoring-save", ok: ((line is not null) && !line.Contains(value: "could not write")), detail: (line?.Trim() ?? "(no world.save echo)"));

            // Every line of the authoring script is meant to APPLY. Without this the script's failure mode is silent:
            // a retired payload shape or a misspelled verb is refused, the artifact is written anyway missing whatever
            // that line authored, and the save echo above still reads green.
            return (saved & ComposedShotKit.SettleWireErrors(stdin: stdin, collector: collector, name: "authoring-refused-nothing", expected: 0));
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }
    }

    // Session 1: boot --world expo, assert the loud definition line, assert the distinguishing world.status counts, then
    // exercise the write-back — raise the live census past the authored cap and flip the peer-source default
    // (world.population), world.save to a temp copy, and assert the cap stays durable while the peer-source folds.
    static bool RunBootAndWriteBack(string exe, string repoRoot, string expoPath, string tempSave, int width, int height, int exitAfterSeconds) {
        var psi = BaseStartInfo(exe: exe, repoRoot: repoRoot);

        AddArg(psi: psi, name: "--world", value: expoPath);
        AddArg(psi: psi, name: "--width", value: width);
        AddArg(psi: psi, name: "--height", value: height);
        AddArg(psi: psi, name: "--exit-after-seconds", value: exitAfterSeconds);

        var process = new Process { StartInfo = psi };
        var collector = new OutputCollector();
        var stopwatch = new Stopwatch();
        var started = false;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --world {expoPath}");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            // (a) the loud boot line names the expo file — the loader's one-line origin.
            var bootLine = Await(collector: collector, mark: 0, predicate: l => (l.Contains(value: "[world] definition:") && l.Contains(value: expoPath)), deadlineSeconds: 30.0);

            passed &= Check(name: "boots-from-expo", ok: (bootLine is not null), detail: (bootLine?.Trim() ?? $"(no '[world] definition: {expoPath}' boot line)"));

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            // (b) the distinguishing world.status fact — expo's kit/screen counts differ from the default's, and a fresh
            // boot has no session drift (the authored render levers/census equal the document defaults).
            var statusMark = collector.Count;

            Send(ctx: ctx, line: "world.status");

            var statusLine = Await(collector: collector, mark: statusMark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);

            passed &= Check(name: "distinguishing-status", ok: ((statusLine is not null) && statusLine.Contains(value: ExpoStatusNeedle)),
                detail: (statusLine?.Trim() ?? "(no world.status echo)"));
            passed &= Check(name: "fresh-boot-no-drift", ok: ((statusLine is not null) && statusLine.Contains(value: "session-drift none")),
                detail: (statusLine?.Trim() ?? "(no world.status echo)"));

            // (c-i) exercise the R-C write-back contract with two session levers, then world.save to a temp copy — the
            // write-back the mutate proof does not cover (it changes a JOURNALED kit; this changes SESSION state folded
            // only at save). Raise the live census PAST the authored cap (proves the clamp + that the transient count is
            // never persisted) and flip the peer-source default to idle (the lever world.save DOES fold).
            Send(ctx: ctx, line: $"world.population {WriteBackCensusRequest} {WriteBackPeerSource}");

            var saveMark = collector.Count;

            Send(ctx: ctx, line: $"world.save {tempSave}");

            var saveLine = Await(collector: collector, mark: saveMark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);

            passed &= Check(name: "write-back-save", ok: ((saveLine is not null) && !saveLine.Contains(value: "could not write")), detail: (saveLine?.Trim() ?? "(no world.save echo)"));

            // The saved copy's networkPlayers must stay the authored ADMISSION CAP (R-C: a census raise is transient and
            // never folds), and the peer-source default must have folded to idle (the surviving session lever).
            if (File.Exists(path: tempSave)) {
                var savedJson = File.ReadAllText(path: tempSave);
                var savedNetwork = ExtractNetworkPlayers(json: savedJson);
                var savedSource = ExtractDefaultPeerSource(json: savedJson);

                passed &= Check(name: "admission-cap-stays-durable", ok: (savedNetwork == ExpoAuthoredNetworkPlayers),
                    detail: $"saved networkPlayers = {(savedNetwork?.ToString(provider: ProofApp.Inv) ?? "?")} (want the authored cap {ExpoAuthoredNetworkPlayers}, NOT the transient census {WriteBackCensusRequest})");
                passed &= Check(name: "peer-source-folded-into-json", ok: string.Equals(a: savedSource, b: WriteBackPeerSourceJson, comparisonType: StringComparison.OrdinalIgnoreCase),
                    detail: $"saved defaultPeerSource = {savedSource ?? "?"} (want {WriteBackPeerSourceJson})");
            }

            passed &= ComposedShotKit.SettleWireErrors(stdin: stdin, collector: collector, name: "write-back-round-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // Session 2: relaunch --world <the write-back copy> and assert the flipped peer-source default survived the fold +
    // relaunch — the live world.population verb echoes its behavior, no code edit, no restart of the change. (R-C: the
    // census COUNT is transient and not persisted; the peer-source default is the session lever that folds and survives.)
    static bool RunWriteBackSurvival(string exe, string repoRoot, string savedPath, int width, int height, int exitAfterSeconds) {
        var psi = BaseStartInfo(exe: exe, repoRoot: repoRoot);

        AddArg(psi: psi, name: "--world", value: savedPath);
        AddArg(psi: psi, name: "--width", value: width);
        AddArg(psi: psi, name: "--height", value: height);
        AddArg(psi: psi, name: "--exit-after-seconds", value: exitAfterSeconds);

        var process = new Process { StartInfo = psi };
        var collector = new OutputCollector();
        var stopwatch = new Stopwatch();
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --world {savedPath}");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            var mark = collector.Count;

            Send(ctx: ctx, line: "world.population");

            var line = Await(collector: collector, mark: mark, predicate: l => l.Contains(value: "[world.population:"), deadlineSeconds: 15.0);
            var survived = Check(name: "peer-source-survived-relaunch", ok: ((line is not null) && line.Contains(value: $"behavior {WriteBackPeerSource}")),
                detail: (line?.Trim() ?? "(no world.population echo)"));

            return (survived & ComposedShotKit.SettleWireErrors(stdin: stdin, collector: collector, name: "survival-round-refused-nothing", expected: 0));
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }
    }

    // The third write-back dimension, positively (expo/default are asset-free, so the ouroboros only exercises the
    // no-live-machine no-op). Boot expo, boot a REAL joypad-echo ROM onto a declared screen via screen.insert, assert
    // world.status now names the 'screens' drift dimension, then world.save and assert the saved JSON carries a machine
    // source referencing that ROM — the live binder insert folded into the screen row. One self-contained session.
    static bool RunScreenInsertFold(string exe, string repoRoot, string expoPath, int width, int height, int exitAfterSeconds) {
        const int insertScreen = 1;
        var romPath = Path.Combine(Path.GetTempPath(), $"puck-world-expo-insert-{Environment.ProcessId}.gb");
        var romBase = Path.GetFileName(path: romPath);
        var tempSave = Path.Combine(Path.GetTempPath(), $"puck-world-expo-insert-save-{Environment.ProcessId}.json");

        File.WriteAllBytes(bytes: ScreensProof.BuildJoypadEchoRom(), path: romPath);

        var psi = BaseStartInfo(exe: exe, repoRoot: repoRoot);

        AddArg(psi: psi, name: "--world", value: expoPath);
        AddArg(psi: psi, name: "--width", value: width);
        AddArg(psi: psi, name: "--height", value: height);
        AddArg(psi: psi, name: "--exit-after-seconds", value: exitAfterSeconds);

        var process = new Process { StartInfo = psi };
        var collector = new OutputCollector();
        var stopwatch = new Stopwatch();
        var started = false;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --world {expoPath} (screen.insert {insertScreen} {romBase})");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Boot the ROM onto a declared expo screen (an overlay on the slot — no new surface, no envelope growth).
            var insertMark = collector.Count;

            Send(ctx: ctx, line: $"screen.insert {insertScreen} {romPath} gaming-brick");

            var insertLine = Await(collector: collector, mark: insertMark, predicate: l => l.Contains(value: "[screen.insert:"), deadlineSeconds: 20.0);

            passed &= Check(name: "insert-ok", ok: ((insertLine is not null) && insertLine.Contains(value: "booted")), detail: (insertLine?.Trim() ?? "(no screen.insert echo)"));

            // The live insert is session state → world.status names the 'screens' drift dimension (the DescribeDrift
            // screen branch, positively).
            var statusMark = collector.Count;

            Send(ctx: ctx, line: "world.status");

            var statusLine = Await(collector: collector, mark: statusMark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);

            passed &= Check(name: "insert-drifts-screens", ok: ((statusLine is not null) && statusLine.Contains(value: "session-drift") && statusLine.Contains(value: "screens")),
                detail: (statusLine?.Trim() ?? "(no world.status echo)"));

            // Save — the fold writes the live machine into screen row's source.
            var saveMark = collector.Count;

            Send(ctx: ctx, line: $"world.save {tempSave}");

            var saveLine = Await(collector: collector, mark: saveMark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);

            passed &= Check(name: "insert-save", ok: ((saveLine is not null) && !saveLine.Contains(value: "could not write")), detail: (saveLine?.Trim() ?? "(no world.save echo)"));

            if (File.Exists(path: tempSave)) {
                // Expo carries NO machine source normally, so a machine-typed source naming this ROM proves the fold.
                var savedJson = File.ReadAllText(path: tempSave);
                var folded = (savedJson.Contains(value: "\"$type\": \"machine\"") && savedJson.Contains(value: "gaming-brick") && savedJson.Contains(value: romBase));

                passed &= Check(name: "insert-folded-into-json", ok: folded, detail: (folded ? $"screen {insertScreen} source is a machine → {romBase}" : "no machine source naming the inserted ROM in the saved JSON"));
            }
            else {
                passed &= Check(name: "insert-folded-into-json", ok: false, detail: $"{tempSave} was never written");
            }

            passed &= ComposedShotKit.SettleWireErrors(stdin: stdin, collector: collector, name: "insert-fold-round-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }

            try {
                if (File.Exists(path: romPath)) {
                    File.Delete(path: romPath);
                }
            }
            catch {
                // best-effort temp cleanup.
            }
        }

        return passed;
    }

    // The population block's networkPlayers value — the canonical writer's stable order puts it right after localPlayers,
    // so this first-after-"networkPlayers" number is unambiguous without a full parse (the same approach ExtractKitMoveSpeed uses).
    static int? ExtractNetworkPlayers(string json) {
        var match = Regex.Match(input: json, pattern: @"""networkPlayers""\s*:\s*(\d+)");

        return (match.Success ? int.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv) : null);
    }

    // The population block's defaultPeerSource enum name (the writer serializes the IntentSource as its member name, e.g.
    // "Idle"/"Wander") — the peer-source session lever the write-back folds. Null when the key is absent.
    static string? ExtractDefaultPeerSource(string json) {
        var match = Regex.Match(input: json, pattern: @"""defaultPeerSource""\s*:\s*""([^""]+)""");

        return (match.Success ? match.Groups[1].Value : null);
    }

    static bool EnsureBuilt(bool noBuild, string projectPath, out string exe) {
        exe = "";

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                _ = ProofApp.Fail(message: $"build failed ({build.ExitCode})");

                return false;
            }
        }

        var found = FindExe(projectPath: projectPath);

        if (found is null) {
            _ = ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");

            return false;
        }

        exe = found;

        return true;
    }

    static ProcessStartInfo BaseStartInfo(string exe, string repoRoot) => new() {
        FileName = exe,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = repoRoot,
    };

    static void AddArg(ProcessStartInfo psi, string name, int value) {
        psi.ArgumentList.Add(item: name);
        psi.ArgumentList.Add(item: value.ToString(provider: ProofApp.Inv));
    }

    static void AddArg(ProcessStartInfo psi, string name, string value) {
        psi.ArgumentList.Add(item: name);
        psi.ArgumentList.Add(item: value);
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }

    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }

    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// SCREENS — the diegetic-screen brick + engage-route proof. Boots a hand-assembled SM83 joypad-
// echo ROM into a declared screen over the pipe, engages player 1 (respecting the route policy),
// drives the SAME intent wire (tape + press) into the brick, and asserts the emulated work-RAM
// bytes over screen.peek. Polls echoes with a deadline (engine echoes can lag 1-2.5s), never fixed
// sleeps. This remains a World-owned live proof; the shared deterministic substrate is gated in Puck.Post Tier A.
// ============================================================================================

static class ScreensProof {
    // The joypad-echo ROM's work-RAM contract: the combined pressed-button byte and a liveness counter.
    const int PressedByteAddr = 0xC000;
    const int CounterAddr = 0xC001;
    // JoypadButtons bit layout (KEEP IN SYNC with Puck.HumbleGamingBrick.JoypadButtons): the ROM packs the pressed byte
    // in exactly this order, so 0xC000 reads back the same byte the world fed.
    const int BitUp = 0x04;
    const int BitA = 0x10;

    static readonly Regex PeekEcho = new(options: RegexOptions.Compiled, pattern: @"\[screen\.peek: (\d+) 0x([0-9A-Fa-f]+)=0x([0-9A-Fa-f]+)\]");
    static readonly Regex StateEcho = new(options: RegexOptions.Compiled, pattern: @"\[screen\.state: (\d+) (.+?)\]");
    // The world.view-refresh echo carries the count of camera views registered in the offscreen pool.
    static readonly Regex ViewRefreshEcho = new(options: RegexOptions.Compiled, pattern: @"\[world\.view-refresh: every \d+ produced frame\(s\); (\d+) camera view\(s\) registered\]");

    public static int RunScreens(ArgMap opts) {
        const int machineScreen = 0;

        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        // The ROM is written to a scratch path at proof time (never committed). A no-space path so the single-token
        // screen.insert <romPath> is safe over the wire.
        var romPath = (opts.Get(name: "--rom") ?? Path.Combine(path1: Path.GetTempPath(), path2: "puck-world-joypad-echo.gb"));

        File.WriteAllBytes(bytes: BuildJoypadEchoRom(), path: romPath);
        Console.WriteLine(value: $"[proof] joypad-echo ROM written: {romPath} (32 KiB, entry $0100 -> loop $0150)");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        // A safety exit bound: the proof drives interactively and kills the child in finally, but a crash must never
        // orphan the GPU window.
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: "120");

        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height}");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return 1;
            }

            // Arm GPU per-pass timing so the evidence read at the end reports the brick's added cost.
            Send(ctx: ctx, line: "world.timing on");
            Send(ctx: ctx, line: "world.quality low");

            // (0) DEFAULT NATIVE AGB IS ASSET-FREE — the fifth screen ships with no cartridge/BIOS baked in (no
            // owner-local path, no copyrighted dump), so a clean checkout boots it into the binder's graceful
            // "no content configured" fault, never a crash or a silent black slab.
            passed &= PollState(ctx: ctx, name: "default-native-agb-unconfigured", index: 4,
                predicate: body => (body.Contains(value: "empty") && body.Contains(value: "fault=no content configured")));

            // (1) NON-MACHINE ENGAGE — screen 0 starts as the overhead view, not a machine: engage errors loudly.
            passed &= ExpectEcho(ctx: ctx, name: "engage-non-machine-errors", command: $"player.engage {machineScreen}",
                predicate: line => (line.Contains(value: "player.engage") && line.Contains(value: "no machine")));

            // (2) BOOT — overlay the ROM on screen 0, then poll screen.state until assigned + bound + stepping.
            passed &= ExpectEcho(ctx: ctx, name: "insert-ok", command: $"screen.insert {machineScreen} {romPath} gaming-brick",
                predicate: line => (line.Contains(value: "screen.insert") && line.Contains(value: "booted") && !line.Contains(value: "not")));
            passed &= PollState(ctx: ctx, name: "state-assigned-bound", index: machineScreen,
                predicate: body => (body.Contains(value: "assigned") && body.Contains(value: "bound")));

            // (3) PASSIVE VIEW ENGAGE — screen 2 is the first-person jumbotron, not an interactive machine: its
            // route rejects engagement before the producer kind matters.
            passed &= ExpectEcho(ctx: ctx, name: "engage-jumbotron-view-errors", command: "player.engage 2",
                predicate: line => (line.Contains(value: "player.engage") && line.Contains(value: "not engageable")));

            // (4) OUT-OF-RANGE ENGAGE — screen 0 has a machine but demands proximity (radius 2.5): far away errors.
            Send(ctx: ctx, line: "player.warp 20 20 1");
            passed &= ExpectEcho(ctx: ctx, name: "engage-out-of-range-errors", command: $"player.engage {machineScreen}",
                predicate: line => (line.Contains(value: "player.engage") && line.Contains(value: "u to engage (player.warp closer)")));

            // Rounds (1), (3) and (4) each refused ONE engage on purpose; nothing else above was meant to be refused.
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "engage-round-refused-only-its-three", expected: 3);

            // (5) ENGAGE — warp within the radius (screen 0 origin is x=-3 z=-3), then engage succeeds.
            Send(ctx: ctx, line: "player.warp -3 -1 1");
            passed &= ExpectEcho(ctx: ctx, name: "engage-ok", command: $"player.engage {machineScreen}",
                predicate: line => (line.Contains(value: "player.engage") && line.Contains(value: $"engaged screen {machineScreen}")));

            // (6) INPUT ON THE INTENT WIRE — a forward run (-> Up) plus a jump press (-> A) both reach the brick via the
            // SAME intent wire the avatar would ride. Poll 0xC000 for the Up|A image the world fed.
            Send(ctx: ctx, line: "player.run 1 0 0 3 1");
            Send(ctx: ctx, line: "player.press primary 2 1");
            passed &= PollPeek(ctx: ctx, name: "brick-reads-up-and-A", index: machineScreen, addr: PressedByteAddr,
                until: value => (((value & BitUp) != 0) && ((value & BitA) != 0)));

            // (7) LIVENESS — the ROM's counter at 0xC001 advances as the machine steps.
            var counterA = PollPeekValue(ctx: ctx, index: machineScreen, addr: CounterAddr);
            var counterB = PollPeekUntil(ctx: ctx, index: machineScreen, addr: CounterAddr, until: v => ((counterA is { } a) && (v != a)));

            passed &= Check(name: "brick-counter-advances", ok: ((counterA is not null) && (counterB is not null) && (counterA != counterB)),
                detail: $"0xC001 {(counterA?.ToString(provider: ProofApp.Inv) ?? "?")} -> {(counterB?.ToString(provider: ProofApp.Inv) ?? "?")}");

            // (8) DISENGAGE + HELD-STATE HYGIENE — disengage, stop the tape, and the brick's buttons drop to 0x00 (no
            // residual held input leaks across the boundary).
            passed &= ExpectEcho(ctx: ctx, name: "disengage-ok", command: "player.disengage 1",
                predicate: line => (line.Contains(value: "player.disengage") && line.Contains(value: "disengaged")));
            Send(ctx: ctx, line: "player.stop 1");
            passed &= PollPeek(ctx: ctx, name: "brick-buttons-cleared", index: machineScreen, addr: PressedByteAddr, until: value => (value == 0));

            // (9) AVATAR MOVES AGAIN — a disengaged player drives its avatar: a fresh run moves it off its rest pose.
            Send(ctx: ctx, line: "player.warp 0 0 1");
            Send(ctx: ctx, line: "player.face 0 1");
            var poseBefore = ReadWhere(ctx: ctx, index: 1);

            Send(ctx: ctx, line: "player.run 1 0 0 1 1");
            var poseAfter = PollWhereUntil(ctx: ctx, index: 1, until: p => ((poseBefore is { } b) && (Math.Abs(value: (p.Z - b.Z)) > 0.3)));

            // PollWhereUntil returns the LAST pose on timeout, never null, so the displacement must be asserted here.
            passed &= Check(name: "avatar-moves-after-disengage",
                ok: ((poseBefore is { } restPose) && (poseAfter is { } movedPose) && (Math.Abs(value: (movedPose.Z - restPose.Z)) > 0.3)),
                detail: $"z {(poseBefore?.Z.ToString(format: "0.00", provider: ProofApp.Inv) ?? "?")} -> {(poseAfter?.Z.ToString(format: "0.00", provider: ProofApp.Inv) ?? "?")}");

            // (10) POPULATION LIFETIME — an engagement route belongs to one WorldBody lifetime, not merely its
            // reusable display index. Queue deactivate + reactivate + query in one pump batch: p7's newly-minted,
            // disengaged player must not inherit a stale route or remain in screen diagnostics.
            passed &= PopulationReactivationDropsEngagement(ctx: ctx, screenIndex: machineScreen);

            // (11) EJECT — the slot reveals its declared overhead view again (no machine to peek).
            passed &= ExpectEcho(ctx: ctx, name: "eject-ok", command: $"screen.eject {machineScreen}",
                predicate: line => (line.Contains(value: "screen.eject") && line.Contains(value: "ejected")));
            passed &= ExpectEcho(ctx: ctx, name: "peek-after-eject-errors", command: $"screen.peek {machineScreen} 0xC000",
                predicate: line => (line.Contains(value: "screen.peek") && line.Contains(value: "no machine")));

            // The peek at (11) is the round's ONE deliberate refusal (an ejected slot has no machine to read).
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "eject-round-refused-only-its-one", expected: 1);

            // (evidence) fps / gpu with a brick running earlier — re-boot briefly so the read reflects a stepped machine.
            Send(ctx: ctx, line: $"screen.insert {machineScreen} {romPath} gaming-brick");
            _ = PollState(ctx: ctx, name: "state-reboot", index: machineScreen, predicate: body => body.Contains(value: "bound"));
            ReportEvidence(ctx: ctx);

            // (12) REMOVE A VIEW SCREEN — screen 2 is the first-person jumbotron, a pure View source whose camera
            // render lives in the offscreen ViewStack pool (its own SDF engine spending refresh budget every few frames).
            // Removing the last screen wired to that camera must RELEASE its view, witnessed by the registered camera-view
            // count in world.view-refresh dropping by one (the co-existing 'overhead' view survives, so it drops 2 -> 1).
            passed &= RemoveViewScreenReleasesCameraView(ctx: ctx, viewScreen: 2);
            // The removed view screen's absent-state read is that helper's ONE deliberate refusal.
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "view-removal-refused-only-its-one", expected: 1);

            // (13) REMOVE AN ENGAGED MACHINE SCREEN — engage p1 on the running brick, then world.screen.remove the
            // whole screen row: the binder must disengage the player (avatar resumes normal intent), dispose the slot, and
            // drop its provider entry, so every screen command reports the index absent.
            passed &= RemoveEngagedScreenDropsEverything(ctx: ctx, screenIndex: machineScreen);
            // state/peek/insert against the removed index — the helper's three deliberate absent-reports.
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "screen-removal-refused-only-its-three", expected: 3);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] screens proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // The 32 KiB ROM-ONLY joypad-echo cartridge: zero-filled (a lenient boot, per W1) with an entry at $0100 that jumps
    // to a loop at $0150 which reads BOTH joypad lines and stores the combined pressed-button byte at 0xC000 (in
    // JoypadButtons order) plus a liveness counter at 0xC001. Internal so ExpoProof can reuse this valid bootable ROM
    // for its positive screen-insert write-back check (a machine must actually BOOT for the insert to fold).
    internal static byte[] BuildJoypadEchoRom() {
        var rom = new byte[0x8000];
        var p = 0x0100;

        void Emit(params byte[] bytes) {
            foreach (var b in bytes) {
                rom[p++] = b;
            }
        }

        // Entry $0100: jump over the header region to the program.
        Emit(0xC3, 0x50, 0x01);              // JP $0150

        // Program $0150 (the loop).
        p = 0x0150;
        Emit(0x3E, 0x20);                    // LD A,$20      ; P15=1 (deselect actions), P14=0 (select d-pad)
        Emit(0xE0, 0x00);                    // LDH ($00),A   ; write P1/JOYP
        Emit(0xF0, 0x00);                    // LDH A,($00)   ; read (settle)
        Emit(0xF0, 0x00);                    // LDH A,($00)   ; read d-pad lines (0 = pressed)
        Emit(0x2F);                          // CPL           ; -> 1 = pressed
        Emit(0xE6, 0x0F);                    // AND $0F       ; Right/Left/Up/Down -> bits 0..3
        Emit(0x47);                          // LD B,A
        Emit(0x3E, 0x10);                    // LD A,$10      ; P14=1 (deselect d-pad), P15=0 (select actions)
        Emit(0xE0, 0x00);                    // LDH ($00),A
        Emit(0xF0, 0x00);                    // LDH A,($00)
        Emit(0xF0, 0x00);                    // LDH A,($00)   ; read action lines (0 = pressed)
        Emit(0x2F);                          // CPL
        Emit(0xE6, 0x0F);                    // AND $0F       ; A/B/Select/Start -> bits 0..3
        Emit(0xCB, 0x37);                    // SWAP A        ; -> bits 4..7
        Emit(0xB0);                          // OR B          ; combine d-pad | actions (== JoypadButtons byte layout)
        Emit(0xEA, 0x00, 0xC0);              // LD ($C000),A  ; store the pressed-button byte
        Emit(0xFA, 0x01, 0xC0);              // LD A,($C001)  ; liveness counter
        Emit(0x3C);                          // INC A
        Emit(0xEA, 0x01, 0xC0);              // LD ($C001),A
        Emit(0xC3, 0x50, 0x01);              // JP $0150      ; loop forever

        return rom;
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    // Shader compilation and first-device startup can exceed an individual assertion's deadline on a cold machine.
    // Establish that stdin has reached the command router AND a fixed-step snapshot has applied before starting the
    // behavioral proof, so the first simulation-routed assertion tests semantics rather than racing initialization.
    // player.stop is idempotent at boot and leaves the player at the authored spawn.
    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(
            collector: ctx.Collector,
            mark: mark,
            predicate: candidate => candidate.Contains(value: "[player.stop:"),
            deadlineSeconds: 30.0
        );

        return Check(
            name: "simulation-ready",
            ok: (line is not null),
            detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds")
        );
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    // Send a command and poll the output stream for a line the predicate accepts, with a deadline (echoes can lag under
    // load). Prints the PASS/FAIL verdict and the matched line.
    static bool ExpectEcho(Ctx ctx, string name, string command, Func<string, bool> predicate) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: command);

        var line = Await(collector: ctx.Collector, mark: mark, predicate: predicate, deadlineSeconds: 12.0);

        return Check(name: name, ok: (line is not null), detail: (line?.Trim() ?? $"(no echo matched for '{command}')"));
    }

    static bool PopulationReactivationDropsEngagement(Ctx ctx, int screenIndex) {
        var passed = ExpectEcho(ctx: ctx, name: "population-route-activate", command: "world.population 60 idle",
            predicate: line => (line.Contains(value: "[world.population:") && line.Contains(value: "60 network-human")));

        Send(ctx: ctx, line: "player.warp -3 -1 7");
        passed &= ExpectEcho(ctx: ctx, name: "population-route-engage", command: $"player.engage {screenIndex} 7",
            predicate: line => (line.Contains(value: "player.engage") && line.Contains(value: $"engaged screen {screenIndex}")));
        passed &= PollState(ctx: ctx, name: "population-route-visible", index: screenIndex,
            predicate: body => body.Contains(value: "engaged=p7"));

        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.population 2");
        Send(ctx: ctx, line: "world.population 60");
        Send(ctx: ctx, line: $"screen.state {screenIndex}");

        var line = Await(
            collector: ctx.Collector,
            mark: mark,
            predicate: candidate => StateEcho.IsMatch(input: candidate),
            deadlineSeconds: 12.0
        );
        var body = ((line is not null) ? StateEcho.Match(input: line).Groups[2].Value : null);

        passed &= Check(
            name: "population-reactivation-drops-engagement",
            ok: (body is not null && body.Contains(value: "engaged=none")),
            detail: (body ?? "(no screen.state echo after population reactivation)")
        );

        return passed;
    }

    // Engage p1 on a running machine screen, remove the whole screen row, and assert the binder torn everything
    // down: the mutation applies, every screen command then reports the index absent (slot + provider entry gone), and
    // the disengaged avatar resumes normal intent (it is not held idle against a machine that no longer exists).
    static bool RemoveEngagedScreenDropsEverything(Ctx ctx, int screenIndex) {
        // Engage p1 within the screen's radius (origin x=-3 z=-3) on the re-inserted brick.
        Send(ctx: ctx, line: "player.warp -3 -1 1");

        var passed = ExpectEcho(ctx: ctx, name: "remove-engage-ok", command: $"player.engage {screenIndex}",
            predicate: line => (line.Contains(value: "player.engage") && line.Contains(value: $"engaged screen {screenIndex}")));

        passed &= PollState(ctx: ctx, name: "remove-engaged-visible", index: screenIndex, predicate: body => body.Contains(value: "engaged=p1"));

        // Remove the whole screen row (a Simulation-routed mutation; the server prints the applied line at the boundary).
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"world.screen.remove {screenIndex}");

        var removed = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[world.mutation: RemoveScreen {screenIndex} applied]"), deadlineSeconds: 15.0);

        passed &= Check(name: "remove-mutation-applied", ok: (removed is not null), detail: (removed?.Trim() ?? "(no '[world.mutation: RemoveScreen ...]' line)"));

        // The binder reconciles the removal on the next client frame: every screen command now reports the index absent.
        passed &= PollAbsent(ctx: ctx, name: "state-reports-absent", index: screenIndex);
        passed &= ExpectEcho(ctx: ctx, name: "peek-reports-absent", command: $"screen.peek {screenIndex} 0xC000",
            predicate: line => (line.Contains(value: "screen.peek") && line.Contains(value: $"no screen {screenIndex}")));
        passed &= ExpectEcho(ctx: ctx, name: "insert-reports-absent", command: $"screen.insert {screenIndex} nonexistent.gb gaming-brick",
            predicate: line => (line.Contains(value: "screen.insert") && line.Contains(value: $"no screen {screenIndex} declared")));

        // The engaged player was disengaged cleanly: a fresh run drives the avatar again (not held idle against the
        // vanished machine).
        Send(ctx: ctx, line: "player.warp 0 0 1");
        Send(ctx: ctx, line: "player.face 0 1");

        var before = ReadWhere(ctx: ctx, index: 1);

        Send(ctx: ctx, line: "player.run 1 0 0 1 1");

        var after = PollWhereUntil(ctx: ctx, index: 1, until: p => ((before is { } b) && (Math.Abs(value: (p.Z - b.Z)) > 0.3)));

        passed &= Check(name: "avatar-resumes-after-screen-removed",
            ok: ((before is { } bb) && (after is { } aa) && (Math.Abs(value: (aa.Z - bb.Z)) > 0.3)),
            detail: $"z {(before?.Z.ToString(format: "0.00", provider: ProofApp.Inv) ?? "?")} -> {(after?.Z.ToString(format: "0.00", provider: ProofApp.Inv) ?? "?")}");

        return passed;
    }

    // Remove a View screen and prove its offscreen camera render is released: the registered camera-view count
    // (read off world.view-refresh) drops by one, and screen.state reports the removed index absent. The camera's
    // offscreen SDF engine was spending refresh budget until the removal; the count is the pipe-observable witness that
    // it stopped (a surviving jumbotron sharing a DIFFERENT camera keeps its own view, so the count drops by exactly one).
    static bool RemoveViewScreenReleasesCameraView(Ctx ctx, int viewScreen) {
        var before = PollCameraViewCount(ctx: ctx);
        var passed = Check(name: "view-count-before-removal", ok: ((before is { } b) && (b > 0)),
            detail: $"registered camera views = {(before?.ToString(provider: ProofApp.Inv) ?? "?")}");

        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"world.screen.remove {viewScreen}");

        var removed = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[world.mutation: RemoveScreen {viewScreen} applied]"), deadlineSeconds: 15.0);

        passed &= Check(name: "view-remove-mutation-applied", ok: (removed is not null), detail: (removed?.Trim() ?? "(no '[world.mutation: RemoveScreen ...]' line)"));

        // The binder reconciles the removal on the next client frame, releasing the orphaned camera view. Poll the count
        // until it drops to exactly (before - 1).
        var target = ((before is { } bc) ? (bc - 1) : -1);
        var after = PollCameraViewCountUntil(ctx: ctx, until: c => (c == target));

        passed &= Check(name: "camera-view-released-after-removal", ok: ((after is { } a) && (a == target)),
            detail: $"registered camera views {(before?.ToString(provider: ProofApp.Inv) ?? "?")} -> {(after?.ToString(provider: ProofApp.Inv) ?? "?")} (want {target})");

        passed &= PollAbsent(ctx: ctx, name: "view-screen-reports-absent", index: viewScreen);

        return passed;
    }

    // One world.view-refresh round trip parsed for the registered camera-view count, or null when the echo did not land.
    static int? PollCameraViewCount(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.view-refresh");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => ViewRefreshEcho.IsMatch(input: l), deadlineSeconds: 6.0);

        return ((line is not null) ? int.Parse(s: ViewRefreshEcho.Match(input: line).Groups[1].Value, provider: ProofApp.Inv) : null);
    }
    // Poll the camera-view count until it satisfies `until` (or a deadline passes); returns the last read value.
    static int? PollCameraViewCountUntil(Ctx ctx, Func<int, bool> until) {
        var deadline = DateTime.UtcNow.AddSeconds(value: 15.0);
        int? last = null;

        while (DateTime.UtcNow < deadline) {
            if (PollCameraViewCount(ctx: ctx) is { } count) {
                last = count;

                if (until(arg: count)) {
                    return count;
                }
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return last;
    }

    // Poll screen.state <index> until it reports the index ABSENT (the removed-slot echo), or a deadline passes.
    static bool PollAbsent(Ctx ctx, string name, int index) {
        var deadline = DateTime.UtcNow.AddSeconds(value: 15.0);

        while (DateTime.UtcNow < deadline) {
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"screen.state {index}");

            var line = Await(collector: ctx.Collector, mark: mark, predicate: l => (l.Contains(value: "[screen.state:") && l.Contains(value: $"no screen {index} declared")), deadlineSeconds: 3.0);

            if (line is not null) {
                return Check(name: name, ok: true, detail: line.Trim());
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return Check(name: name, ok: false, detail: $"screen.state never reported screen {index} absent");
    }

    // Poll screen.state <index> until its body satisfies the predicate (or a deadline passes).
    static bool PollState(Ctx ctx, string name, int index, Func<string, bool> predicate) {
        var deadline = DateTime.UtcNow.AddSeconds(value: 15.0);
        string? lastBody = null;

        while (DateTime.UtcNow < deadline) {
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"screen.state {index}");

            var line = Await(collector: ctx.Collector, mark: mark, predicate: l => StateEcho.IsMatch(input: l), deadlineSeconds: 3.0);

            if (line is not null) {
                lastBody = StateEcho.Match(input: line).Groups[2].Value;

                if (predicate(arg: lastBody)) {
                    return Check(name: name, ok: true, detail: lastBody);
                }
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return Check(name: name, ok: false, detail: (lastBody ?? "(no screen.state echo)"));
    }

    // Poll screen.peek until its value satisfies `until` (or a deadline). Prints the verdict + last value.
    static bool PollPeek(Ctx ctx, string name, int index, int addr, Func<int, bool> until) {
        var value = PollPeekUntil(ctx: ctx, index: index, addr: addr, until: until);

        return Check(name: name, ok: (value is not null), detail: ((value is { } v) ? $"0x{addr:X4}=0x{v:X2}" : $"0x{addr:X4} never satisfied"));
    }
    static int? PollPeekUntil(Ctx ctx, int index, int addr, Func<int, bool> until) {
        var deadline = DateTime.UtcNow.AddSeconds(value: 12.0);
        int? last = null;

        while (DateTime.UtcNow < deadline) {
            var value = PollPeekValue(ctx: ctx, index: index, addr: addr);

            if (value is { } v) {
                last = v;

                if (until(arg: v)) {
                    return v;
                }
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return last;
    }

    // One screen.peek round trip: send, await the echo, parse the hex value.
    static int? PollPeekValue(Ctx ctx, int index, int addr) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"screen.peek {index} 0x{addr:X4}");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => PeekEcho.IsMatch(input: l), deadlineSeconds: 3.0);

        if (line is null) {
            return null;
        }

        var match = PeekEcho.Match(input: line);

        return int.Parse(s: match.Groups[3].Value, style: NumberStyles.HexNumber, provider: ProofApp.Inv);
    }
    static Pose? ReadWhere(Ctx ctx, int index) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"player.where {index}");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => ProofApp.WhereEcho.IsMatch(input: l), deadlineSeconds: 6.0);

        return ((line is not null) ? ParsePose(line: line) : null);
    }
    static Pose? PollWhereUntil(Ctx ctx, int index, Func<Pose, bool> until) {
        var deadline = DateTime.UtcNow.AddSeconds(value: 8.0);
        Pose? last = null;

        while (DateTime.UtcNow < deadline) {
            if (ReadWhere(ctx: ctx, index: index) is { } pose) {
                last = pose;

                if (until(arg: pose)) {
                    return pose;
                }
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return last;
    }
    static Pose? ParsePose(string line) {
        var match = ProofApp.WhereEcho.Match(input: line);

        return (match.Success
            ? new Pose(
                X: double.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv),
                Y: double.Parse(s: match.Groups[3].Value, provider: ProofApp.Inv),
                Z: double.Parse(s: match.Groups[4].Value, provider: ProofApp.Inv),
                Yaw: int.Parse(s: match.Groups[5].Value, provider: ProofApp.Inv),
                Pitch: int.Parse(s: match.Groups[6].Value, provider: ProofApp.Inv),
                Roll: int.Parse(s: match.Groups[7].Value, provider: ProofApp.Inv))
            : null);
    }

    // Read and print the world.fps + world.gpu evidence with the brick running (a stepped machine per frame is new load).
    static void ReportEvidence(Ctx ctx) {
        foreach (var verb in new[] { "world.fps", "world.gpu" }) {
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: verb);

            var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: verb), deadlineSeconds: 6.0);

            if (line is not null) {
                Console.WriteLine(value: $"[proof] evidence: {line.Trim()}");
            }
        }
    }

    // Poll the collector's snapshot for a line the predicate accepts after <paramref name="mark"/>, until a deadline.
    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }
    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// MUTATE — the scripted mutation round-trip proof: world.kit.tune / world.undo / world.save
// journal discipline (dirty = journal length), rejection honesty (the defaultSeatKit invariant), and
// survival through a relaunch against the saved file. This is a SEPARATE session per (a)+(b) vs. (c):
// session A drives the round-trip and saves to a temp file; session B relaunches --world <that file>
// to prove the boot line and the on-disk JSON actually carry the edit.
// ============================================================================================
static class MutateProof {
    public static int RunMutate(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var pid = Environment.ProcessId;
        var temp1 = Path.Combine(Path.GetTempPath(), $"puck-world-mutate-1-{pid}.json");

        Console.WriteLine(value: "[proof] === mutate (a): world.kit.tune / world.undo / world.save journal round-trip ===");
        var roundTripPassed = RunRoundTrip(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, savePath: temp1, rejectionPassed: out var rejectionPassed);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === mutate (b): rejection honesty — the defaultSeatKit invariant ===");
        Console.WriteLine(value: $"[proof]   {(rejectionPassed ? "PASS" : "FAIL")} (asserted inline with (a) — same session, see above)");

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === mutate (c): survival through a relaunch (--world <saved file>) ===");
        var survivalPassed = (File.Exists(path: temp1) && RunSurvival(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldPath: temp1));

        if (!File.Exists(path: temp1)) {
            Console.WriteLine(value: $"[proof]   FAIL survival: {temp1} was never written (round-trip did not reach world.save)");
        }

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === mutate (d): protocol-version handshake ===");
        Console.WriteLine(value: "[proof]   SKIPPED by design — no scripted Join path exists over stdin (SessionRequest.Join is not a console");
        Console.WriteLine(value: "[proof]            verb), and this proof does not invent a debug verb to exercise it. The rejecting handshake");
        Console.WriteLine(value: "[proof]            (Accepted: false + reason on a version mismatch) was proven by the implementing session.");

        var passed = (roundTripPassed && rejectionPassed && survivalPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] mutate proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // Session A: boot the baked-default world (no --world), drive the tune/status/undo/tune/save round-trip plus the
    // kit-removal rejection, asserting the journal-length dirty counter and the server's loud accept/reject/undo lines
    // at every step. Never asserts the in-flight #expect-band shape this file's other proofs use — this is a pure
    // console-echo proof, no pose corpus.
    static bool RunRoundTrip(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string savePath, out bool rejectionPassed) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        rejectionPassed = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (baked-default world)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            // (0) baseline — nothing molded yet.
            passed &= ExpectStatus(ctx: ctx, name: "status-clean", dirty: 0);

            // (1) tune runner.moveSpeed 4 -> 6: applied + dirty 1.
            passed &= MutateAndExpectStatus(ctx: ctx, name: "tune-applies", command: "world.kit.tune runner moveSpeed 6",
                appliedNeedle: "[world.mutation: UpsertKit 'runner' applied]", dirty: 1);

            // (2) undo: dropped + dirty back to 0.
            passed &= MutateAndExpectStatus(ctx: ctx, name: "undo-reverts", command: "world.undo",
                appliedNeedle: "[world.undo: dropped 1, 0 remaining]", dirty: 0);

            // (3) tune again — the edit that survives to the save below.
            passed &= MutateAndExpectStatus(ctx: ctx, name: "tune-reapplies", command: "world.kit.tune runner moveSpeed 6",
                appliedNeedle: "[world.mutation: UpsertKit 'runner' applied]", dirty: 1);

            // (4) save compacts the journal: dirty -> 0 (the saved definition becomes the new base).
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"world.save {savePath}");

            var saveLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);

            passed &= Check(name: "save-writes", ok: ((saveLine is not null) && !saveLine.Contains(value: "could not write")), detail: (saveLine?.Trim() ?? "(no world.save echo)"));
            passed &= ExpectStatus(ctx: ctx, name: "status-clean-after-save", dirty: 0);

            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "mutate-round-refused-nothing", expected: 0);

            // (b) rejection honesty: removing the defaultSeatKit fails validation loudly, and the document is unchanged.
            rejectionPassed = ExpectRejectedKitRemoval(ctx: ctx);
            rejectionPassed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "remove-round-refused-only-the-default-kit", expected: 1);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // world.kit.remove runner targets the definition-designated DefaultSeatKit ('runner' in the baked default): the
    // composed candidate passes TryCompose (the row exists) but fails WorldDefinitionValidator (defaultSeatKit names no
    // kit row), so the server rejects loudly and the definition — and therefore world.status's kit count — is unchanged.
    static bool ExpectRejectedKitRemoval(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.kit.remove runner");
        Send(ctx: ctx, line: "world.status");

        var statusLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);
        var statusOk = Check(name: "status-unchanged-after-rejected-remove", ok: ((statusLine is not null) && statusLine.Contains(value: "kits 5")),
            detail: (statusLine?.Trim() ?? "(no world.status echo)"));

        var snapshot = ctx.Collector.Snapshot();
        var rejectedFound = false;

        for (var i = mark; (i < snapshot.Length); i++) {
            if (snapshot[i].Contains(value: "[world.mutation rejected:") && snapshot[i].Contains(value: "RemoveKit 'runner'") && snapshot[i].Contains(value: "defaultSeatKit")) {
                rejectedFound = true;

                break;
            }
        }

        var rejectedOk = Check(name: "remove-runner-rejected", ok: rejectedFound, detail: (rejectedFound ? "seen" : "missing '[world.mutation rejected: RemoveKit ...defaultSeatKit...]'"));

        return (statusOk && rejectedOk);
    }

    // Session B: relaunch against the file session A saved and assert the boot line names it (the loader's one-line
    // origin) — the loud opposite of the worlddoc proof's baked-default-fallback line.
    static bool RunSurvival(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string worldPath) {
        var runnerMoveSpeed = ExtractKitMoveSpeed(json: File.ReadAllText(path: worldPath), kitName: "runner");
        var flyerMoveSpeed = ExtractKitMoveSpeed(json: File.ReadAllText(path: worldPath), kitName: "flyer");

        var jsonPassed = Check(name: "saved-json-runner-tuned", ok: (runnerMoveSpeed == 6.0), detail: $"runner moveSpeed = {(runnerMoveSpeed?.ToString(provider: ProofApp.Inv) ?? "?")} (want 6)");

        jsonPassed &= Check(name: "saved-json-other-kits-untouched", ok: (flyerMoveSpeed == 4.0), detail: $"flyer moveSpeed = {(flyerMoveSpeed?.ToString(provider: ProofApp.Inv) ?? "?")} (want 4)");

        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        psi.ArgumentList.Add(item: "--world");
        psi.ArgumentList.Add(item: worldPath);
        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var started = false;
        var bootPassed = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --world {worldPath} --width {width} --height {height}");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var bootLine = Await(collector: collector, mark: 0, predicate: l => (l.Contains(value: "[world] definition:") && l.Contains(value: worldPath)), deadlineSeconds: 30.0);

            bootPassed = Check(name: "boots-from-saved-file", ok: (bootLine is not null), detail: (bootLine?.Trim() ?? $"(no '[world] definition: {worldPath}' boot line)"));
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return (jsonPassed && bootPassed);
    }

    // The first "moveSpeed" number following a kit row's "name": "<kitName>" — the canonical writer's stable member
    // order puts tuning.moveSpeed as the tuning block's first field, immediately after model, so this is unambiguous
    // without a full JSON parse.
    static double? ExtractKitMoveSpeed(string json, string kitName) {
        var match = Regex.Match(input: json, pattern: $@"""name""\s*:\s*""{Regex.Escape(kitName)}""[\s\S]*?""moveSpeed""\s*:\s*(-?[0-9.]+)");

        return (match.Success ? double.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv) : null);
    }

    // A mutation verb (Simulation-routed, quiet ack) followed by a world.status read: the stdin drain barrier holds the
    // Immediate world.status behind the pending Simulation submission, so its answer reflects the applied (or rejected)
    // state for free — no polling needed. Also asserts the server's own loud line appeared somewhere in the same window.
    static bool MutateAndExpectStatus(Ctx ctx, string name, string command, string appliedNeedle, int dirty) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: command);
        Send(ctx: ctx, line: "world.status");

        var statusLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);
        var statusOk = Check(name: $"{name}-status", ok: ((statusLine is not null) && statusLine.Contains(value: $"dirty {dirty} undoable {dirty}]")), detail: (statusLine?.Trim() ?? "(no world.status echo)"));

        var snapshot = ctx.Collector.Snapshot();
        var appliedFound = false;

        for (var i = mark; (i < snapshot.Length); i++) {
            if (snapshot[i].Contains(value: appliedNeedle)) {
                appliedFound = true;

                break;
            }
        }

        var appliedOk = Check(name: $"{name}-echo", ok: appliedFound, detail: (appliedFound ? "seen" : $"missing '{appliedNeedle}'"));

        return (statusOk && appliedOk);
    }

    static bool ExpectStatus(Ctx ctx, string name, int dirty) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.status");

        var statusLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);

        return Check(name: name, ok: ((statusLine is not null) && statusLine.Contains(value: $"dirty {dirty} undoable {dirty}]")), detail: (statusLine?.Trim() ?? "(no world.status echo)"));
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    // Shader compilation and first-device startup can exceed an individual assertion's deadline on a cold machine.
    // player.stop is idempotent at boot and leaves the player at the authored spawn.
    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }
    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// GRANTS — the principals/grants keystone proof: mount an authored autopilot addon as a data-side
// WorldAddonRow, grant it Drive
// over a body and watch it move, revoke mid-run and watch the server's edge-latched denial freeze
// it, then prove Mutate-section enforcement (console loses/regains world.kit.tune) with
// world.status's dirty counter as the honest witness. Two sessions, mirroring MutateProof's shape:
// session A mounts + saves (world.addon.set is DATA-only — WorldAddonDriver only mounts ENABLED
// rows from the definition it was constructed with, at boot), session B relaunches
// --world <saved file> to actually mount the addon and drives the grant/revoke/mutate-denial
// sequence. The engagement-view regression (WorldEngagement as a view over the same grant table)
// is covered by proof.cs screens — not duplicated here.
// ============================================================================================
static class GrantsProof {
    const string AutopilotName = "autopilot";
    const string AutopilotModulePath = "Assets/addons/autopilot.wat";
    const int AutopilotBodyIndex = 9;               // 0-based entity index — a network stand-in, never a seat
    const int AutopilotPlayerIndex = (AutopilotBodyIndex + 1); // player.where's 1-based index
    const int OrdinaryBodyIndex = 11;               // ordinary-then-exclusive order test body (an active network stand-in)
    const int ExclusiveBodyIndex = 12;              // exclusive-then-ordinary + sole-driver test body
    const int ExclusivePlayerIndex = (ExclusiveBodyIndex + 1); // player.run's 1-based index for the sole-driver test
    const int CensusForBodies = (ExclusiveBodyIndex + 1); // the world.population raise that admits every body index above
    // u — the addon's steady walk covers ~2.6 u over 1 s. The floor discriminates only because the census below parks
    // every stand-in at IntentSource.Idle: under the 'wander' default a body drifts ~3.4 u over the same window and
    // clears this floor with NOTHING driving it. The ambient-drift-nulled check measures that premise every run.
    const double MovedEpsilon = 0.5;
    const double FrozenEpsilon = 0.02;               // u — a revoked, un-driven body must not move at all (2-decimal echo precision)

    public static int RunGrants(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var pid = Environment.ProcessId;
        var worldPath = Path.Combine(Path.GetTempPath(), $"puck-world-grants-{pid}.world.json");

        // The (h) profile-subject round edits the player document, so the REAL store is backed up whole and restored
        // in the finally (the bindings-proof discipline); the cleared store reseeds deterministic catalog ids.
        var worldDir = PlayerStorePaths.WorldDir();
        var worldBackup = DirectoryBackup.Snapshot(dir: worldDir);
        bool mountedSaved;
        bool sessionPassed;

        try {
            DirectoryBackup.Clear(dir: worldDir);

            Console.WriteLine(value: "[proof] === grants (a): world.addon.set autopilot + world.save (data-only; the driver mounts at boot) ===");
            mountedSaved = RunMountAndSave(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldPath: worldPath);

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === grants (b)-(h): relaunch --world <saved> — mount, drive/revoke, mutate-denial, exclusivity, profile subject ===");
            sessionPassed = (File.Exists(path: worldPath) && RunGrantSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldPath: worldPath));

            if (!File.Exists(path: worldPath)) {
                Console.WriteLine(value: $"[proof]   FAIL relaunch: {worldPath} was never written (grants (a) did not reach world.save)");
            }
        }
        finally {
            DirectoryBackup.Restore(snapshot: worldBackup);
        }

        var passed = (mountedSaved && sessionPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] grants proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // Session A: boot the baked-default world, upsert the autopilot WorldAddonRow (world.addon.set is a document
    // mutation like any other — buffered, journaled, revalidated) and save it. world.addon.set never hot-mounts;
    // WorldAddonDriver.Create only reads ENABLED addon rows off the definition it is constructed with, at boot — so
    // session B's relaunch is what actually proves the saved row boots the addon.
    static bool RunMountAndSave(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string worldPath) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (baked-default world)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            var addonJson = $"{{\"name\":\"{AutopilotName}\",\"modulePath\":\"{AutopilotModulePath}\",\"hash\":\"\",\"fuel\":100000,\"enabled\":true}}";

            passed &= MutateAndExpectStatus(ctx: ctx, name: "addon-row-upserts", command: $"world.addon.set {addonJson}",
                appliedNeedle: $"[world.mutation: UpsertAddon '{AutopilotName}' applied]", dirty: 1);

            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"world.save {worldPath}");

            var saveLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);

            passed &= Check(name: "save-writes", ok: ((saveLine is not null) && !saveLine.Contains(value: "could not write")), detail: (saveLine?.Trim() ?? "(no world.save echo)"));
            passed &= SettleWireErrors(ctx: ctx, name: "addon-authoring-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // Session B: relaunch against the file session A saved — THIS boot actually mounts the addon (WorldAddonDriver
    // reads the definition's addons section at construction, once). Drives (b) grant+movement, (c) revoke+frozen, and
    // (d) the console Mutate/kits denial-then-recovery, all in one running session.
    static bool RunGrantSession(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string worldPath) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: worldPath);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --world {worldPath} --width {width} --height {height}");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            var mountLine = Await(collector: ctx.Collector, mark: 0, predicate: l => l.Contains(value: $"[world.addon: mounted {AutopilotName}"), deadlineSeconds: 30.0);

            passed &= Check(name: "addon-mounted-at-boot", ok: (mountLine is not null), detail: (mountLine?.Trim() ?? $"(no '[world.addon: mounted {AutopilotName} ...]' boot line)"));

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            // The census stands at ZERO at boot — `population.networkPlayers` is the remote-admission CAP, not a
            // reservation — so the stand-in bodies this suite drives and contends over must be admitted first.
            // Without the raise, body:9/11/12 have no population entry and every pose readback below reads "(?)".
            // The `idle` token is load-bearing, not decoration: it sweeps every peer's IntentSource to Idle, so the
            // wander producer never touches the bodies this suite measures and every displacement below is a
            // SUBMITTED intent's.
            var censusMark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"world.population {CensusForBodies} idle");

            var censusLine = Await(collector: ctx.Collector, mark: censusMark, predicate: l => l.Contains(value: "[world.population:"), deadlineSeconds: 15.0);

            passed &= Check(name: "census-admits-stand-ins", ok: (censusLine is not null), detail: (censusLine?.Trim() ?? "(no '[world.population: ...]' echo)"));

            // (a2) THE NEGATIVE CONTROL for (b). Read the parked source back, then sample the SAME window the drive
            // check uses with nothing driving the body. Ambient motion is measured, not assumed: if the idle sweep ever
            // stops taking, this fails here instead of quietly re-floating the drive check on wander.
            passed &= ExpectControl(ctx: ctx, name: "stand-in-parked-idle", index: AutopilotPlayerIndex, word: "idle");

            var beforeAmbient = ReadWhere(ctx: ctx, index: AutopilotPlayerIndex);

            Thread.Sleep(millisecondsTimeout: 1000);

            var afterAmbient = ReadWhere(ctx: ctx, index: AutopilotPlayerIndex);
            var ambientDrift = Distance(a: beforeAmbient, b: afterAmbient);

            passed &= Check(name: "ambient-drift-nulled", ok: ((beforeAmbient is not null) && (afterAmbient is not null) && (ambientDrift <= FrozenEpsilon)),
                detail: $"p{AutopilotPlayerIndex} {Fmt(pose: beforeAmbient)} -> {Fmt(pose: afterAmbient)} (delta {ambientDrift:0.000} u, want <= {FrozenEpsilon})");

            // (b) grant Drive over a network stand-in body: the addon starts driving through the SAME wire a seat
            // uses, and the driver flips the body's IntentSource to Live, so the movement below is unambiguously
            // the addon's — not the ambient wander producer it superseded.
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"world.grant addon:{AutopilotName} drive body:{AutopilotBodyIndex} exclusive");

            var grantLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[world.grant: addon:{AutopilotName} drive body:{AutopilotBodyIndex} exclusive]"), deadlineSeconds: 15.0);

            passed &= Check(name: "grant-drive-accepted", ok: (grantLine is not null), detail: (grantLine?.Trim() ?? "(no '[world.grant: ...]' echo)"));

            // Give the driver a couple of ticks to discover the grant and flip IntentSource before the first sample.
            Thread.Sleep(millisecondsTimeout: 300);

            // ASSERT THE LATCH the drive check rests on: the body left Idle because the addon put it on Live. Nothing
            // asserted this before, so a driver that silently stopped latching would have read as a pass.
            passed &= ExpectControl(ctx: ctx, name: "addon-latches-body-live", index: AutopilotPlayerIndex, word: "live");

            var beforeMove = ReadWhere(ctx: ctx, index: AutopilotPlayerIndex);

            Thread.Sleep(millisecondsTimeout: 1000);

            var afterMove = ReadWhere(ctx: ctx, index: AutopilotPlayerIndex);
            var moveDistance = Distance(a: beforeMove, b: afterMove);

            passed &= Check(name: "addon-drives-body", ok: ((beforeMove is not null) && (afterMove is not null) && (moveDistance > MovedEpsilon)),
                detail: $"p{AutopilotPlayerIndex} {Fmt(pose: beforeMove)} -> {Fmt(pose: afterMove)} (delta {moveDistance:0.000} u, want > {MovedEpsilon})");

            // (c) revoke mid-run: the server drops the addon's next submitted intent — loud ONCE — and the body idles
            // (IntentSource stays Live, so it never resumes wander; nothing else drives it).
            mark = ctx.Collector.Count;

            Send(ctx: ctx, line: $"world.revoke addon:{AutopilotName} drive body:{AutopilotBodyIndex}");

            var revokeLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[world.revoke: addon:{AutopilotName} drive body:{AutopilotBodyIndex}]"), deadlineSeconds: 15.0);

            passed &= Check(name: "revoke-accepted", ok: (revokeLine is not null), detail: (revokeLine?.Trim() ?? "(no '[world.revoke: ...]' echo)"));

            var deniedLine = Await(collector: ctx.Collector, mark: mark,
                predicate: l => (l.Contains(value: "[world.grant denied:") && l.Contains(value: $"addon:{AutopilotName}") && l.Contains(value: $"body:{AutopilotBodyIndex}")),
                deadlineSeconds: 15.0);

            passed &= Check(name: "revoke-denies-next-intent", ok: (deniedLine is not null), detail: (deniedLine?.Trim() ?? "(no edge-latched '[world.grant denied: ...]' line)"));

            var beforeFreeze = ReadWhere(ctx: ctx, index: AutopilotPlayerIndex);

            Thread.Sleep(millisecondsTimeout: 1000);

            var afterFreeze = ReadWhere(ctx: ctx, index: AutopilotPlayerIndex);
            var freezeDrift = Distance(a: beforeFreeze, b: afterFreeze);

            passed &= Check(name: "revoked-body-frozen", ok: ((beforeFreeze is not null) && (afterFreeze is not null) && (freezeDrift <= FrozenEpsilon)),
                detail: $"p{AutopilotPlayerIndex} {Fmt(pose: beforeFreeze)} -> {Fmt(pose: afterFreeze)} (delta {freezeDrift:0.000} u, want <= {FrozenEpsilon})");
            // (a)-(c) piped no line the wire should refuse: the revoke's dropped intent is the ADDON's submission over
            // the link, not a console line.
            passed &= SettleWireErrors(ctx: ctx, name: "drive-round-refused-nothing", expected: 0);

            // (d) denied-mutation honesty: strip console's Mutate/kits grant, prove the tune is rejected AND the
            // document (world.status's dirty counter) is genuinely unchanged, then re-grant and prove it applies.
            passed &= RunMutateDenialRoundTrip(ctx: ctx);

            // (e) EXCLUSIVITY: the two grant orders both reject a conflicting second grant, and an exclusively
            // held body has exactly one effective driver — the exclusive holder overrides even the console's Drive/all.
            passed &= RunExclusivityOrders(ctx: ctx);

            // (g) EXCLUSIVE SECTION ACQUISITION: the seeded per-section Mutate defaults never block a hold.
            passed &= RunExclusiveSectionRound(ctx: ctx);

            // (h) PROFILE SUBJECT: Edit checks the concrete profile:<id> subject.
            passed &= RunProfileSubjectRound(ctx: ctx);

            // Every deliberate refusal above was settled and cleared by its own round. A nonzero count here is a line
            // this suite MEANT to succeed and the wire refused — the failure mode that reads as green everywhere else,
            // because an await satisfied by other means never notices the step that no-opped.
            passed &= SettleWireErrors(ctx: ctx, name: "no-silent-rejections", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // (d): revoke console's Mutate/kits grant, prove world.kit.tune is rejected loudly with world.status's dirty
    // counter unchanged (this session's journal is fresh off a file load, so the baseline is 0), re-grant, and prove
    // the identical command now applies (dirty -> 1).
    static bool RunMutateDenialRoundTrip(Ctx ctx) {
        var passed = true;
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.revoke console mutate section:kits");

        var revokeLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.revoke: console mutate section:kits]"), deadlineSeconds: 15.0);

        passed &= Check(name: "console-loses-kits-mutate", ok: (revokeLine is not null), detail: (revokeLine?.Trim() ?? "(no '[world.revoke: ...]' echo)"));
        passed &= ExpectStatus(ctx: ctx, name: "status-baseline-before-denied-tune", dirty: 0);

        mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.kit.tune runner moveSpeed 6");
        Send(ctx: ctx, line: "world.status");

        var statusLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);

        passed &= Check(name: "tune-denied-status-unchanged", ok: ((statusLine is not null) && statusLine.Contains(value: "dirty 0 undoable 0]")), detail: (statusLine?.Trim() ?? "(no world.status echo)"));

        var snapshot = ctx.Collector.Snapshot();
        var deniedFound = false;

        for (var i = mark; (i < snapshot.Length); i++) {
            if (snapshot[i].Contains(value: "[world.grant denied:") && snapshot[i].Contains(value: "console") && snapshot[i].Contains(value: "section:kits") && snapshot[i].Contains(value: "UpsertKit 'runner' dropped")) {
                deniedFound = true;

                break;
            }
        }

        passed &= Check(name: "tune-denied-line", ok: deniedFound, detail: (deniedFound ? "seen" : "missing '[world.grant denied: console ... section:kits ... UpsertKit ...runner... dropped]'"));

        mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.grant console mutate section:kits");

        var grantLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.grant: console mutate section:kits]"), deadlineSeconds: 15.0);

        passed &= Check(name: "console-regains-kits-mutate", ok: (grantLine is not null), detail: (grantLine?.Trim() ?? "(no '[world.grant: ...]' echo)"));
        passed &= MutateAndExpectStatus(ctx: ctx, name: "tune-reapplies-after-regrant", command: "world.kit.tune runner moveSpeed 6",
            appliedNeedle: "[world.mutation: UpsertKit 'runner' applied]", dirty: 1);
        // One deliberate refusal: the denied world.kit.tune.
        passed &= SettleWireErrors(ctx: ctx, name: "mutate-round-refused-only-the-denied-tune", expected: 1);

        return passed;
    }

    // (e): exclusivity must actually exclude, in BOTH grant orders, and the exclusive holder must be the SOLE effective
    // driver. The addon holds no Drive body here at entry (grant (b)'s body:9 was revoked in (c)), so m_exclusive
    // starts clean; the two order tests use fresh bodies so they never interfere with each other.
    //
    // The grant table is bookkeeping over an integer subject — it never consults EntryBody — so the accept/reject
    // echoes below would read identically over bodies that do not exist. The census RunGrantSession raises makes
    // body:11/body:12 real, and the sole-driver round is what actually spends that: it ends on a body whose motion
    // answers the question the echoes only assert.
    static bool RunExclusivityOrders(Ctx ctx) {
        var passed = true;

        // Order 1 — ORDINARY-then-EXCLUSIVE: seat2 takes body:11 with an ordinary grant, then a DIFFERENT principal's
        // exclusive acquisition of that same concrete body is rejected. (A concrete ordinary holder blocks; the seeded
        // console Drive/all wildcard deliberately does NOT — that is what keeps the addon flow in (b) possible.)
        passed &= ExpectGrant(ctx: ctx, name: "ordinary-grant-accepted", line: $"world.grant seat2 drive body:{OrdinaryBodyIndex}",
            needle: $"[world.grant: seat2 drive body:{OrdinaryBodyIndex}]");
        // The reject echo omits the trailing "exclusive" (only the ACCEPT line carries it), so the needle stops at the
        // subject.
        passed &= ExpectGrant(ctx: ctx, name: "exclusive-after-ordinary-rejected", line: $"world.grant addon:{AutopilotName} drive body:{OrdinaryBodyIndex} exclusive",
            needle: $"[world.grant rejected: addon:{AutopilotName} drive body:{OrdinaryBodyIndex} ");

        Send(ctx: ctx, line: $"world.revoke seat2 drive body:{OrdinaryBodyIndex}");

        // Order 2 — EXCLUSIVE-then-ORDINARY: the addon takes body:12 exclusively (which SUCCEEDS despite the console's
        // seeded Drive/all — the wildcard is not a blocker at acquisition), then a different principal's ordinary grant
        // of that same body is rejected.
        passed &= ExpectGrant(ctx: ctx, name: "exclusive-grant-accepted", line: $"world.grant addon:{AutopilotName} drive body:{ExclusiveBodyIndex} exclusive",
            needle: $"[world.grant: addon:{AutopilotName} drive body:{ExclusiveBodyIndex} exclusive]");
        passed &= ExpectGrant(ctx: ctx, name: "ordinary-after-exclusive-rejected", line: $"world.grant seat3 drive body:{ExclusiveBodyIndex}",
            needle: $"[world.grant rejected: seat3 drive body:{ExclusiveBodyIndex}");

        // Sole-driver enforcement: an exclusively-held body admits its HOLDER's intents and nobody else's — not even the
        // console, which holds the seeded Drive/all wildcard.
        //
        // Hand the hold from the addon to seat4 first — a principal that submits nothing. That swap is what makes the
        // pose pair below mean anything: while the ADDON held body:12 it was also DRIVING it (the grant IS the driver's
        // body binding), so the body travelled under the hold whether the console's segment was denied or applied, and
        // no sample could tell the two apart. Under an inert holder the body has no effective driver at all, so the
        // hold sample is the released check's negative control.
        Send(ctx: ctx, line: $"world.revoke addon:{AutopilotName} drive body:{ExclusiveBodyIndex}");

        passed &= ExpectGrant(ctx: ctx, name: "inert-holder-takes-exclusive", line: $"world.grant seat4 drive body:{ExclusiveBodyIndex} exclusive",
            needle: $"[world.grant: seat4 drive body:{ExclusiveBodyIndex} exclusive]");

        Thread.Sleep(millisecondsTimeout: 400);

        var beforeHold = ReadWhere(ctx: ctx, index: ExclusivePlayerIndex);
        var mark = ctx.Collector.Count;

        // A full-forward segment, not the all-zero hold it used to be: a zero segment moves nothing even when accepted,
        // so a denial of it proved only that a string was printed. The IDENTICAL line is re-issued after the release
        // below, where it must actually travel.
        Send(ctx: ctx, line: $"player.run 1 0 0 1 {ExclusivePlayerIndex}");

        var deniedLine = Await(collector: ctx.Collector, mark: mark,
            predicate: l => (l.Contains(value: "[world.grant denied:") && l.Contains(value: "console") && l.Contains(value: $"body:{ExclusiveBodyIndex}")),
            deadlineSeconds: 15.0);

        passed &= Check(name: "exclusive-overrides-console-wildcard", ok: (deniedLine is not null),
            detail: (deniedLine?.Trim() ?? $"(no '[world.grant denied: console ... body:{ExclusiveBodyIndex} ...]' line — the wildcard was not overridden)"));

        // The denial is only half the claim. Sample the body over the window the dropped segment would have covered:
        // a denial that still let the segment through would show up here as travel.
        Thread.Sleep(millisecondsTimeout: 1400);

        var afterHold = ReadWhere(ctx: ctx, index: ExclusivePlayerIndex);
        var holdDrift = Distance(a: beforeHold, b: afterHold);

        passed &= Check(name: "held-body-inert-under-lease", ok: ((beforeHold is not null) && (afterHold is not null) && (holdDrift <= FrozenEpsilon)),
            detail: $"p{ExclusivePlayerIndex} {Fmt(pose: beforeHold)} -> {Fmt(pose: afterHold)} (delta {holdDrift:0.000} u, want <= {FrozenEpsilon})");

        // GROUND THE LEASE IN THE BODY. Drop the hold and re-issue the IDENTICAL command — it must now be accepted (no
        // fresh denial) and the body must actually travel. The lease is exactly that difference: the same input, over
        // the same real body, inert under the hold and effective without it — both halves now measured on the pose.
        Send(ctx: ctx, line: $"world.revoke seat4 drive body:{ExclusiveBodyIndex}");

        Thread.Sleep(millisecondsTimeout: 600);

        var beforeRelease = ReadWhere(ctx: ctx, index: ExclusivePlayerIndex);

        mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"player.run 1 0 0 1 {ExclusivePlayerIndex}");

        Thread.Sleep(millisecondsTimeout: 1400);

        var afterRelease = ReadWhere(ctx: ctx, index: ExclusivePlayerIndex);
        var releaseDistance = Distance(a: beforeRelease, b: afterRelease);

        passed &= Check(name: "released-body-accepts-console-drive",
            ok: ((beforeRelease is not null) && (afterRelease is not null) && (releaseDistance > MovedEpsilon)),
            detail: $"p{ExclusivePlayerIndex} {Fmt(pose: beforeRelease)} -> {Fmt(pose: afterRelease)} (delta {releaseDistance:0.000} u, want > {MovedEpsilon})");

        var reDeniedLine = Await(collector: ctx.Collector, mark: mark,
            predicate: l => (l.Contains(value: "[world.grant denied:") && l.Contains(value: "console") && l.Contains(value: $"body:{ExclusiveBodyIndex}")),
            deadlineSeconds: 1.0);

        passed &= Check(name: "released-body-stops-denying-console", ok: (reDeniedLine is null),
            detail: (reDeniedLine?.Trim() ?? "no denial after the exclusive hold was revoked"));

        // (f) EXCLUSIVE WILDCARD: an "exclusively own everything" claim is rejected OUTRIGHT — an exclusive
        // reservation must name a concrete subject, checked on a fresh table AND with a concrete ordinary hold
        // already present.
        passed &= ExpectGrant(ctx: ctx, name: "exclusive-all-rejected-fresh-table",
            line: $"world.grant addon:{AutopilotName} drive all exclusive",
            needle: $"[world.grant rejected: addon:{AutopilotName} drive all — ");

        passed &= ExpectGrant(ctx: ctx, name: "ordinary-concrete-hold-for-wildcard-order", line: $"world.grant seat2 drive body:{OrdinaryBodyIndex}",
            needle: $"[world.grant: seat2 drive body:{OrdinaryBodyIndex}]");
        passed &= ExpectGrant(ctx: ctx, name: "exclusive-all-rejected-after-concrete-hold",
            line: $"world.grant addon:{AutopilotName} drive all exclusive",
            needle: $"[world.grant rejected: addon:{AutopilotName} drive all — ");
        Send(ctx: ctx, line: $"world.revoke seat2 drive body:{OrdinaryBodyIndex}");

        // Five deliberate refusals: the two order rejections, the console segment denied under the lease, and the two
        // exclusive-'all' rejections.
        passed &= SettleWireErrors(ctx: ctx, name: "exclusivity-round-refused-only-its-five", expected: 5);

        return passed;
    }

    // (g): an exclusive SECTION hold must be acquirable on a DEFAULT table — the seeded per-section Mutate rows
    // are the permissive backdrop's concrete spelling and never block a reservation. While seat1 holds section:scene
    // exclusively, the console's scene mutation is denied AT THE GRANT BOUNDARY (before compose; the denial also rides
    // the EchoTap toast channel ui-floor proved). Revoking the hold restores the console: the identical command then
    // fails at COMPOSE instead (no such row id), proving the grant boundary passed.
    static bool RunExclusiveSectionRound(Ctx ctx) {
        var passed = true;

        passed &= ExpectGrant(ctx: ctx, name: "exclusive-section-accepted-on-default-table",
            line: "world.grant seat1 mutate section:scene exclusive",
            needle: "[world.grant: seat1 mutate section:scene exclusive]");

        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.grants seat1");

        var grantsLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.grants:"), deadlineSeconds: 15.0);

        passed &= Check(name: "exclusive-section-marked", ok: ((grantsLine is not null) && grantsLine.Contains(value: "mutate/section:scene(x)")),
            detail: (grantsLine?.Trim() ?? "(no world.grants echo)"));

        mark = ctx.Collector.Count;
        Send(ctx: ctx, line: "world.scene.row.remove missing-row");

        var deniedLine = Await(collector: ctx.Collector, mark: mark,
            predicate: l => (l.Contains(value: "[world.grant denied: console cannot mutate section:scene") && l.Contains(value: "RemoveSceneRow 'missing-row' dropped")),
            deadlineSeconds: 15.0);

        passed &= Check(name: "exclusive-section-denies-console", ok: (deniedLine is not null),
            detail: (deniedLine?.Trim() ?? "(no '[world.grant denied: console ... section:scene ...]' line)"));

        Send(ctx: ctx, line: "world.revoke seat1 mutate section:scene");
        mark = ctx.Collector.Count;
        Send(ctx: ctx, line: "world.scene.row.remove missing-row");

        var composeLine = Await(collector: ctx.Collector, mark: mark,
            predicate: l => l.Contains(value: "[world.mutation rejected: RemoveSceneRow 'missing-row'"),
            deadlineSeconds: 15.0);

        passed &= Check(name: "revoke-restores-console-mutate", ok: (composeLine is not null),
            detail: (composeLine?.Trim() ?? "(no compose-stage '[world.mutation rejected: ...]' line — the grant boundary still denies)"));

        // Two deliberate refusals: the grant-boundary denial and the compose-stage rejection that replaces it.
        passed &= SettleWireErrors(ctx: ctx, name: "section-round-refused-only-its-two", expected: 2);

        return passed;
    }

    // (h): SetPlayerSection is gated on the CONCRETE profile:<id> Edit subject. The seeded Edit/all wildcard
    // keeps local play unchanged (the baseline edit applies); revoking it denies every profile edit; a narrow
    // profile:amber grant restores exactly amber while cobalt stays denied. The store was cleared before launch, so
    // the seeded catalog ids (amber, cobalt) are deterministic; RunGrants restores the real store afterward.
    static bool RunProfileSubjectRound(Ctx ctx) {
        var passed = true;

        passed &= ExpectGrant(ctx: ctx, name: "edit-all-wildcard-baseline",
            line: "profile.section amber preferences {\"theme\":\"dark\",\"hud\":true}",
            needle: "[profile.section: amber preferences applied]");
        passed &= ExpectGrant(ctx: ctx, name: "console-loses-edit-all",
            line: "world.revoke console edit all",
            needle: "[world.revoke: console edit all]");
        passed &= ExpectGrant(ctx: ctx, name: "edit-denied-without-grant",
            line: "profile.section amber preferences {\"theme\":\"light\",\"hud\":true}",
            needle: "[profile.section: console cannot edit profile:amber]");
        passed &= ExpectGrant(ctx: ctx, name: "profile-grant-accepted",
            line: "world.grant console edit profile:amber",
            needle: "[world.grant: console edit profile:amber]");
        passed &= ExpectGrant(ctx: ctx, name: "edit-applies-with-profile-grant",
            line: "profile.section amber preferences {\"theme\":\"dark\",\"hud\":true}",
            needle: "[profile.section: amber preferences applied]");
        passed &= ExpectGrant(ctx: ctx, name: "other-profile-still-denied",
            line: "profile.section cobalt preferences {\"theme\":\"dark\",\"hud\":true}",
            needle: "[profile.section: console cannot edit profile:cobalt]");
        passed &= ExpectGrant(ctx: ctx, name: "edit-all-restored",
            line: "world.grant console edit all",
            needle: "[world.grant: console edit all]");
        // Two deliberate refusals: the ungranted amber edit and the never-granted cobalt one.
        passed &= SettleWireErrors(ctx: ctx, name: "profile-round-refused-only-its-two", expected: 2);

        return passed;
    }

    // Submit a grant/revoke line and assert a settled accept/reject echo appears (the server prints it synchronously).
    static bool ExpectGrant(Ctx ctx, string name, string line, string needle) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: line);

        var hit = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: needle), deadlineSeconds: 15.0);

        return Check(name: name, ok: (hit is not null), detail: (hit?.Trim() ?? $"(no line containing '{needle}')"));
    }

    static bool SettleWireErrors(Ctx ctx, string name, int expected) {
        return ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: name, expected: expected);
    }

    // Read a body's INTENT SOURCE back through player.control's echo form and assert it. Every displacement check in
    // this suite is an argument about WHO moved a body, and the source is the premise that argument rests on: it says
    // whether the ambient wander producer can touch the body at all.
    static bool ExpectControl(Ctx ctx, string name, int index, string word) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"player.control {index}");

        var hit = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[player.control: p{index} is "), deadlineSeconds: 15.0);

        return Check(name: name, ok: ((hit is not null) && hit.Contains(value: $"is {word}]")), detail: (hit?.Trim() ?? $"(no '[player.control: p{index} is ...]' echo)"));
    }

    // A mutation verb (Simulation-routed, quiet ack) followed by a world.status read: the stdin drain barrier holds the
    // Immediate world.status behind the pending Simulation submission, so its answer reflects the applied state for
    // free — no polling needed. Also asserts the server's own loud accept line appeared somewhere in the same window.
    static bool MutateAndExpectStatus(Ctx ctx, string name, string command, string appliedNeedle, int dirty) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: command);
        Send(ctx: ctx, line: "world.status");

        var statusLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);
        var statusOk = Check(name: $"{name}-status", ok: ((statusLine is not null) && statusLine.Contains(value: $"dirty {dirty} undoable {dirty}]")), detail: (statusLine?.Trim() ?? "(no world.status echo)"));

        var snapshot = ctx.Collector.Snapshot();
        var appliedFound = false;

        for (var i = mark; (i < snapshot.Length); i++) {
            if (snapshot[i].Contains(value: appliedNeedle)) {
                appliedFound = true;

                break;
            }
        }

        var appliedOk = Check(name: $"{name}-echo", ok: appliedFound, detail: (appliedFound ? "seen" : $"missing '{appliedNeedle}'"));

        return (statusOk && appliedOk);
    }

    static bool ExpectStatus(Ctx ctx, string name, int dirty) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "world.status");

        var statusLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);

        return Check(name: name, ok: ((statusLine is not null) && statusLine.Contains(value: $"dirty {dirty} undoable {dirty}]")), detail: (statusLine?.Trim() ?? "(no world.status echo)"));
    }

    static Pose? ReadWhere(Ctx ctx, int index) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"player.where {index}");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => ProofApp.WhereEcho.IsMatch(input: l), deadlineSeconds: 6.0);

        return ((line is not null) ? ParsePose(line: line) : null);
    }
    static Pose? ParsePose(string line) {
        var match = ProofApp.WhereEcho.Match(input: line);

        return (match.Success
            ? new Pose(
                X: double.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv),
                Y: double.Parse(s: match.Groups[3].Value, provider: ProofApp.Inv),
                Z: double.Parse(s: match.Groups[4].Value, provider: ProofApp.Inv),
                Yaw: int.Parse(s: match.Groups[5].Value, provider: ProofApp.Inv),
                Pitch: int.Parse(s: match.Groups[6].Value, provider: ProofApp.Inv),
                Roll: int.Parse(s: match.Groups[7].Value, provider: ProofApp.Inv))
            : null);
    }
    // Euclidean distance between two samples, or NaN when either read failed (a NaN then fails BOTH the "moved beyond
    // epsilon" and "frozen within epsilon" comparisons — the correct behavior for a missing sample, not a vacuous pass).
    static double Distance(Pose? a, Pose? b) {
        if ((a is not { } pa) || (b is not { } pb)) {
            return double.NaN;
        }

        var dx = (pb.X - pa.X);
        var dy = (pb.Y - pa.Y);
        var dz = (pb.Z - pa.Z);

        return Math.Sqrt(d: ((dx * dx) + (dy * dy) + (dz * dz)));
    }
    static string Fmt(Pose? pose) {
        return (pose is { } p ? $"({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})" : "(?)");
    }

    static ProcessStartInfo BuildPsi(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string? worldArg) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        if (worldArg is not null) {
            psi.ArgumentList.Add(item: "--world");
            psi.ArgumentList.Add(item: worldArg);
        }

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        return psi;
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    // Shader compilation and first-device startup can exceed an individual assertion's deadline on a cold machine.
    // player.stop is idempotent at boot and leaves the player at the authored spawn.
    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }
    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// BINDINGS — the player-document + layered binding-resolution proof
// (engine default ⊕ world overlay ⊕ profile ⊕ session), its console rebind surface
// (player.bind / player.bindings / profile.save / profile.doc), and persistence through
// puck.world.player.v1. Sessions against the REAL local player-document store — there is no CLI
// override for its path (WorldProfileStore addresses a fixed %LOCALAPPDATA% location) — so this
// proof backs up whatever the real world/ subtree holds (or its absence) before touching
// anything, and restores it in a finally no matter how the sessions finish. Never delete or
// revert files this proof did not itself create.
// ============================================================================================
static class BindingsProof {
    public static int RunBindings(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var worldDir = PlayerStorePaths.WorldDir();

        // Snapshot whatever the REAL catalog holds (the whole world/ subtree, byte-for-byte) before this proof
        // touches anything.
        var worldBackup = DirectoryBackup.Snapshot(dir: worldDir);
        var passed = true;

        try {
            DirectoryBackup.Clear(dir: worldDir);

            Console.WriteLine(value: "[proof] === bindings (a): session A — engine defaults, a live rebind, profile.save ===");

            var (sessionAPassed, revisionAfterSave) = RunSessionA(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            passed &= sessionAPassed;

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === bindings (b): session B — the rebind survives a relaunch ===");
            passed &= RunSessionB(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, revisionAfterSave: revisionAfterSave);

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === bindings (b2): the identity/motion/preferences SetPlayerSection variants (positive + malformed) ===");
            passed &= RunSectionEditSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === bindings (b3): the section edits survive a relaunch ===");
            passed &= RunSectionSurvives(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === bindings (c): session C — a per-world binding overlay merges, then live-removes ===");

            var kartRemapPath = Path.Combine(path1: projectPath, path2: "Assets", path3: "worlds", path4: "kart-remap.world.json");

            passed &= RunSessionC(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldPath: kartRemapPath);
        }
        finally {
            DirectoryBackup.Restore(snapshot: worldBackup);
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] bindings proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // Session A: boot the baked-default world against a freshly-cleared store (seeds the built-in default catalog —
    // amber/cobalt/moss/violet, revision 1, boot=amber). Asserts the engine-default composed mapping, a live rebind
    // through player.bind, and a profile.save fold-and-persist (revision bumps, read back through profile.doc).
    static (bool Passed, long RevisionAfterSave) RunSessionA(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;
        var revisionAfterSave = -1L;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (baked-default world, fresh player-document store)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return (false, revisionAfterSave);
            }

            var revision0 = ReadRevision(ctx: ctx, name: "revision-baseline");

            passed &= Check(name: "revision-baseline-present", ok: (revision0 is not null), detail: (revision0?.ToString(provider: ProofApp.Inv) ?? "(no profile.doc echo)"));

            // The engine-default composed mapping (no overlay, no profile, no session layer yet): spot-check three
            // sources across the base page.
            passed &= ExpectBindingsContains(ctx: ctx, name: "defaults-w-forward", seat: 1, needle: "keyboard.w→player.forward", wantPresent: true);
            passed &= ExpectBindingsContains(ctx: ctx, name: "defaults-space-primary", seat: 1, needle: "keyboard.space→player.primary", wantPresent: true);
            passed &= ExpectBindingsContains(ctx: ctx, name: "defaults-east-secondary", seat: 1, needle: "gamepad.buttonEast→player.secondary", wantPresent: true);

            // A live session rebind: keyboard.e -> player.forward, unsaved until profile.save.
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: "player.bind 1 keyboard.e player.forward");

            var bindLine = Await(collector: ctx.Collector, mark: mark,
                predicate: l => l.Contains(value: "[player.bind: seat 1 'keyboard.e' → 'player.forward'"), deadlineSeconds: 15.0);

            passed &= Check(name: "bind-echo", ok: (bindLine is not null), detail: (bindLine?.Trim() ?? "(no '[player.bind: ...]' echo)"));
            passed &= ExpectBindingsContains(ctx: ctx, name: "session-rebind-visible", seat: 1, needle: "keyboard.e→player.forward", wantPresent: true);

            // profile.save folds the session rebind into the boot profile ('amber' on a freshly seeded catalog) and
            // persists it through the server-owned player document.
            mark = ctx.Collector.Count;

            Send(ctx: ctx, line: "profile.save 1");

            var saveLine = Await(collector: ctx.Collector, mark: mark,
                predicate: l => l.Contains(value: "[profile.save: seat 1 → profile 'amber' bindings saved]"), deadlineSeconds: 15.0);

            passed &= Check(name: "profile-save-echo", ok: (saveLine is not null), detail: (saveLine?.Trim() ?? "(no '[profile.save: ...]' echo)"));

            var revision1 = ReadRevision(ctx: ctx, name: "revision-after-save");

            passed &= Check(name: "revision-bumped-by-save", ok: ((revision0 is { } r0) && (revision1 is { } r1) && (r1 > r0)),
                detail: $"{revision0?.ToString(provider: ProofApp.Inv) ?? "?"} -> {revision1?.ToString(provider: ProofApp.Inv) ?? "?"} (want strictly greater)");

            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "rebind-round-refused-nothing", expected: 0);
            revisionAfterSave = (revision1 ?? -1L);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return (passed, revisionAfterSave);
    }

    // Session B: relaunch against the SAME real store session A just saved (no --world). Asserts the rebind survived
    // (puck.world.player.v1 persistence) and that a plain boot does not itself bump the revision.
    static bool RunSessionB(string exe, string repoRoot, int width, int height, int exitAfterSeconds, long revisionAfterSave) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (baked-default world, persisted player-document store)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= ExpectBindingsContains(ctx: ctx, name: "rebind-survives-relaunch", seat: 1, needle: "keyboard.e→player.forward", wantPresent: true);

            var revisionB = ReadRevision(ctx: ctx, name: "revision-after-relaunch");

            passed &= Check(name: "revision-unchanged-by-plain-boot", ok: (revisionB == revisionAfterSave),
                detail: $"{revisionB?.ToString(provider: ProofApp.Inv) ?? "?"} (want == {revisionAfterSave})");
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "relaunch-round-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // Session C: boot --world <kart-remap> (the checked-in bindingOverlays example) and assert its lane remap merges
    // over the engine default from tick 0, then world.bindings.remove live-recomposes every seat back to it.
    static bool RunSessionC(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string worldPath) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: worldPath);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --world {worldPath} --width {width} --height {height}");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            // The overlay merges from boot (WorldSeatBindings is constructed with the world's bindingOverlays before
            // the first tick): East now fires player.primary, and no longer the engine-default player.secondary.
            passed &= ExpectBindingsContains(ctx: ctx, name: "overlay-merges-east-primary", seat: 1, needle: "gamepad.buttonEast→player.primary", wantPresent: true);
            passed &= ExpectBindingsContains(ctx: ctx, name: "overlay-supersedes-east-secondary", seat: 1, needle: "gamepad.buttonEast→player.secondary", wantPresent: false);

            // Live removal: a Simulation-routed mutation, so the stdin barrier holds the following Immediate
            // player.bindings read until it applies (and WorldSimulation.Step's SyncOverlays recomposes in the same
            // tick) — the same read-after-write guarantee MutateProof's world.status pattern relies on.
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: "world.bindings.remove kart-remap");

            var removedLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.mutation: RemoveBindingOverlay 'kart-remap' applied]"), deadlineSeconds: 15.0);

            passed &= Check(name: "overlay-remove-applied", ok: (removedLine is not null), detail: (removedLine?.Trim() ?? "(no '[world.mutation: RemoveBindingOverlay ...]' echo)"));
            passed &= ExpectBindingsContains(ctx: ctx, name: "removal-recomposes-east-secondary", seat: 1, needle: "gamepad.buttonEast→player.secondary", wantPresent: true);
            passed &= ExpectBindingsContains(ctx: ctx, name: "removal-drops-east-primary", seat: 1, needle: "gamepad.buttonEast→player.primary", wantPresent: false);
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "overlay-round-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // (b2): the identity/motion/preferences SetPlayerSection variants. Boots against the persisted store from
    // session A/B (boot profile = amber), and drives every declared section through the raw profile.section reflection:
    // each POSITIVE edit applies + bumps the revision + shows up in profile.doc, an IDENTITY edit LIVE-refreshes the
    // seated participant (profile.show reads the roster's shared handle — not stale), and each MALFORMED payload rejects
    // with a reason while leaving the revision untouched. The candidate-document validator is exercised directly by the
    // cross-profile duplicate-name rejection.
    static bool RunSectionEditSession(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (persisted player-document store)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            // --- IDENTITY (positive): rename+recolor the seated boot profile 'amber' ---
            var revBeforeIdentity = ReadRevision(ctx: ctx, name: "rev-before-identity");

            passed &= Check(name: "rev-before-identity-present", ok: (revBeforeIdentity is not null), detail: (revBeforeIdentity?.ToString(provider: ProofApp.Inv) ?? "(no profile.doc echo)"));
            passed &= ExpectSection(ctx: ctx, name: "identity-applies", line: "profile.section amber identity {\"name\":\"amberedit\",\"color\":\"#0A141E\"}", needle: "amber identity applied");

            var revAfterIdentity = ReadRevision(ctx: ctx, name: "rev-after-identity");

            passed &= Check(name: "identity-bumps-revision", ok: ((revBeforeIdentity is { } r0) && (revAfterIdentity is { } r1) && (r1 > r0)),
                detail: $"{revBeforeIdentity?.ToString(provider: ProofApp.Inv) ?? "?"} -> {revAfterIdentity?.ToString(provider: ProofApp.Inv) ?? "?"} (want strictly greater)");
            passed &= ExpectDocContains(ctx: ctx, name: "doc-has-new-name", needle: "\"name\":\"amberedit\"");
            passed &= ExpectDocContains(ctx: ctx, name: "doc-has-new-color", needle: "\"color\":\"#0A141E\"");
            // The live SEAT refresh: profile.show reads the roster's shared handle (the participant path), so the rename
            // and recolor are visible on the seated player — no stale cached identity survives the edit.
            passed &= ExpectShowContains(ctx: ctx, name: "seat-identity-refreshed", index: 1, needles: ["amberedit", "#0A141E"]);

            // --- IDENTITY (malformed: bad JSON) rejects and does not bump ---
            var revBeforeBadIdentity = ReadRevision(ctx: ctx, name: "rev-before-bad-identity");

            passed &= ExpectSection(ctx: ctx, name: "identity-malformed-rejects", line: "profile.section amber identity {oops", needle: "did not parse");
            passed &= ExpectRevisionUnchanged(ctx: ctx, name: "identity-malformed-no-bump", before: revBeforeBadIdentity);

            // --- IDENTITY (validation: a cross-profile duplicate name) rejects through the WHOLE-document thick gate ---
            passed &= ExpectSection(ctx: ctx, name: "identity-duplicate-rejects", line: "profile.section cobalt identity {\"name\":\"amberedit\",\"color\":\"#334455\"}", needle: "duplicated");

            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "identity-round-refused-only-its-two", expected: 2);

            // --- MOTION (positive) ---
            var revBeforeMotion = ReadRevision(ctx: ctx, name: "rev-before-motion");

            passed &= ExpectSection(ctx: ctx, name: "motion-applies", line: "profile.section amber motion {\"moveSpeed\":9,\"turnSpeed\":3,\"invertLookX\":true}", needle: "amber motion applied");

            var revAfterMotion = ReadRevision(ctx: ctx, name: "rev-after-motion");

            passed &= Check(name: "motion-bumps-revision", ok: ((revBeforeMotion is { } m0) && (revAfterMotion is { } m1) && (m1 > m0)),
                detail: $"{revBeforeMotion?.ToString(provider: ProofApp.Inv) ?? "?"} -> {revAfterMotion?.ToString(provider: ProofApp.Inv) ?? "?"} (want strictly greater)");
            passed &= ExpectDocContains(ctx: ctx, name: "doc-has-new-speed", needle: "\"moveSpeed\":9");
            passed &= ExpectShowContains(ctx: ctx, name: "seat-motion-refreshed", index: 1, needles: ["speed=9"]);

            // --- MOTION (malformed: a non-positive speed) rejects at the thick gate and does not bump ---
            var revBeforeBadMotion = ReadRevision(ctx: ctx, name: "rev-before-bad-motion");

            passed &= ExpectSection(ctx: ctx, name: "motion-malformed-rejects", line: "profile.section amber motion {\"moveSpeed\":-5,\"turnSpeed\":3,\"invertLookX\":false}", needle: "positive");
            passed &= ExpectRevisionUnchanged(ctx: ctx, name: "motion-malformed-no-bump", before: revBeforeBadMotion);

            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "motion-round-refused-only-its-one", expected: 1);

            // --- PREFERENCES (positive) ---
            var revBeforePrefs = ReadRevision(ctx: ctx, name: "rev-before-prefs");

            passed &= ExpectSection(ctx: ctx, name: "preferences-applies", line: "profile.section amber preferences {\"theme\":\"dark\",\"hud\":true}", needle: "amber preferences applied");

            var revAfterPrefs = ReadRevision(ctx: ctx, name: "rev-after-prefs");

            passed &= Check(name: "preferences-bumps-revision", ok: ((revBeforePrefs is { } p0) && (revAfterPrefs is { } p1) && (p1 > p0)),
                detail: $"{revBeforePrefs?.ToString(provider: ProofApp.Inv) ?? "?"} -> {revAfterPrefs?.ToString(provider: ProofApp.Inv) ?? "?"} (want strictly greater)");
            passed &= ExpectDocContains(ctx: ctx, name: "doc-has-pref", needle: "\"theme\":\"dark\"");

            // --- PREFERENCES (malformed: not a JSON object) rejects and does not bump ---
            var revBeforeBadPrefs = ReadRevision(ctx: ctx, name: "rev-before-bad-prefs");

            passed &= ExpectSection(ctx: ctx, name: "preferences-malformed-rejects", line: "profile.section amber preferences [1,2,3]", needle: "did not parse");
            passed &= ExpectRevisionUnchanged(ctx: ctx, name: "preferences-malformed-no-bump", before: revBeforeBadPrefs);

            // --- BINDINGS (positive): a raw profile.section bindings edit on the SEATED profile 'amber' must reach
            // seat 1's ACTIVE mapping LIVE — no reseat, no restart. keyboard.q is unbound by default; this remaps it to
            // player.forward through the durable section, and player.bindings 1 must show it IMMEDIATELY afterwards.
            passed &= ExpectBindingsContains(ctx: ctx, name: "bindings-section-absent-before", seat: 1, needle: "keyboard.q→player.forward", wantPresent: false);

            var revBeforeBindings = ReadRevision(ctx: ctx, name: "rev-before-bindings");

            passed &= ExpectSection(ctx: ctx, name: "bindings-section-applies",
                line: "profile.section amber bindings {\"version\":\"puck.bindings.v1\",\"modifiers\":[],\"chords\":[{\"group\":\"play\",\"chord\":[],\"page\":{\"id\":\"base\",\"entries\":[{\"source\":\"keyboard.q\",\"command\":\"player.forward\",\"anyModifiers\":true}]}}]}",
                needle: "amber bindings applied");

            var revAfterBindings = ReadRevision(ctx: ctx, name: "rev-after-bindings");

            passed &= Check(name: "bindings-bumps-revision", ok: ((revBeforeBindings is { } bb0) && (revAfterBindings is { } bb1) && (bb1 > bb0)),
                detail: $"{revBeforeBindings?.ToString(provider: ProofApp.Inv) ?? "?"} -> {revAfterBindings?.ToString(provider: ProofApp.Inv) ?? "?"} (want strictly greater)");
            // The seated player's composed mapping now carries the durable rebind, with no reseat.
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "preferences-round-refused-only-its-one", expected: 1);
            passed &= ExpectBindingsContains(ctx: ctx, name: "bindings-section-live-no-reseat", seat: 1, needle: "keyboard.q→player.forward", wantPresent: true);
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "bindings-round-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // (b3): relaunch against the same real store and assert the identity/motion/preferences edits all survived (the
    // durable puck.world.player.v1 persistence path, not just the live handle).
    static bool RunSectionSurvives(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (persisted player-document store — section edits survive)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= ExpectDocContains(ctx: ctx, name: "survives-identity-name", needle: "\"name\":\"amberedit\"");
            passed &= ExpectDocContains(ctx: ctx, name: "survives-identity-color", needle: "\"color\":\"#0A141E\"");
            passed &= ExpectDocContains(ctx: ctx, name: "survives-motion-speed", needle: "\"moveSpeed\":9");
            passed &= ExpectDocContains(ctx: ctx, name: "survives-preferences", needle: "\"theme\":\"dark\"");
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "section-survival-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // Send a profile.section edit and assert its accept/reject echo contains a needle (both echoes start with
    // "[profile.section:"; the needle distinguishes an "... applied" from a rejection reason).
    static bool ExpectSection(Ctx ctx, string name, string line, string needle) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: line);

        var echo = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[profile.section:"), deadlineSeconds: 15.0);

        return Check(name: name, ok: ((echo is not null) && echo.Contains(value: needle, comparisonType: StringComparison.Ordinal)),
            detail: (echo?.Trim() ?? $"(no '[profile.section: ...]' echo containing '{needle}')"));
    }

    // profile.doc is the whole-document JSON echo; assert a needle appears (a section value survived into the document).
    static bool ExpectDocContains(Ctx ctx, string name, string needle) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "profile.doc");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "puck.world.player.v1"), deadlineSeconds: 15.0);

        return Check(name: name, ok: ((line is not null) && line.Contains(value: needle, comparisonType: StringComparison.Ordinal)),
            detail: (((line is not null) && line.Contains(value: needle, comparisonType: StringComparison.Ordinal)) ? $"has '{needle}'" : $"missing '{needle}' in {(line?.Trim() ?? "(no profile.doc echo)")}"));
    }

    // profile.show <index> reads the SEATED handle through the roster — asserts every needle appears (the live refresh).
    static bool ExpectShowContains(Ctx ctx, string name, int index, string[] needles) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"profile.show {index}");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[profile.show: player {index}"), deadlineSeconds: 15.0);
        var text = (line ?? string.Empty);
        var all = (line is not null) && needles.All(predicate: n => text.Contains(value: n, comparisonType: StringComparison.Ordinal));

        return Check(name: name, ok: all, detail: (all ? text.Trim() : $"missing one of [{string.Join(separator: ", ", values: needles)}] in {(text.Length > 0 ? text.Trim() : "(no '[profile.show: ...]' echo)")}"));
    }

    static bool ExpectRevisionUnchanged(Ctx ctx, string name, long? before) {
        var after = ReadRevision(ctx: ctx, name: $"{name}-read");

        return Check(name: name, ok: ((before is { } b) && (after is { } a) && (a == b)),
            detail: $"{before?.ToString(provider: ProofApp.Inv) ?? "?"} -> {after?.ToString(provider: ProofApp.Inv) ?? "?"} (want unchanged)");
    }

    // player.bindings <seat> is an Immediate read; asserts a source->command needle is present (or absent) in the
    // composed active mapping.
    static bool ExpectBindingsContains(Ctx ctx, string name, int seat, string needle, bool wantPresent) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: $"player.bindings {seat}");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: $"[player.bindings: seat {seat}"), deadlineSeconds: 15.0);
        var text = (line ?? string.Empty);
        var present = text.Contains(value: needle, comparisonType: StringComparison.Ordinal);

        return Check(name: name, ok: (line is not null) && (present == wantPresent),
            detail: $"{(present ? "has" : "missing")} '{needle}' (want {(wantPresent ? "present" : "absent")}): {(text.Length > 0 ? text.Trim() : "(no '[player.bindings: ...]' echo)")}");
    }

    // profile.doc is an Immediate echo of the whole server-owned puck.world.player.v1 document as compact JSON (one
    // line, no "[profile.doc: ...]" wrapper) — extracts its top-level "revision" field.
    static long? ReadRevision(Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "profile.doc");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "puck.world.player.v1"), deadlineSeconds: 15.0);

        if (line is null) {
            _ = Check(name: name, ok: false, detail: "(no profile.doc echo)");

            return null;
        }

        var match = Regex.Match(input: line, pattern: @"""revision""\s*:\s*(-?\d+)");

        return (match.Success ? long.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv) : null);
    }

    static ProcessStartInfo BuildPsi(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string? worldArg) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        if (worldArg is not null) {
            psi.ArgumentList.Add(item: "--world");
            psi.ArgumentList.Add(item: worldArg);
        }

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        return psi;
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    // Shader compilation and first-device startup can exceed an individual assertion's deadline on a cold machine.
    // player.stop is idempotent at boot and leaves the player at the authored spawn.
    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }
    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// Cloud-readiness proof, proven against the local backend only. storage.status is the
// control surface; the honest baseline (cloud unwired, identity declined/override, endpoint reflection) and the
// Revision/version-token ordering + clobber-guard fields it reports are the whole surface today.
// ============================================================================================

static class StorageProof {
    public static int RunStorage(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var worldDir = PlayerStorePaths.WorldDir();
        var catalogPath = PlayerStorePaths.CatalogPath();
        var worldLocalPath = PlayerStorePaths.LocalPath();
        var profilesDir = PlayerStorePaths.ProfilesDir();

        // Snapshot whatever the REAL catalog holds (the whole world/ subtree, byte-for-byte) before this proof
        // touches anything.
        var worldBackup = DirectoryBackup.Snapshot(dir: worldDir);
        var passed = true;

        try {
            DirectoryBackup.Clear(dir: worldDir);

            Console.WriteLine(value: "[proof] === storage (a): fresh boot — honest baseline, a revision-bumping mutation, the on-disk split layout ===");

            var (basePassed, revisionAfterSet) = RunBaselineAndMutate(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            passed &= basePassed;
            passed &= Check(name: "catalog-json-on-disk", ok: File.Exists(path: catalogPath), detail: catalogPath);
            passed &= Check(name: "local-json-on-disk", ok: File.Exists(path: worldLocalPath), detail: worldLocalPath);

            var profileBlobs = (Directory.Exists(path: profilesDir) ? Directory.GetFiles(path: profilesDir, searchPattern: "*.json") : []);

            passed &= Check(name: "profile-blobs-on-disk", ok: (profileBlobs.Length > 0), detail: $"{profileBlobs.Length} blob(s) under {profilesDir}");

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === storage (b): relaunch — the persisted revision survives ===");
            passed &= RunRelaunchPersists(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, expectedRevision: revisionAfterSet);

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === storage (c): --user-id <a valid oid Guid> — explicit override ===");
            passed &= RunUserIdOverride(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === storage (d): --user-id not-a-guid — declines loudly ===");
            passed &= RunUserIdDeclines(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);
        }
        finally {
            DirectoryBackup.Restore(snapshot: worldBackup);
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] storage proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // Session (a): boot against the freshly-cleared REAL store. Asserts storage.status's honest baseline (local
    // authoritative/cloud unwired, identity declined, endpoint none, a present catalog revision + version token), then
    // the cheapest revision-bumping verb (profile.set) is asserted to bump the revision, change the version token, and
    // flip dirty on.
    static (bool Passed, long RevisionAfterSet) RunBaselineAndMutate(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null, userIdArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;
        var revisionAfterSet = -1L;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (fresh player-document store, no identity override)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return (false, revisionAfterSet);
            }

            var (baseline, baselineOk) = ReadStorageStatus(ctx: ctx, tag: "baseline");

            passed &= baselineOk;
            passed &= Check(name: "tier-local-authoritative", ok: baseline.Contains(value: "tier local (authoritative); cloud unwired"), detail: baseline);
            passed &= Check(name: "identity-declined-baseline", ok: baseline.Contains(value: "identity declined"), detail: baseline);
            passed &= Check(name: "endpoint-none-baseline", ok: baseline.Contains(value: "endpoint none"), detail: baseline);

            var revision0 = ExtractLong(text: baseline, pattern: @"catalog revision (-?\d+)");
            var token0 = ExtractToken(text: baseline);

            passed &= Check(name: "catalog-revision-present-baseline", ok: (revision0 is not null), detail: baseline);
            passed &= Check(name: "version-token-present-baseline", ok: (!string.IsNullOrEmpty(value: token0) && (token0 != "none")), detail: baseline);

            // profile.set speed 7 1 — the cheapest revision-bumping verb (ProfileCommandModule.SetFloat calls
            // WorldProfiles.Save directly): boot profile 'amber' defaults to speed 4, so old -> new is deterministic.
            var mark = ctx.Collector.Count;

            Send(ctx: ctx, line: "profile.set speed 7 1");

            var setLine = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[profile.set:"), deadlineSeconds: 15.0);

            passed &= Check(name: "profile-set-echo", ok: (setLine is not null), detail: (setLine?.Trim() ?? "(no '[profile.set: ...]' echo)"));

            var (afterSet, afterSetOk) = ReadStorageStatus(ctx: ctx, tag: "after-set");

            passed &= afterSetOk;

            var revision1 = ExtractLong(text: afterSet, pattern: @"catalog revision (-?\d+)");
            var token1 = ExtractToken(text: afterSet);

            passed &= Check(name: "revision-bumped-by-mutation", ok: ((revision0 is { } r0) && (revision1 is { } r1) && (r1 > r0)),
                detail: $"{revision0?.ToString(provider: ProofApp.Inv) ?? "?"} -> {revision1?.ToString(provider: ProofApp.Inv) ?? "?"} (want strictly greater)");
            passed &= Check(name: "version-token-changed-by-mutation", ok: ((token0 is not null) && (token1 is not null) && (token0 != token1)),
                detail: $"{token0 ?? "?"} -> {token1 ?? "?"} (want different)");
            passed &= Check(name: "dirty-on-after-mutation", ok: afterSet.Contains(value: "dirty on"), detail: afterSet);

            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "storage-round-refused-nothing", expected: 0);
            revisionAfterSet = (revision1 ?? -1L);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return (passed, revisionAfterSet);
    }

    // Session (b): relaunch against the SAME real store session (a) just saved (no --world, no --user-id). Asserts
    // storage.status reports the same persisted revision — cross-session persistence on the local backend.
    static bool RunRelaunchPersists(string exe, string repoRoot, int width, int height, int exitAfterSeconds, long expectedRevision) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null, userIdArg: null);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (persisted player-document store)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            var (status, ok) = ReadStorageStatus(ctx: ctx, tag: "relaunch");

            passed &= ok;

            var revision = ExtractLong(text: status, pattern: @"catalog revision (-?\d+)");

            passed &= Check(name: "revision-persisted-across-relaunch", ok: (revision == expectedRevision),
                detail: $"{revision?.ToString(provider: ProofApp.Inv) ?? "?"} (want == {expectedRevision.ToString(provider: ProofApp.Inv)})");
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "storage-relaunch-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // Session (c): boot with --user-id <a valid oid-shaped Guid>. Asserts the explicit-override identity echo
    // (the ExplicitOverridePlayerStorageIdentityResolver path).
    static bool RunUserIdOverride(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        const string userId = "11112222-3333-4444-5555-666677778888";

        return RunUserIdSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, userId: userId,
            name: "identity-explicit-override", needle: $"identity explicit override userId={userId}", label: $"--user-id {userId}");
    }

    // Session (d): boot with --user-id not-a-guid. Asserts the resolver declines loudly rather than inventing a
    // container (the non-Guid-override branch).
    static bool RunUserIdDeclines(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        const string userId = "not-a-guid";

        return RunUserIdSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, userId: userId,
            name: "identity-declined-bad-user-id", needle: $"identity declined — explicit override userId '{userId}' is not a container Guid; declining (local-only)",
            label: $"--user-id {userId}");
    }

    static bool RunUserIdSession(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string userId, string name, string needle, string label) {
        var psi = BuildPsi(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, worldArg: null, userIdArg: userId);
        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} ({label})");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return false;
            }

            var (status, ok) = ReadStorageStatus(ctx: ctx, tag: name);

            passed &= ok;
            passed &= Check(name: name, ok: status.Contains(value: needle, comparisonType: StringComparison.Ordinal), detail: status);
            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "storage-identity-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        return passed;
    }

    // storage.status is an Immediate echo of the honest local storage state — one line, "[storage.status: ...]".
    static (string Text, bool Ok) ReadStorageStatus(Ctx ctx, string tag) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "storage.status");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[storage.status:"), deadlineSeconds: 15.0);
        var ok = Check(name: $"storage-status-echo-{tag}", ok: (line is not null), detail: (line?.Trim() ?? "(no '[storage.status: ...]' echo)"));

        return ((line ?? string.Empty), ok);
    }

    static long? ExtractLong(string text, string pattern) {
        var match = Regex.Match(input: text, pattern: pattern);

        return (match.Success ? long.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv) : null);
    }

    // storage.status's "token <value> lastWrite ..." segment — <value> is a hex digest, "none", or (theoretically)
    // never whitespace, so a non-whitespace run up to " lastWrite" isolates it.
    static string? ExtractToken(string text) {
        var match = Regex.Match(input: text, pattern: @"token (\S+) lastWrite");

        return (match.Success ? match.Groups[1].Value : null);
    }

    // player.stop is idempotent at boot and leaves the player at the authored spawn; its echo proves the simulation
    // (and the console dispatch behind the stdin barrier) is ready.
    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }
    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }
    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
    static ProcessStartInfo BuildPsi(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string? worldArg, string? userIdArg) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        if (worldArg is not null) {
            psi.ArgumentList.Add(item: "--world");
            psi.ArgumentList.Add(item: worldArg);
        }

        if (userIdArg is not null) {
            psi.ArgumentList.Add(item: "--user-id");
            psi.ArgumentList.Add(item: userIdArg);
        }

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        return psi;
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);
}

// ============================================================================================
// record — native-capture proof
// ============================================================================================
static class RecordProof {
    public static int RunRecord(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var seconds = Math.Clamp(opts.GetInt(fallback: 4, name: "--seconds"), 2, 60);
        var outPath = opts.Get(name: "--out");

        var repoRoot = ProofApp.RepoRoot();
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                return ProofApp.Fail(message: $"build failed ({build.ExitCode})");
            }
        }

        var exe = FindExe(projectPath: projectPath);

        if (exe is null) {
            return ProofApp.Fail(message: "Puck.World.exe not found under bin/Release — build first");
        }

        var exitAfterSeconds = (seconds + 20);
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();
        var passed = true;
        var started = false;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        Console.WriteLine(value: $"[proof] launching: {exe} --width {width} --height {height} (native-capture proof, {seconds}s)");

        try {
            _ = process.Start();
            started = true;
            stopwatch.Start();
            collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
            collector.Start(reader: process.StandardError, stopwatch: stopwatch);

            var stdin = process.StandardInput;

            stdin.AutoFlush = true;

            var ctx = new Ctx(Collector: collector, Process: process, Stdin: stdin);

            if (!WaitForConsole(ctx: ctx)) {
                return 1;
            }

            // The recording document the world resolved at boot (the checked-in asset or the baked default).
            var docLine = Await(collector: collector, mark: 0, predicate: l => l.Contains(value: "[recording] document:"), deadlineSeconds: 5.0);

            passed &= Check(name: "recording-document-resolved", ok: (docLine is not null), detail: (docLine?.Trim() ?? "(no [recording] document: boot line)"));
            passed &= CheckOverlayPresence(docLine: docLine);

            // (a) idle before start.
            passed &= ExpectStatus(ctx: ctx, needle: "idle", name: "status-idle-before-start");

            // (b) start — arm the session; echo names the negotiated codec + resolved path (declines are loud).
            var startMark = collector.Count;

            Send(ctx: ctx, line: "capture.start");

            var startLine = Await(collector: collector, mark: startMark, predicate: l => l.Contains(value: "[capture.start:"), deadlineSeconds: 30.0);
            var startOk = ((startLine is not null) && startLine.Contains(value: "recording ->"));

            passed &= Check(name: "capture-start-recording", ok: startOk, detail: (startLine?.Trim() ?? "(no capture.start echo)"));

            if (!startOk) {
                return Finish(passed: false, process: process, started: started);
            }

            var recordingPath = Extract(line: startLine!, after: "recording -> ", until: " |");
            var codec = Extract(line: startLine!, after: "codec ", until: " |");

            Console.WriteLine(value: $"[proof]   negotiated codec: {codec} | path: {recordingPath}");

            // (c) ~seconds of the autonomous crowd moving.
            Thread.Sleep(millisecondsTimeout: (seconds * 1000));

            // (d) still recording mid-run.
            passed &= ExpectStatus(ctx: ctx, needle: "recording ->", name: "status-recording-midrun");

            // (e) stop — finalize the container.
            var stopMark = collector.Count;

            Send(ctx: ctx, line: "capture.stop");

            var stopLine = Await(collector: collector, mark: stopMark, predicate: l => l.Contains(value: "[capture.stop:"), deadlineSeconds: 30.0);

            passed &= Check(name: "capture-stop-wrote", ok: ((stopLine is not null) && stopLine.Contains(value: "wrote ")), detail: (stopLine?.Trim() ?? "(no capture.stop echo)"));

            // (f) idle again after stop.
            passed &= ExpectStatus(ctx: ctx, needle: "idle", name: "status-idle-after-stop");

            // (g) the produced container on disk.
            var fullPath = (Path.IsPathRooted(path: recordingPath) ? recordingPath : Path.Combine(path1: repoRoot, path2: recordingPath));

            passed &= ComposedShotKit.SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: "capture-round-refused-nothing", expected: 0);
            passed &= AssertContainer(fullPath: fullPath, codec: codec, outPath: outPath);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;

            if (started && !process.HasExited) {
                KillQuietly(process: process);
            }
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] record proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static int Finish(bool passed, Process process, bool started) {
        if (started && !process.HasExited) {
            KillQuietly(process: process);
        }

        Console.WriteLine(value: $"[proof] record proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // The recording document must carry the capture-only overlay — read the resolved file and assert
    // a text overlay row is present. A baked-default fallback (no file) has no overlays; that is a FAIL of this proof's
    // premise (the checked-in asset should have loaded), reported honestly.
    static bool CheckOverlayPresence(string? docLine) {
        if ((docLine is null) || docLine.Contains(value: "baked default")) {
            return Check(name: "overlay-present-in-document", ok: false, detail: "recording document fell back to the baked default (no overlays) - the checked-in asset did not load");
        }

        var marker = "[recording] document: ";
        var index = docLine.IndexOf(value: marker, comparisonType: StringComparison.Ordinal);
        var path = ((index >= 0) ? docLine[(index + marker.Length)..].Trim() : "");
        var ok = (File.Exists(path: path) && File.ReadAllText(path: path).Contains(value: "\"overlays\""));

        return Check(name: "overlay-present-in-document", ok: ok, detail: (ok ? $"overlays present in {Path.GetFileName(path: path)}" : $"no overlays in {path}"));
    }

    static bool AssertContainer(string fullPath, string codec, string? outPath) {
        var exists = File.Exists(path: fullPath);
        var ok = Check(name: "file-exists", ok: exists, detail: fullPath);

        if (!exists) {
            return false;
        }

        var bytes = File.ReadAllBytes(path: fullPath);

        ok &= Check(name: "file-non-trivial", ok: (bytes.Length > 8000), detail: $"{bytes.Length} bytes");

        var walk = MiniEbml.Walk(data: bytes);
        var audioOnly = codec.Contains(value: "audio only");
        var expectDocType = (string.Equals(a: codec, b: "V_AV1", comparisonType: StringComparison.Ordinal) ? "webm" : "matroska");

        ok &= Check(name: "ebml-doctype-matches-codec", ok: string.Equals(a: walk.DocType, b: expectDocType, comparisonType: StringComparison.Ordinal), detail: $"docType={walk.DocType} codec={codec} (want {expectDocType})");
        ok &= Check(name: "audio-track-present", ok: walk.HasAudioTrack, detail: (walk.HasAudioTrack ? "A_OPUS track present" : "no audio track"));

        if (!audioOnly) {
            ok &= Check(name: "video-track-present", ok: walk.HasVideoTrack, detail: (walk.HasVideoTrack ? $"video track {walk.VideoCodecId}" : "no video track"));
        }

        if (outPath is not null) {
            try {
                Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: outPath))!);
                File.Copy(sourceFileName: fullPath, destFileName: outPath, overwrite: true);
                Console.WriteLine(value: $"[proof]   copied artifact -> {outPath} ({bytes.Length} bytes)");
            } catch (Exception exception) {
                Console.WriteLine(value: $"[proof]   (could not copy to {outPath}: {exception.Message})");
            }
        }

        return ok;
    }

    static bool ExpectStatus(Ctx ctx, string needle, string name) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "capture.status");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[capture.status:"), deadlineSeconds: 15.0);

        return Check(name: name, ok: ((line is not null) && line.Contains(value: needle)), detail: (line?.Trim() ?? "(no capture.status echo)"));
    }

    static string Extract(string line, string after, string until) {
        var start = line.IndexOf(value: after, comparisonType: StringComparison.Ordinal);

        if (start < 0) {
            return "";
        }

        start += after.Length;

        var end = line.IndexOf(value: until, startIndex: start, comparisonType: StringComparison.Ordinal);

        return ((end < 0) ? line[start..].Trim() : line[start..end].Trim());
    }

    sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 45.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 45 seconds"));
    }

    static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();

        try {
            ctx.Stdin.Write(value: line);
            ctx.Stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }

    static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }

    static string? FindExe(string projectPath) {
        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");

        if (!Directory.Exists(path: binRelease)) {
            return null;
        }

        return Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
            .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
        }
    }
}

// A minimal reader-side EBML walker (independent of the muxer) for the record proof: doc type + which track types are
// present. Mirrors the mux-check walker's primitives; it only reads what the container assertions need.
sealed class MiniEbml {
    public string DocType = "";
    public bool HasVideoTrack;
    public bool HasAudioTrack;
    public string VideoCodecId = "";

    public static MiniEbml Walk(byte[] data) {
        var walker = new MiniEbml();
        var position = 0;

        while (position < data.Length) {
            var id = ReadId(data: data, position: ref position);
            var size = ReadSize(data: data, position: ref position);
            var contentStart = position;
            var contentEnd = ((size < 0) ? data.Length : (int)(position + size));

            if (id == 0x1A45DFA3) {
                walker.ParseEbmlHeader(data: data, start: contentStart, end: contentEnd);
            } else if (id == 0x18538067) {
                walker.ParseSegment(data: data, start: contentStart, end: contentEnd);
            }

            position = contentEnd;
        }

        return walker;
    }

    void ParseEbmlHeader(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data: data, position: ref position);
            var size = ReadSize(data: data, position: ref position);

            if (id == 0x4282) {
                DocType = System.Text.Encoding.ASCII.GetString(bytes: data, index: position, count: (int)size);
            }

            position += (int)size;
        }
    }

    void ParseSegment(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data: data, position: ref position);
            var size = ReadSize(data: data, position: ref position);
            var contentStart = position;
            var contentEnd = ((size < 0) ? end : (int)(position + size));

            if (id == 0x1654AE6B) {
                ParseTracks(data: data, start: contentStart, end: contentEnd);
            }

            position = contentEnd;
        }
    }

    void ParseTracks(byte[] data, int start, int end) {
        var position = start;

        while (position < end) {
            var id = ReadId(data: data, position: ref position);
            var size = ReadSize(data: data, position: ref position);

            if (id == 0xAE) {
                ParseTrackEntry(data: data, start: position, end: (int)(position + size));
            }

            position += (int)size;
        }
    }

    void ParseTrackEntry(byte[] data, int start, int end) {
        var position = start;
        var type = 0;
        var codecId = "";

        while (position < end) {
            var id = ReadId(data: data, position: ref position);
            var size = ReadSize(data: data, position: ref position);

            if (id == 0x83) {
                type = (int)ReadUInt(data: data, position: position, length: (int)size);
            } else if (id == 0x86) {
                codecId = System.Text.Encoding.ASCII.GetString(bytes: data, index: position, count: (int)size);
            }

            position += (int)size;
        }

        if (type == 1) {
            HasVideoTrack = true;
            VideoCodecId = codecId;
        } else if (type == 2) {
            HasAudioTrack = true;
        }
    }

    static uint ReadId(byte[] data, ref int position) {
        var first = data[position];
        var length = (((first & 0x80) != 0) ? 1 : ((first & 0x40) != 0) ? 2 : ((first & 0x20) != 0) ? 3 : 4);
        var id = 0u;

        for (var index = 0; (index < length); index++) {
            id = ((id << 8) | data[position + index]);
        }

        position += length;

        return id;
    }

    static long ReadSize(byte[] data, ref int position) {
        var first = data[position];
        var length = (((first & 0x80) != 0) ? 1 : ((first & 0x40) != 0) ? 2 : ((first & 0x20) != 0) ? 3 : ((first & 0x10) != 0) ? 4 : ((first & 0x08) != 0) ? 5 : ((first & 0x04) != 0) ? 6 : ((first & 0x02) != 0) ? 7 : 8);
        long value = (first & (0xFF >> length));
        var allOnes = (value == (0xFFL >> length));

        for (var index = 1; (index < length); index++) {
            value = ((value << 8) | data[position + index]);

            if (data[position + index] != 0xFF) {
                allOnes = false;
            }
        }

        position += length;

        return (allOnes ? -1L : value);
    }

    static long ReadUInt(byte[] data, int position, int length) {
        var value = 0L;

        for (var index = 0; (index < length); index++) {
            value = ((value << 8) | data[position + index]);
        }

        return value;
    }
}

// ============================================================================================
// COMPOSED-SHOT KIT — the shared session harness for the composed-frame proofs (ui-floor,
// editor-mode): launch a windowed Puck.World with piped stdio, drive verbs, arm world.screenshot
// captures through the outermost decorator, and decode the engine's own PNGs (8-bit RGBA,
// filter 0, one zlib IDAT — anything else is loudly invalid; this is a proof harness, not an
// image library).
// ============================================================================================
static class ComposedShotKit {
    public sealed record Ctx(Process Process, StreamWriter Stdin, OutputCollector Collector);

    // Build (unless --no-build) and locate the freshest Release exe; null on failure (already reported).
    public static string? BuildAndFindExe(string repoRoot, bool noBuild) {
        var projectPath = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World");

        if (!noBuild) {
            Console.WriteLine(value: "[proof] building Puck.World (Release)...");

            var build = Process.Start(startInfo: new ProcessStartInfo {
                Arguments = $"build \"{projectPath}\" -c Release --nologo -v q",
                FileName = "dotnet",
                UseShellExecute = false,
            })!;

            build.WaitForExit();

            if (build.ExitCode != 0) {
                Console.Error.WriteLine(value: $"[proof] build failed ({build.ExitCode})");

                return null;
            }
        }

        var binRelease = Path.Combine(path1: projectPath, path2: "bin", path3: "Release");
        var exe = (Directory.Exists(path: binRelease)
            ? Directory.EnumerateFiles(path: binRelease, searchOption: SearchOption.AllDirectories, searchPattern: "Puck.World.exe")
                .OrderByDescending(keySelector: File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null);

        if (exe is null) {
            Console.Error.WriteLine(value: "[proof] Puck.World.exe not found under bin/Release — build first");
        }

        return exe;
    }

    // The shared launch shape: piped stdio, repo-root working directory, the standard size/backend/exit options.
    public static Ctx Launch(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds, Stopwatch stopwatch, string[]? extraArgs = null, [CallerMemberName] string session = "") {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        if (backend is not null) {
            psi.ArgumentList.Add(item: "--backend");
            psi.ArgumentList.Add(item: backend);
        }

        foreach (var extra in (extraArgs ?? [])) {
            psi.ArgumentList.Add(item: extra);
        }

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: exitAfterSeconds.ToString(provider: ProofApp.Inv));

        var process = new Process { StartInfo = psi };
        var collector = new OutputCollector(label: session);

        Console.WriteLine(value: $"[proof] launching: {exe} {(backend is null ? "" : $"--backend {backend} ")}--width {width} --height {height}");
        _ = process.Start();
        stopwatch.Start();
        collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
        collector.Start(reader: process.StandardError, stopwatch: stopwatch);

        var stdin = process.StandardInput;

        stdin.AutoFlush = true;

        return new Ctx(Process: process, Stdin: stdin, Collector: collector);
    }

    // Shader compilation and first-device startup can exceed an individual assertion's deadline on a cold machine.
    // player.stop is idempotent at boot and leaves the player at the authored spawn.
    public static bool WaitForConsole(Ctx ctx) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: "player.stop 1");

        var line = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: "[player.stop:"), deadlineSeconds: 30.0);

        return Check(name: "simulation-ready", ok: (line is not null), detail: (line?.Trim() ?? "player.stop did not apply within 30 seconds"));
    }

    public static void Send(Ctx ctx, string line) {
        ctx.Collector.NoteDriven();
        Write(stdin: ctx.Stdin, line: line);
    }

    // The raw pipe write. Settling uses it directly: reading the counter is not itself a driven line, so it never
    // creates the obligation it discharges.
    public static void Write(StreamWriter stdin, string line) {
        try {
            stdin.Write(value: line);
            stdin.Write(value: '\n');
        }
        catch (IOException) {
        }
        catch (ObjectDisposedException) {
        }
    }

    // THE HOUSE RULE, one implementation. Settle the wire's own refused-line counter against the refusals the round
    // just ahead DELIBERATELY provoked, then zero it so the next round's expectation stands alone. A round that meant
    // to refuse nothing passes `expected: 0`; the terminal settle of every session is that zero, and it MEANS
    // something only because each deliberate round cleared its own count first.
    //
    // The count is read from the app (`wire.errors`), never re-derived from the transcript: the wire counts an unknown
    // verb, a parse failure, a handler's error result AND a deferred rejection raised a tick after the line was
    // accepted, and only the first of those four leaves the registry's "[wire.reject:" sigil behind.
    public static bool SettleWireErrors(Ctx ctx, string name, int expected) {
        return SettleWireErrors(stdin: ctx.Stdin, collector: ctx.Collector, name: name, expected: expected);
    }
    public static bool SettleWireErrors(StreamWriter stdin, OutputCollector collector, string name, int expected) {
        string? last = null;

        // A deferred refusal lands a tick after its line was accepted, so the count RISES into the expectation; it
        // never falls. An overshoot is therefore final and fails immediately rather than burning the retry budget.
        for (var attempt = 0; (attempt < 20); attempt++) {
            var mark = collector.Count;

            Write(stdin: stdin, line: "wire.errors");
            last = Await(collector: collector, mark: mark, predicate: l => WireErrorsEcho.IsMatch(input: l), deadlineSeconds: 5.0);

            var seen = ((last is null) ? -1 : int.Parse(s: WireErrorsEcho.Match(input: last).Groups[1].ValueSpan, provider: ProofApp.Inv));

            if (seen == expected) {
                mark = collector.Count;

                Write(stdin: stdin, line: "wire.errors reset");
                _ = Await(collector: collector, mark: mark, predicate: l => WireErrorsEcho.IsMatch(input: l), deadlineSeconds: 5.0);
                collector.NoteSettled();

                return Check(name: name, ok: true, detail: $"{expected} deliberate refusal(s) counted, counter cleared");
            }

            if (seen > expected) {
                break;
            }

            Thread.Sleep(millisecondsTimeout: 200);
        }

        return Check(name: name, ok: false, detail: $"{last?.Trim() ?? "(no '[wire.errors: ...]' echo)"} — want {expected} rejected");
    }

    static readonly Regex WireErrorsEcho = new(pattern: @"\[wire\.errors: (\d+) rejected\]", options: RegexOptions.Compiled);

    public static string? Await(OutputCollector collector, int mark, Func<string, bool> predicate, double deadlineSeconds) {
        var deadline = DateTime.UtcNow.AddSeconds(value: deadlineSeconds);

        while (true) {
            var snapshot = collector.Snapshot();

            for (var i = mark; (i < snapshot.Length); i++) {
                if (predicate(arg: snapshot[i])) {
                    return snapshot[i];
                }
            }

            if (DateTime.UtcNow >= deadline) {
                return null;
            }

            Thread.Sleep(millisecondsTimeout: 100);
        }
    }

    // Send a line, await its echo, and record the check under one name — the every-verb round-trip shape.
    public static bool SendAwait(Ctx ctx, string line, string expect, string name, double deadlineSeconds = 15.0) {
        var mark = ctx.Collector.Count;

        Send(ctx: ctx, line: line);

        var seen = Await(collector: ctx.Collector, mark: mark, predicate: candidate => candidate.Contains(value: expect), deadlineSeconds: deadlineSeconds);

        return Check(name: name, ok: (seen is not null), detail: (seen?.Trim() ?? $"(no '{expect}' echo)"));
    }

    public static bool Check(string name, bool ok, string detail) {
        Console.WriteLine(value: $"[proof]   {(ok ? "PASS" : "FAIL")} {name}: {detail}");

        return ok;
    }

    // Arms world.screenshot and waits for the unified overlay's capture echo (the readback writes the file BEFORE
    // echoing, so the echo implies the PNG is on disk).
    public static bool Screenshot(Ctx ctx, string name, string path) {
        var mark = ctx.Collector.Count;
        var fileName = Path.GetFileName(path: path);

        Send(ctx: ctx, line: $"world.screenshot {path}");

        var captured = Await(collector: ctx.Collector, mark: mark, predicate: l => (l.Contains(value: "[capture] unified overlay ->") && l.Contains(value: fileName)), deadlineSeconds: 30.0);

        return Check(name: name, ok: ((captured is not null) && File.Exists(path: path)), detail: (captured?.Trim() ?? "(no unified-overlay capture echo)"));
    }

    // Decodes the engine's own PngEncoder output: 8-bit RGBA, color type 6, every scanline filter 0, one zlib
    // deflate stream across the IDAT chunks.
    public static (int Width, int Height, byte[] Rgba) DecodePng(string path) {
        var bytes = File.ReadAllBytes(path: path);
        var offset = 8;
        var width = 0;
        var height = 0;

        using var idat = new MemoryStream();

        while (offset < bytes.Length) {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(source: bytes.AsSpan(start: offset, length: 4));
            var type = Encoding.ASCII.GetString(bytes: bytes, index: (offset + 4), count: 4);
            var data = bytes.AsSpan(start: (offset + 8), length: length);

            if (type == "IHDR") {
                width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(source: data[..4]);
                height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(source: data.Slice(start: 4, length: 4));

                if ((data[8] != 8) || (data[9] != 6)) {
                    throw new InvalidDataException($"{path}: expected 8-bit RGBA (bit depth {data[8]}, color type {data[9]})");
                }
            } else if (type == "IDAT") {
                idat.Write(buffer: data);
            }

            offset += (12 + length);

            if (type == "IEND") {
                break;
            }
        }

        idat.Position = 0;

        var rowBytes = (width * 4);
        var raw = new byte[(height * (1 + rowBytes))];

        using (var inflate = new System.IO.Compression.ZLibStream(stream: idat, mode: System.IO.Compression.CompressionMode.Decompress)) {
            inflate.ReadExactly(buffer: raw);
        }

        var rgba = new byte[(height * rowBytes)];

        for (var row = 0; (row < height); row++) {
            if (raw[(row * (1 + rowBytes))] != 0) {
                throw new InvalidDataException($"{path}: unexpected scanline filter {raw[(row * (1 + rowBytes))]} on row {row}");
            }

            Buffer.BlockCopy(src: raw, srcOffset: ((row * (1 + rowBytes)) + 1), dst: rgba, dstOffset: (row * rowBytes), count: rowBytes);
        }

        return (width, height, rgba);
    }

    // No loud GPU/runtime faults anywhere in the session (both streams).
    public static bool FaultSweep(Ctx ctx) {
        var faults = 0;

        foreach (var line in ctx.Collector.Snapshot()) {
            if (line.Contains(value: "Unhandled exception") || line.Contains(value: "Fatal error.") || line.Contains(value: "VUID-")) {
                faults++;
                Console.WriteLine(value: $"[proof]   fault line: {line.Trim()}");
            }
        }

        return Check(name: "no-gpu-or-runtime-faults", ok: (faults == 0), detail: ((faults == 0) ? "clean" : $"{faults} fault line(s)"));
    }

    public static void TryDelete(string path) {
        try {
            File.Delete(path: path);
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }
    }

    public static void KillQuietly(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch {
            // best-effort — the child must never outlive us.
        }
    }
}

// ============================================================================================
// UI-FLOOR — the unified-overlay proof: the ONE screen-space overlay decorator (console
// mirror + per-seat binding bars + mutation toasts) renders on BOTH backends and the
// world.screenshot verb captures the final COMPOSED frame through the outermost decorator.
// Three composed captures per backend session: overlay (console on), control (console off),
// toast (after a deliberately invalid mutation). The overlay's presence is asserted in PIXELS,
// never by file existence: the console panel's 0.90-alpha scrim must darken its stage region's
// mean luminance versus the control (robust against the moving world beneath), and the
// rejection toast must plant a danger-red pixel population in the toast strip that the control
// lacks. Session/PNG machinery lives in ComposedShotKit.
// ============================================================================================
static class UiFloorProof {
    public static int RunUiFloor(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 120, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        // D3D12 FIRST (World's default backend), then Vulkan.
        Console.WriteLine(value: "[proof] === ui-floor (a): Direct3D 12 (the default backend) ===");
        var directXPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === ui-floor (b): Vulkan ===");
        var vulkanPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        var passed = (directXPassed && vulkanPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] ui-floor proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // One scripted session on one backend: boot → overlay shot → console off → control shot → rejected
    // mutation → toast shot → pixel assertions → loud-fault sweep.
    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var tag = (backend ?? "directx");
        var overlayPath = Path.Combine(Path.GetTempPath(), $"puck-ui-floor-{pid}-{tag}-overlay.png");
        var controlPath = Path.Combine(Path.GetTempPath(), $"puck-ui-floor-{pid}-{tag}-control.png");
        var toastPath = Path.Combine(Path.GetTempPath(), $"puck-ui-floor-{pid}-{tag}-toast.png");
        var gizmoPath = Path.Combine(Path.GetTempPath(), $"puck-ui-floor-{pid}-{tag}-gizmo.png");
        var gizmoControlPath = Path.Combine(Path.GetTempPath(), $"puck-ui-floor-{pid}-{tag}-gizmo-control.png");
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // (1) The composed frame with the console panel visible (the boot default).
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "overlay-shot", path: overlayPath);

            // (2) The no-console control: the binding bars stay, but the assertion region is the console's stage
            // corner, which they never enter.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "control-shot", path: controlPath);

            // (3) The rejection toast: removing the defaultSeatKit fails validation loudly server-side AND must
            // surface on screen. The screenshot rides the stdin barrier behind the Simulation-routed mutation.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.kit.remove runner", expect: "[world.mutation rejected:", name: "mutation-rejected-loudly");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "toast-shot", path: toastPath);

            // (4) The pixel assertions — decode the three composed frames.
            var overlay = ComposedShotKit.DecodePng(path: overlayPath);
            var control = ComposedShotKit.DecodePng(path: controlPath);
            var toast = ComposedShotKit.DecodePng(path: toastPath);

            // Console presence: the panel's 0.90 dark scrim must pull the stage region's mean luminance well below
            // the control's (the world beneath moves between shots; a scrim-sized drop dwarfs that noise).
            var regionX = 48;
            var regionY = 48;
            var regionW = (width - 112);
            var regionH = ((int)(height * 0.45) - 48);
            var overlayLuma = MeanLuminance(image: overlay, x: regionX, y: regionY, w: regionW, h: regionH);
            var controlLuma = MeanLuminance(image: control, x: regionX, y: regionY, w: regionW, h: regionH);

            passed &= ComposedShotKit.Check(
                name: "console-panel-darkens-stage",
                ok: ((controlLuma - overlayLuma) > 15.0),
                detail: $"mean luminance overlay {overlayLuma.ToString(format: "F1", provider: ProofApp.Inv)} vs control {controlLuma.ToString(format: "F1", provider: ProofApp.Inv)} (want a > 15 scrim drop)"
            );

            // Toast presence: a danger-red population (the state rail + Tier-1 ring in #F2565B) in the mid-right
            // toast strip that the control shot lacks.
            var stripX = (int)(width * 0.55);
            var stripY = ((height / 2) - 16);
            var stripW = ((width - 40) - stripX);
            var stripH = 32;
            var toastRed = CountDangerRed(image: toast, x: stripX, y: stripY, w: stripW, h: stripH);
            var controlRed = CountDangerRed(image: control, x: stripX, y: stripY, w: stripW, h: stripH);

            // Both backends measure ~190 danger-red pixels in the strip (the 2px rail + the ring arcs it clips)
            // against a clean 0 in the control; 120/+100 keeps a decisive margin without riding exact ring geometry.
            passed &= ComposedShotKit.Check(
                name: "rejection-surfaces-as-toast",
                ok: ((toastRed > (controlRed + 100)) && (toastRed > 120)),
                detail: $"danger-red pixels in the toast strip: toast {toastRed} vs control {controlRed}"
            );

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "toast-round-refused-only-the-default-kit", expected: 1);

            // (4c) THE SPEAKER GIZMO: a bed speaker dead ahead of the seat camera, SELECTED in editor mode —
            // its accent-tier chip (accent bloom ring + halo) and accent radius ring put an accent-orange population
            // in the central stage that leaving editor mode removes (gizmos are editor-mode-only). Screenshots ride
            // the stdin barrier behind the Simulation-routed acts.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: """world.speaker.set {"$type":"bed","name":"gizmo-bed","center":[0,1.2,-5],"radius":2.5,"feed":{"source":{"$type":"none"},"channel":"mix","gain":1}}""", expect: "[world.mutation: UpsertSpeaker 'gizmo-bed' applied]", name: "gizmo-speaker-applies");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter 1", expect: "[editor.enter: seat 1 editing", name: "gizmo-editor-enters");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select speakers gizmo-bed", expect: "speakers 'gizmo-bed'", name: "gizmo-selects");
            // Let the upsert's change-shimmer pulse decay (0.9 s): the HELD tier would otherwise mask the ACCENT
            // tier this round asserts (held wins over accent by the chip-state contract).
            Thread.Sleep(millisecondsTimeout: 1500);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "gizmo-shot", path: gizmoPath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit 1", expect: "[editor.exit: seat 1", name: "gizmo-editor-exits");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "gizmo-control-shot", path: gizmoControlPath);

            var gizmo = ComposedShotKit.DecodePng(path: gizmoPath);
            var gizmoControl = ComposedShotKit.DecodePng(path: gizmoControlPath);
            // The central stage (clear of the console corner, the mid-right toast strip, and the bottom binding bar).
            var stageX = (int)(width * 0.32);
            var stageY = (int)(height * 0.25);
            var stageW = ((int)(width * 0.52) - stageX);
            var stageH = ((int)(height * 0.68) - stageY);
            var gizmoAccent = CountAccentOrange(image: gizmo, x: stageX, y: stageY, w: stageW, h: stageH);
            var controlAccent = CountAccentOrange(image: gizmoControl, x: stageX, y: stageY, w: stageW, h: stageH);

            passed &= ComposedShotKit.Check(
                name: "gizmo-lights-editor-mode-only",
                ok: ((gizmoAccent > (controlAccent + 25)) && (gizmoAccent > 40)),
                detail: $"accent-orange pixels in the stage: editor {gizmoAccent} vs exited {controlAccent}"
            );

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "gizmo-round-refused-nothing", expected: 0);

            // (5) No loud GPU/runtime faults anywhere in the session (both streams).
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: overlayPath);
            ComposedShotKit.TryDelete(path: controlPath);
            ComposedShotKit.TryDelete(path: toastPath);
            ComposedShotKit.TryDelete(path: gizmoPath);
            ComposedShotKit.TryDelete(path: gizmoControlPath);
        }

        return passed;
    }

    static double MeanLuminance((int Width, int Height, byte[] Rgba) image, int x, int y, int w, int h) {
        var sum = 0L;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * image.Width) + col) * 4);

                sum += (image.Rgba[i] + image.Rgba[(i + 1)] + image.Rgba[(i + 2)]);
            }
        }

        return ((double)sum / ((long)w * h * 3));
    }

    // Danger-hue population: pixels whose red channel clearly dominates BOTH others — the toast's #F2565B rail/ring
    // family reads true here while grass (green-dominant), scrims (near-neutral), and the purple avatars (blue-high)
    // do not.
    static int CountDangerRed((int Width, int Height, byte[] Rgba) image, int x, int y, int w, int h) {
        var count = 0;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * image.Width) + col) * 4);
                int r = image.Rgba[i];
                int g = image.Rgba[(i + 1)];
                int b = image.Rgba[(i + 2)];

                if ((r > (g + 40)) && (r > (b + 40))) {
                    count++;
                }
            }
        }

        return count;
    }

    // Accent-hue population: the token accent #FF6A2B (electric amber-orange) — red well over green AND green over
    // blue, which the danger family (g ≈ b), grass (green-dominant), gray stone, and the purple/magenta avatars all
    // fail. Thresholds calibrated against the chip's 0.55-alpha bloom ring blended over the world (a half-alpha
    // accent stays decisively r>g+50/g>b+15; the 0.35-alpha radius ring only reads over darker ground).
    static int CountAccentOrange((int Width, int Height, byte[] Rgba) image, int x, int y, int w, int h) {
        var count = 0;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * image.Width) + col) * 4);
                int r = image.Rgba[i];
                int g = image.Rgba[(i + 1)];
                int b = image.Rgba[(i + 2)];

                if ((r > (g + 50)) && (g > (b + 15))) {
                    count++;
                }
            }
        }

        return count;
    }
}

// ============================================================================================
// EDITOR-MODE — a seat enters editor mode mid-session over stdin, its ACTIVE
// binding GROUP flips play→editor (asserted through editor.status, which reads the SAME
// PageView the bar renders — group= + page=), the group's FIVE ordered trigger chords each
// turn to a distinct page over synthesized pad signals (player.signal) — including the two
// two-trigger orders landing apart — the wire-reachable uniqueness rules reject loudly, a
// session-rebind chord row binds and echoes, the seat's intent diverts to the honest idle
// (player.control reads
// idle while editing, the prior source after), the editor camera seeds at the chase framing and
// flies on command (asserted in PIXELS: the console panel is hidden and a central world region
// is mean-abs-diffed between shots — the flown shot must differ decisively while the seeded and
// restored shots hug the pre-enter chase shot, so neither mode edge pops the camera), and the
// diversion unwinds honestly (the avatar drives after exit, two idle where samples match
// exactly, and a scripted tape STILL drives the avatar while editing — script outranks idle).
// Runs on BOTH backends like ui-floor.
// ============================================================================================
static class EditorModeProof {
    public static int RunEditorMode(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 150, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        Console.WriteLine(value: "[proof] === editor-mode (a): Direct3D 12 (the default backend) ===");
        var directXPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === editor-mode (b): Vulkan ===");
        var vulkanPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        var passed = (directXPassed && vulkanPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] editor-mode proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var tag = (backend ?? "directx");
        var prePath = ShotPath(pid: pid, tag: tag, name: "pre");
        var seedPath = ShotPath(pid: pid, tag: tag, name: "seed");
        var flyPath = ShotPath(pid: pid, tag: tag, name: "fly");
        var postPath = ShotPath(pid: pid, tag: tag, name: "post");
        var duoPath = ShotPath(pid: pid, tag: tag, name: "duo");
        var railPath = ShotPath(pid: pid, tag: tag, name: "rail");
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // The console panel would repaint with every verb echo between shots — hide it so the pixel region
            // reads the WORLD (the binding bars sit in the excluded bottom strip; no toasts fire in this script).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

            // Pin the roster to seat 1: connected pads on a dev machine auto-seat extra players, and any second seat
            // arms the sole-editor LAYOUT policy — which would swamp the camera pixel work below. The policy gets its
            // own positive block later. player.leave is a friendly no-op echo for an unjoined seat.
            for (var seat = 2; (seat <= 4); seat++) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-roster-leave-{seat}");
            }

            // (1) The mode round trip, narrated: not editing (group=play — the resting group) → enter → the active
            // GROUP flips to editor and its resting page answers (editor.status reads the same PageView the bar
            // renders — the bar flip's assertable truth) → the seat's intent source reads idle (the diversion) —
            // then the camera work — then exit → restored (group=play again).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "[editor.status: seat 1 not editing group=play page=base", name: "status-before-enter");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "pre-shot", path: prePath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "enter-echo");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=editor page=editor 'Editor'", name: "bar-flips-to-editor-group");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.control 1", expect: "[player.control: p1 is idle]", name: "intent-diverts-to-idle");

            // (2) The camera: the first editor frame seeds at the chase framing (no pose pop), then the console twin
            // of stick flight relocates it decisively.
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "seed-shot", path: seedPath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 12 8 -18 140 -15", expect: "[editor.cam.pose: seat 1", name: "cam-pose-echo");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "fly-shot", path: flyPath);

            // (3) Exit restores: the prior source returns, the group flips back, the chase rig re-anchors (pixels below).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit", expect: "[editor.exit: seat 1", name: "exit-echo");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "[editor.status: seat 1 not editing group=play page=base", name: "status-after-exit");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.control 1", expect: "[player.control: p1 is live]", name: "intent-restores-to-live");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "post-shot", path: postPath);

            // (4) The pixel assertions over a central world band (below any console chrome, above the toast strip
            // and the bottom binding bars — which DO legitimately change across the mode flip).
            var pre = ComposedShotKit.DecodePng(path: prePath);
            var seed = ComposedShotKit.DecodePng(path: seedPath);
            var fly = ComposedShotKit.DecodePng(path: flyPath);
            var post = ComposedShotKit.DecodePng(path: postPath);
            var regionX = (int)(width * 0.10);
            var regionY = (int)(height * 0.06);
            var regionW = (int)(width * 0.80);
            var regionH = (int)(height * 0.38);
            var flyDiff = MeanAbsDiff(a: fly, b: seed, x: regionX, y: regionY, w: regionW, h: regionH);
            var seedDiff = MeanAbsDiff(a: seed, b: pre, x: regionX, y: regionY, w: regionW, h: regionH);
            var restoreDiff = MeanAbsDiff(a: post, b: pre, x: regionX, y: regionY, w: regionW, h: regionH);

            // The flown shot must differ decisively (a relocated camera repaints the band); the seeded/restored
            // shots must hug the pre-enter chase framing (relative guards, robust against ambient world motion).
            passed &= ComposedShotKit.Check(
                name: "camera-flies-on-pose",
                ok: (flyDiff > 8.0),
                detail: $"fly-vs-seed mean abs diff {flyDiff.ToString(format: "F2", provider: ProofApp.Inv)} (want > 8)"
            );
            passed &= ComposedShotKit.Check(
                name: "enter-seeds-at-chase",
                ok: (seedDiff < (flyDiff * 0.5)),
                detail: $"seed-vs-pre {seedDiff.ToString(format: "F2", provider: ProofApp.Inv)} vs fly {flyDiff.ToString(format: "F2", provider: ProofApp.Inv)} (want < half)"
            );
            passed &= ComposedShotKit.Check(
                name: "exit-restores-chase",
                ok: (restoreDiff < (flyDiff * 0.5)),
                detail: $"post-vs-pre {restoreDiff.ToString(format: "F2", provider: ProofApp.Inv)} vs fly {flyDiff.ToString(format: "F2", provider: ProofApp.Inv)} (want < half)"
            );

            // (5) The avatar drives again after exit — the diversion unwound.
            var before = AwaitWhere(ctx: ctx, name: "where-before-run");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.run 1 0 0 0.8", expect: "[player.run:", name: "run-after-exit");
            Thread.Sleep(millisecondsTimeout: 1400);

            var after = AwaitWhere(ctx: ctx, name: "where-after-run");

            passed &= ComposedShotKit.Check(
                name: "avatar-drives-after-exit",
                ok: ((before is { } b1) && (after is { } a1) && (Planar(a: b1, b: a1) > 0.5)),
                detail: DeltaDetail(before: before, after: after)
            );

            // (6) Nothing held leaked across the mode flip: two idle samples half a second apart match exactly.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.stop 1", expect: "[player.stop:", name: "stop-after-run");
            Thread.Sleep(millisecondsTimeout: 400);

            var restA = AwaitWhere(ctx: ctx, name: "where-at-rest-a");

            Thread.Sleep(millisecondsTimeout: 600);

            var restB = AwaitWhere(ctx: ctx, name: "where-at-rest-b");

            passed &= ComposedShotKit.Check(
                name: "no-held-leak-after-exit",
                ok: ((restA is { } r1) && (restB is { } r2) && (Planar(a: r1, b: r2) < 0.005)),
                detail: DeltaDetail(before: restA, after: restB)
            );

            // (6b) THE FIVE CHORD PAGES, end to end from DATA: player.signal synthesizes the trigger sweeps on
            // seat 1's lane and the seat's ACTIVE page turns with the held chord — nothing held = the resting
            // page, LT = page 1, RT = page 2, LT-then-RT = page 3, RT-then-LT = page 4 (press order is
            // load-bearing, so the two two-trigger orders land on DIFFERENT pages). Asserted in the editor group,
            // whose five pages are all populated. Signals fold on the next 32 Hz tick, so each status read follows
            // a short settle.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "chord-block-enter");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.leftTrigger 0.9", expect: "[player.signal: gamepad.leftTrigger 0.9]", name: "chord-lt-signal");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=editor page=editor-camera", name: "held-lt-selects-camera-page");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.rightTrigger 0.9", expect: "[player.signal: gamepad.rightTrigger 0.9]", name: "chord-rt-signal");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=editor page=editor-place", name: "lt-then-rt-selects-place-page");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.leftTrigger 0", expect: "[player.signal: gamepad.leftTrigger 0]", name: "chord-lt-release");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=editor page=editor-select", name: "held-rt-selects-select-page");
            // The REVERSE squeeze: RT is still held, so pressing LT now completes [rt, lt] — page 4, not page 3.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.leftTrigger 0.9", expect: "[player.signal: gamepad.leftTrigger 0.9]", name: "chord-lt-resqueeze");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=editor page=editor-reverse", name: "rt-then-lt-selects-reverse-page");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.leftTrigger 0", expect: "[player.signal: gamepad.leftTrigger 0]", name: "chord-lt-release-2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.rightTrigger 0", expect: "[player.signal: gamepad.rightTrigger 0]", name: "chord-rt-release");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=editor page=editor 'Editor'", name: "releases-walk-to-editor-resting");
            // Fresh presses resolve in the NEW group: South on the editor resting page is the camera toggle.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.buttonSouth press", expect: "[player.signal: gamepad.buttonSouth press]", name: "south-press-signal");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.signal gamepad.buttonSouth release", expect: "[player.signal: gamepad.buttonSouth release]", name: "south-release-signal");
            Thread.Sleep(millisecondsTimeout: 400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "editing orbit", name: "fresh-press-uses-editor-group");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.fly", expect: "[editor.fly: seat 1 camera fly]", name: "restore-fly-mode");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit", expect: "[editor.exit: seat 1", name: "chord-block-exit");

            // (6c) The uniqueness rules are LOUD at the wire: an overlay whose new group has no resting page is
            // rejected by the composed compile inside the validator (surfacing as the mutation rejection), and a
            // chord rebind on an undeclared modifier id rejects at player.bind. (One-meaning-per-(group, chord)
            // duplication cannot be EXPRESSED through the composer — rows merge by that very key — so its loud
            // rejection is engine-gated in Puck.Post's binding-page stage.)
            passed &= ComposedShotKit.SendAwait(
                ctx: ctx,
                line: "world.bindings.set {\"id\":\"orphan\",\"document\":{\"version\":\"puck.bindings.v1\",\"modifiers\":[],\"chords\":[{\"group\":\"solo\",\"chord\":[\"lt\"],\"command\":{\"command\":\"editor.enter\"}}]}}",
                expect: "no resting (empty-chord) page",
                name: "resting-page-rule-rejects-loudly"
            );
            passed &= ComposedShotKit.SendAwait(
                ctx: ctx,
                line: "player.bind 1 chord:zz+rt editor.enter",
                expect: "does not compile",
                name: "undeclared-modifier-chord-rejects-loudly"
            );
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "uniqueness-round-refused-only-its-two", expected: 2);

            // The positive twin: a session-rebind chord row declares a meaning through the same grammar and echoes.
            passed &= ComposedShotKit.SendAwait(
                ctx: ctx,
                line: "player.bind 1 chord:rt+lt editor.status",
                expect: "[player.bind: seat 1 'chord:rt+lt' → 'editor.status'",
                name: "session-chord-row-binds"
            );
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.bindings", expect: "chord play:[rt+lt]→editor.status", name: "session-chord-row-echoes");

            // (7) The sole-editor LAYOUT policy, positively: with a second seat joined the split is side-by-side;
            // seat 1 entering the editor takes the full-height left 70% workbench and seat 2 moves into the right
            // rail — so the band the seam crosses (x 52..68%) repaints decisively between the two shots.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.join 2", expect: "[player.join: player 2 joined pending", name: "join-second-seat");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "duo-shot", path: duoPath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "enter-for-layout");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "rail-shot", path: railPath);

            var duo = ComposedShotKit.DecodePng(path: duoPath);
            var rail = ComposedShotKit.DecodePng(path: railPath);
            var seamX = (int)(width * 0.52);
            var seamY = (int)(height * 0.10);
            var seamW = (int)(width * 0.16);
            var seamH = (int)(height * 0.35);
            var seamDiff = MeanAbsDiff(a: rail, b: duo, x: seamX, y: seamY, w: seamW, h: seamH);

            passed &= ComposedShotKit.Check(
                name: "sole-editor-takes-workbench",
                ok: (seamDiff > 8.0),
                detail: $"seam-band mean abs diff {seamDiff.ToString(format: "F2", provider: ProofApp.Inv)} (want > 8 — the 50% split boundary moved to 70%)"
            );
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit", expect: "[editor.exit: seat 1", name: "exit-for-layout");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.leave 2", expect: "[player.leave: player 2 left", name: "leave-second-seat");

            // (8) Re-enter and prove the idle contract's honest edge: a scripted TAPE still drives the avatar while
            // its seat edits (script outranks idle — the player.control contract, unchanged by the mode), and the
            // second enter/exit cycle works.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "re-enter-echo");

            var tapeBefore = AwaitWhere(ctx: ctx, name: "where-before-tape");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.run 1 0 0 0.6", expect: "[player.run:", name: "tape-while-editing");
            Thread.Sleep(millisecondsTimeout: 1200);

            var tapeAfter = AwaitWhere(ctx: ctx, name: "where-after-tape");

            passed &= ComposedShotKit.Check(
                name: "tape-still-drives-in-editor",
                ok: ((tapeBefore is { } t1) && (tapeAfter is { } t2) && (Planar(a: t1, b: t2) > 0.3)),
                detail: DeltaDetail(before: tapeBefore, after: tapeAfter)
            );
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit", expect: "[editor.exit: seat 1", name: "re-exit-echo");

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "editor-mode-refused-nothing-else", expected: 0);

            // (9) No loud GPU/runtime faults anywhere in the session (both streams).
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: prePath);
            ComposedShotKit.TryDelete(path: seedPath);
            ComposedShotKit.TryDelete(path: flyPath);
            ComposedShotKit.TryDelete(path: postPath);
            ComposedShotKit.TryDelete(path: duoPath);
            ComposedShotKit.TryDelete(path: railPath);
        }

        return passed;
    }

    static string ShotPath(int pid, string tag, string name) {
        return Path.Combine(Path.GetTempPath(), $"puck-editor-mode-{pid}-{tag}-{name}.png");
    }

    // Sends player.where and parses player 1's echoed pose through the shared regex; null (and a FAIL line) when the
    // echo never lands.
    static (double X, double Z)? AwaitWhere(ComposedShotKit.Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "player.where 1");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => ProofApp.WhereEcho.IsMatch(input: candidate), deadlineSeconds: 15.0);

        if (line is null) {
            _ = ComposedShotKit.Check(name: name, ok: false, detail: "(no player.where echo)");

            return null;
        }

        var match = ProofApp.WhereEcho.Match(input: line);

        return (
            X: double.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv),
            Z: double.Parse(s: match.Groups[4].Value, provider: ProofApp.Inv)
        );
    }

    static double Planar((double X, double Z) a, (double X, double Z) b) {
        var dx = (a.X - b.X);
        var dz = (a.Z - b.Z);

        return Math.Sqrt(d: ((dx * dx) + (dz * dz)));
    }

    static string DeltaDetail((double X, double Z)? before, (double X, double Z)? after) {
        if ((before is not { } b) || (after is not { } a)) {
            return "(a player.where sample is missing)";
        }

        return $"({b.X.ToString(format: "F2", provider: ProofApp.Inv)}, {b.Z.ToString(format: "F2", provider: ProofApp.Inv)}) -> ({a.X.ToString(format: "F2", provider: ProofApp.Inv)}, {a.Z.ToString(format: "F2", provider: ProofApp.Inv)}), planar delta {Planar(a: b, b: a).ToString(format: "F3", provider: ProofApp.Inv)}";
    }

    // Mean absolute per-channel difference over a region of two same-sized frames — the camera-motion witness.
    static double MeanAbsDiff((int Width, int Height, byte[] Rgba) a, (int Width, int Height, byte[] Rgba) b, int x, int y, int w, int h) {
        var sum = 0L;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * a.Width) + col) * 4);

                sum += Math.Abs(value: (a.Rgba[i] - b.Rgba[i]));
                sum += Math.Abs(value: (a.Rgba[(i + 1)] - b.Rgba[(i + 1)]));
                sum += Math.Abs(value: (a.Rgba[(i + 2)] - b.Rgba[(i + 2)]));
            }
        }

        return ((double)sum / ((long)w * h * 3));
    }
}

// ============================================================================================
// EDITOR-EDIT — the selection/manipulation proof (see the header's subcommand block for the
// full assertion list). The wire-coalescing headline rides world.status's dirty counter: motion
// inside a grab must not move it, release must move it by EXACTLY one. The world is pinned
// static (roster to seat 1, census to 0) so the select/deselect pixel pair reads the amber
// selection tint, not crowd motion. Runs on BOTH backends like editor-mode.
// ============================================================================================
static class EditorEditProof {
    static readonly Regex DirtyEcho = new(pattern: @"dirty (\d+) ", options: RegexOptions.Compiled);

    public static int RunEditorEdit(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 240, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        Console.WriteLine(value: "[proof] === editor-edit (a): Direct3D 12 (the default backend) ===");
        var directXPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === editor-edit (b): Vulkan ===");
        var vulkanPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        // The seat-clip proof runs NARROW on purpose: 640x480 quad-split gives each seat 320px, well under the
        // HUD's worst-case line width, so an unclipped panel would visibly cross the seam.
        Console.WriteLine();
        Console.WriteLine(value: "[proof] === editor-edit (c): four-seat HUD viewport clipping, Direct3D 12 (640x480) ===");
        var clipDirectXPassed = RunHudClipSession(exe: exe, repoRoot: repoRoot, backend: null, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === editor-edit (d): four-seat HUD viewport clipping, Vulkan (640x480) ===");
        var clipVulkanPassed = RunHudClipSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", exitAfterSeconds: exitAfterSeconds);

        var passed = (((directXPassed && vulkanPassed) && clipDirectXPassed) && clipVulkanPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] editor-edit proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // The seat-clip pixel proof: 4 seats in the 2x2 quad at 640x480 (320px per seat), seats 1 and 3 editing (TWO editors
    // = the standard ladder, no sole-editor workbench). Seat 1's HUD panel would be ~480px wide unclipped; the clip
    // contract must CUT it at the x=320 seam, so the band just right of the seam inside seat 2's region stays at the
    // control image while the band left of the seam repaints decisively.
    static bool RunHudClipSession(string exe, string repoRoot, string? backend, int exitAfterSeconds) {
        const int width = 640;
        const int height = 480;
        var pid = Environment.ProcessId;
        var tag = ((backend ?? "directx") + "-clip");
        var controlAPath = ShotPath(pid: pid, tag: tag, name: "control-a");
        var controlBPath = ShotPath(pid: pid, tag: tag, name: "control-b");
        var hudPath = ShotPath(pid: pid, tag: tag, name: "hud");
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Pin the stage: console panel off, exactly four console-joined seats, zero census (static world).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "clip-console-off");

            for (var seat = 2; (seat <= 4); seat++) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"clip-pin-leave-{seat}");
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0", expect: "[world.population:", name: "clip-census-zero");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.join cobalt 2", expect: "[player.join:", name: "clip-join-2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.join moss 3", expect: "[player.join:", name: "clip-join-3");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.join violet 4", expect: "[player.join:", name: "clip-join-4");

            // Controls first (no HUD): a pair bounds the static noise floor.
            Thread.Sleep(millisecondsTimeout: 700);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "clip-control-a", path: controlAPath);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "clip-control-b", path: controlBPath);

            // Two editors (seats 1 and 3) with live SPAWN selections: spawns render no geometry and take no
            // selection tint, so the ONLY pixel change versus the controls is the HUD surface itself — and the
            // spawn selection line is wide enough that an unclipped seat-1 panel would cross the x=320 seam.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter 1", expect: "[editor.enter: seat 1 editing", name: "clip-enter-1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter 3", expect: "[editor.enter: seat 3 editing", name: "clip-enter-3");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select spawns seat-2 1", expect: "[editor.select: seat 1 spawns 'seat-2'", name: "clip-select-1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select spawns seat-3 3", expect: "[editor.select: seat 3 spawns 'seat-3'", name: "clip-select-3");
            Thread.Sleep(millisecondsTimeout: 700);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "clip-hud-shot", path: hudPath);

            var controlA = ComposedShotKit.DecodePng(path: controlAPath);
            var controlB = ComposedShotKit.DecodePng(path: controlBPath);
            var hud = ComposedShotKit.DecodePng(path: hudPath);
            // The HUD band: y spans the panel rows under seat 1's top gutter. Left band = inside seat 1 where the
            // panel must paint; right band = just past the x=320 seam inside seat 2, where an unclipped panel bleeds.
            var bandY = 20;
            var bandH = 120;
            var leftX = 40;
            var leftW = 270;
            // The bleed zone: an unclipped ~380px panel would paint x 320..~390 in seat 2's region.
            var rightX = 322;
            var rightW = 56;
            var noiseRight = MeanAbsDiff(a: controlB, b: controlA, x: rightX, y: bandY, w: rightW, h: bandH);
            var hudLeft = MeanAbsDiff(a: hud, b: controlA, x: leftX, y: bandY, w: leftW, h: bandH);
            var hudRight = MeanAbsDiff(a: hud, b: controlA, x: rightX, y: bandY, w: rightW, h: bandH);

            passed &= ComposedShotKit.Check(
                name: "hud-renders-inside-own-seat",
                ok: (hudLeft > 2.0),
                detail: $"left-of-seam band diff {hudLeft.ToString(format: "F2", provider: ProofApp.Inv)} (want > 2 — the seat-1 HUD panel is visible)"
            );
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "hud-clip-round-refused-nothing", expected: 0);
            passed &= ComposedShotKit.Check(
                name: "hud-clips-at-seat-seam",
                ok: (hudRight <= ((noiseRight * 4.0) + 0.5)),
                detail: $"right-of-seam band diff {hudRight.ToString(format: "F2", provider: ProofApp.Inv)} vs static noise {noiseRight.ToString(format: "F2", provider: ProofApp.Inv)} (an unclipped 480px panel would repaint seat 2)"
            );
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "clip-png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: controlAPath);
            ComposedShotKit.TryDelete(path: controlBPath);
            ComposedShotKit.TryDelete(path: hudPath);
        }

        return passed;
    }

    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var tag = (backend ?? "directx");
        var deselectAPath = ShotPath(pid: pid, tag: tag, name: "deselect-a");
        var selectedPath = ShotPath(pid: pid, tag: tag, name: "selected");
        var deselectBPath = ShotPath(pid: pid, tag: tag, name: "deselect-b");
        var rejectPath = ShotPath(pid: pid, tag: tag, name: "reject");
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Pin the stage: console panel off (the pixel band must read the WORLD), roster to seat 1 (dev-machine
            // pads auto-seat extras), census to 0 (a static world — the highlight diff's noise floor).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

            for (var seat = 2; (seat <= 4); seat++) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-roster-leave-{seat}");
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0", expect: "[world.population:", name: "pin-census-zero");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "enter-editor");

            // (a) A discrete place is EXACTLY one journal entry.
            var dirty0 = ReadDirty(ctx: ctx, name: "dirty-baseline");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.place boulder 0.5 0.3", expect: "one mutation submitted", name: "place-echo");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {(dirty0 + 1)} ", name: "place-is-one-entry");

            // (b) THE HEADLINE — drag coalescing at the wire: grab, move THREE times (client-local; dirty must not
            // move), release = exactly ONE more entry, position committed.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select scene boulder-1", expect: "[editor.select: seat 1 scene 'boulder-1'", name: "select-drag-subject");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab", expect: "dragging scene 'boulder-1'", name: "grab-begins");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 2 0 0", expect: "[editor.drag: seat 1 scene 'boulder-1' at (0.80, 0.72, -0.30)]", name: "drag-step-1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 1.5 0 0", expect: "[editor.drag: seat 1 scene 'boulder-1' at (2.30, 0.72, -0.30)]", name: "drag-step-2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 0.5 0 1", expect: "[editor.drag: seat 1 scene 'boulder-1' at (2.80, 0.72, 0.70)]", name: "drag-step-3");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {(dirty0 + 1)} ", name: "drag-moves-no-wire");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.release", expect: "(-1.20, 0.72, -0.30) -> (2.80, 0.72, 0.70)", name: "release-echo");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {(dirty0 + 2)} ", name: "release-is-one-entry");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "sel=scene 'boulder-1' at (2.80, 0.72, 0.70)", name: "position-committed");

            // (c) Undo restores the pre-drag position (the journal replay) and the dirty count steps back.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.undo", expect: "[world.undo: dropped 1,", name: "undo-echo");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "sel=scene 'boulder-1' at (-1.20, 0.72, -0.30)", name: "undo-restores-position");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {(dirty0 + 1)} ", name: "undo-steps-dirty-back");

            // (d) The look-ray pick: pose the camera 5.5u in front of boulder-2 (center (0.6, 0.88, 0.5), r 1.1),
            // looking straight down +Z — the crosshair ray must name that row.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 0.6 0.88 -5 0 0", expect: "[editor.cam.pose: seat 1", name: "pose-for-pick");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.pick", expect: "selected scene 'boulder-2'", name: "look-ray-picks-aimed-row");

            // (e) The highlight in pixels: from the same vantage, a deselected control pair bounds the static noise
            // floor and the selected shot must clear it decisively (the amber tint on boulder-2). Let the last
            // mutation toast expire first so the band reads geometry only.
            Thread.Sleep(millisecondsTimeout: 3400);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.deselect", expect: "[editor.deselect: seat 1 cleared]", name: "deselect-for-control");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "deselect-shot-a", path: deselectAPath);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "deselect-shot-b", path: deselectBPath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select scene boulder-2", expect: "[editor.select: seat 1 scene 'boulder-2'", name: "select-for-highlight");
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "selected-shot", path: selectedPath);

            var controlA = ComposedShotKit.DecodePng(path: deselectAPath);
            var controlB = ComposedShotKit.DecodePng(path: deselectBPath);
            var selected = ComposedShotKit.DecodePng(path: selectedPath);
            var bandX = (int)(width * 0.25);
            var bandY = (int)(height * 0.25);
            var bandW = (int)(width * 0.35);
            var bandH = (int)(height * 0.50);
            var noiseDiff = MeanAbsDiff(a: controlB, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);
            var highlightDiff = MeanAbsDiff(a: selected, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);

            passed &= ComposedShotKit.Check(
                name: "selection-highlight-visible",
                ok: ((highlightDiff > 0.8) && (highlightDiff > ((noiseDiff * 4.0) + 0.4))),
                detail: $"selected-vs-control band diff {highlightDiff.ToString(format: "F2", provider: ProofApp.Inv)} vs static noise {noiseDiff.ToString(format: "F2", provider: ProofApp.Inv)}"
            );

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "edit-rounds-refused-nothing", expected: 0);

            // (f) Capacity honesty: flood placements past the authoring headroom. The envelope must reject loudly,
            // a further placement must leave dirty unchanged, and the rejection must surface as the danger toast.
            const int floodPlacements = 40;
            var dirtyBeforeFlood = ReadDirty(ctx: ctx, name: "dirty-before-flood");
            var floodMark = ctx.Collector.Count;

            for (var index = 0; (index < floodPlacements); index++) {
                ComposedShotKit.Send(ctx: ctx, line: "editor.place slab");
            }

            var rejection = ComposedShotKit.Await(
                collector: ctx.Collector,
                mark: floodMark,
                predicate: line => (line.Contains(value: "[world.mutation rejected: UpsertSceneRow") && line.Contains(value: "render envelope")),
                deadlineSeconds: 60.0
            );

            passed &= ComposedShotKit.Check(name: "capacity-rejects-loudly", ok: (rejection is not null), detail: (rejection?.Trim() ?? "(no envelope rejection line)"));

            var dirtyAtCeiling = ReadDirty(ctx: ctx, name: "dirty-at-ceiling");
            var extraMark = ctx.Collector.Count;

            ComposedShotKit.Send(ctx: ctx, line: "editor.place slab");
            passed &= ComposedShotKit.Check(
                name: "over-ceiling-place-rejected",
                ok: (ComposedShotKit.Await(collector: ctx.Collector, mark: extraMark, predicate: line => line.Contains(value: "[world.mutation rejected: UpsertSceneRow"), deadlineSeconds: 20.0) is not null),
                detail: "the placement past the ceiling rejected"
            );
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "reject-toast-shot", path: rejectPath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {dirtyAtCeiling} ", name: "rejection-leaves-dirty-unchanged");

            var reject = ComposedShotKit.DecodePng(path: rejectPath);
            var stripX = (int)(width * 0.55);
            var stripY = ((height / 2) - 16);
            var stripW = ((width - 40) - stripX);
            var stripH = 32;
            var rejectRed = CountDangerRed(image: reject, x: stripX, y: stripY, w: stripW, h: stripH);
            var controlRed = CountDangerRed(image: controlA, x: stripX, y: stripY, w: stripW, h: stripH);

            passed &= ComposedShotKit.Check(
                name: "capacity-rejection-surfaces-as-toast",
                ok: ((rejectRed > (controlRed + 100)) && (rejectRed > 120)),
                detail: $"danger-red pixels in the toast strip: reject {rejectRed} vs control {controlRed}"
            );

            // Every slab the envelope turned away is one refusal, and the journal counts the ones that landed: the
            // flood plus the one over-ceiling place, less whatever the envelope had room for.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "flood-refused-only-the-over-ceiling-rows",
                expected: ((floodPlacements + 1) - (dirtyAtCeiling - dirtyBeforeFlood)));

            // (g) every editor-local typed float surface rejects non-finite values loudly, before any local
            // state can be poisoned (NaN slides past ordinary range guards; a non-finite center would rebuild the SDF).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.speed NaN", expect: "as a finite number", name: "finite-cam-speed");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose Infinity 0 0", expect: "as finite numbers", name: "finite-cam-pose");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.snap NaN", expect: "expected on|off|<pitch>", name: "finite-snap-pitch");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag NaN 0 0", expect: "as finite numbers", name: "finite-drag");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.move NaN 0 0", expect: "as finite numbers", name: "finite-move");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.nudge -Infinity 0 0", expect: "as finite numbers", name: "finite-nudge");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.place boulder NaN", expect: "bad radius", name: "finite-place");

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "non-finite-round-refused-only-its-seven", expected: 7);

            // (h) editor deactivation owns the COMPLETE teardown: an exit mid-drag drops the pending row and
            // the selection, so re-entry starts clean and the abandoned drag can never be committed.
            var dirtyBeforeExit = ReadDirty(ctx: ctx, name: "dirty-before-exit-mid-drag");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select scene boulder-1", expect: "[editor.select: seat 1 scene 'boulder-1'", name: "exit-mid-drag-select");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab", expect: "dragging scene 'boulder-1'", name: "exit-mid-drag-grab");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 3 0 0", expect: "[editor.drag: seat 1", name: "exit-mid-drag-move");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit", expect: "[editor.exit: seat 1", name: "exit-mid-drag-exit");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "exit-mid-drag-reenter");

            var statusMark = ctx.Collector.Count;

            ComposedShotKit.Send(ctx: ctx, line: "editor.status");

            var statusLine = ComposedShotKit.Await(collector: ctx.Collector, mark: statusMark, predicate: l => l.Contains(value: "[editor.status: seat 1 editing"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(
                name: "reenter-starts-clean",
                ok: ((statusLine is not null) && statusLine.Contains(value: "sel=none") && !statusLine.Contains(value: "drag=")),
                detail: (statusLine?.Trim() ?? "(no editor.status echo)")
            );
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.release", expect: "has no live drag", name: "abandoned-drag-not-committable");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {dirtyBeforeExit} ", name: "exit-mid-drag-no-wire");

            // The departure variant: a seat that LEAVES mid-drag is pruned, and its slot's next occupant inherits
            // neither the drag nor the selection.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.join cobalt 2", expect: "[player.join:", name: "depart-join-seat2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter 2", expect: "[editor.enter: seat 2 editing", name: "depart-enter-seat2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select scene boulder-2 2", expect: "[editor.select: seat 2 scene 'boulder-2'", name: "depart-select-seat2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab 2", expect: "dragging scene 'boulder-2'", name: "depart-grab-seat2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.leave 2", expect: "[player.leave: player 2 left", name: "depart-leave-mid-drag");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.join cobalt 2", expect: "[player.join:", name: "depart-rejoin-seat2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter 2", expect: "[editor.enter: seat 2 editing", name: "depart-reenter-seat2");

            var rejoinMark = ctx.Collector.Count;

            ComposedShotKit.Send(ctx: ctx, line: "editor.status 2");

            var rejoinStatus = ComposedShotKit.Await(collector: ctx.Collector, mark: rejoinMark, predicate: l => l.Contains(value: "[editor.status: seat 2 editing"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(
                name: "rejoined-slot-starts-clean",
                ok: ((rejoinStatus is not null) && rejoinStatus.Contains(value: "sel=none") && !rejoinStatus.Contains(value: "drag=")),
                detail: (rejoinStatus?.Trim() ?? "(no editor.status 2 echo)")
            );
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.release 2", expect: "has no live drag", name: "departed-drag-not-committable");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit 2", expect: "[editor.exit: seat 2", name: "depart-cleanup-exit");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.leave 2", expect: "[player.leave: player 2 left", name: "depart-cleanup-leave");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: $"dirty {dirtyBeforeExit} ", name: "departed-drag-no-wire");

            // Both abandoned-drag releases are deliberate refusals; nothing else in the teardown round is.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "teardown-round-refused-only-its-two", expected: 2);

            // (i) a frozen released preview resolves independently on ITS OWN result, with the honest reason narrated.
            // Apply: the release's delivery carries exactly the expected row.
            var applyMark = ctx.Collector.Count;

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select scene boulder-1", expect: "[editor.select: seat 1 scene 'boulder-1'", name: "retire-apply-select");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab", expect: "dragging scene 'boulder-1'", name: "retire-apply-grab");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 1 0 0", expect: "[editor.drag: seat 1", name: "retire-apply-move");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.release", expect: "one mutation submitted", name: "retire-apply-release");
            passed &= ComposedShotKit.Check(
                name: "frozen-retires-on-own-apply",
                ok: (ComposedShotKit.Await(collector: ctx.Collector, mark: applyMark, predicate: l => l.Contains(value: "frozen scene 'boulder-1' retired: applied"), deadlineSeconds: 15.0) is not null),
                detail: "the released preview retired against its own delivered row"
            );

            // Rejection with a SAME-BATCH unrelated delivery: the seat's release is denied (revoked grant) while a
            // console kit mutation applies in the same drain — the retire reason must be the REJECTION correlation,
            // never the unrelated delivery.
            var preRejectStatus = ReadSelectionPosition(ctx: ctx, name: "document-pose-before-rejection");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.revoke seat1 mutate section:scene", expect: "[world.revoke: seat1 mutate section:scene]", name: "retire-reject-revoke");

            var rejectMark = ctx.Collector.Count;

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab", expect: "dragging scene 'boulder-1'", name: "retire-reject-grab");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 2 0 0", expect: "[editor.drag: seat 1", name: "retire-reject-move");
            ComposedShotKit.Send(ctx: ctx, line: "editor.release");
            ComposedShotKit.Send(ctx: ctx, line: "world.kit.tune runner moveSpeed 7");
            passed &= ComposedShotKit.Check(
                name: "frozen-retires-on-own-rejection",
                ok: (ComposedShotKit.Await(collector: ctx.Collector, mark: rejectMark, predicate: l => l.Contains(value: "frozen scene 'boulder-1' retired: rejected"), deadlineSeconds: 15.0) is not null),
                detail: "the rejected release correlated back to its frozen preview"
            );

            var retireLines = ctx.Collector.Snapshot();
            var unrelatedRetire = false;

            for (var i = rejectMark; (i < retireLines.Length); i++) {
                if (retireLines[i].Contains(value: "retired: applied") || retireLines[i].Contains(value: "retired: deadline")) {
                    unrelatedRetire = true;
                }
            }

            passed &= ComposedShotKit.Check(
                name: "unrelated-delivery-does-not-retire",
                ok: !unrelatedRetire,
                detail: "no apply/deadline retirement fired in the rejection round (the same-batch kit delivery left the preview to its own result)"
            );

            var postRejectStatus = ReadSelectionPosition(ctx: ctx, name: "document-pose-after-rejection");

            passed &= ComposedShotKit.Check(
                name: "rejected-row-snaps-back",
                ok: ((preRejectStatus is not null) && string.Equals(a: preRejectStatus, b: postRejectStatus, comparisonType: StringComparison.Ordinal)),
                detail: $"selection pose before '{preRejectStatus}' vs after '{postRejectStatus}'"
            );
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.undo", expect: "[world.undo: dropped 1,", name: "retire-reject-undo-kit");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.grant seat1 mutate section:scene", expect: "[world.grant: seat1 mutate section:scene]", name: "retire-reject-regrant");

            // The revoked release is the round's ONE deliberate refusal.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "retire-round-refused-only-the-denied-release", expected: 1);

            // (j) the candidate ring is explicit and bounded: near the flooded scene the ring caps at 16;
            // far from everything it is honestly empty; editor.status narrates the policy.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 0.6 0.88 -5 0 0", expect: "[editor.cam.pose: seat 1", name: "candidates-pose-near");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.next", expect: "of 16 candidates (r 32u, cap 16)", name: "candidates-cap-engages");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 500 5 500 0 0", expect: "[editor.cam.pose: seat 1", name: "candidates-pose-far");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.next", expect: "no candidates within 32u", name: "candidates-radius-bounds");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "cand=0 (r 32u, cap 16)", name: "candidates-status-narrates");

            // (l) the editor/authoring policy row is DATA, split honestly at apply: the candidate radius/cap
            // are LIVE-CONSUMED (the very next chord reads the new value, no restart) while the headroom/repeat-cap
            // fields are BOOT-CONSUMED (the running session's frozen render-envelope probe cannot retroactively
            // grow — the accept echo narrates "next boot" for that half of the SAME whole-row mutation).
            const string authoringDefault = "{\"authoringHeadroomRows\":32,\"authoringHeadroomScreens\":4,\"authoringHeadroomPlacements\":8,\"maxRepeatPerSegment\":8,\"minPlacementScale\":0.2,\"maxPlacementScale\":5,\"candidateRadius\":32,\"candidateCap\":16,\"workbenchFraction\":0.7,\"previewDeadlineFrames\":12}";
            const string authoringHugeRadius = "{\"authoringHeadroomRows\":32,\"authoringHeadroomScreens\":4,\"authoringHeadroomPlacements\":8,\"maxRepeatPerSegment\":8,\"minPlacementScale\":0.2,\"maxPlacementScale\":5,\"candidateRadius\":2000,\"candidateCap\":16,\"workbenchFraction\":0.7,\"previewDeadlineFrames\":12}";
            const string authoringTinyCap = "{\"authoringHeadroomRows\":32,\"authoringHeadroomScreens\":4,\"authoringHeadroomPlacements\":8,\"maxRepeatPerSegment\":8,\"minPlacementScale\":0.2,\"maxPlacementScale\":5,\"candidateRadius\":32,\"candidateCap\":3,\"workbenchFraction\":0.7,\"previewDeadlineFrames\":12}";

            // STILL at the far pose (500, 5, 500) from (j): the 32u-radius ring there is PROVEN empty
            // ("candidates-radius-bounds" above). Growing the live radius past the ~700u distance to the flooded
            // cluster must cross that distance boundary on the very next chord — no relaunch, no rebuild wait.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringHugeRadius}", expect: "candidate/layout/preview levers live now", name: "authoring-live-set-huge-radius");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.next", expect: "candidates (r 2000u, cap 16)", name: "authoring-live-radius-crosses-boundary");

            // Restore the default radius — the far pose reads honestly empty again, proving the live read cuts both
            // ways (no stale widened cache).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringDefault}", expect: "candidate/layout/preview levers live now", name: "authoring-live-set-restore-radius");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.next", expect: "no candidates within 32u", name: "authoring-live-radius-restores-empty");

            // The candidate CAP is the second live lever: back at the near pose (the proven 16-candidate ring),
            // shrinking the cap to 3 must shrink the ACTUAL ring (not just its echoed number) on the next chord —
            // GatherCandidates' Math.Min(count, CandidateCap) reads the live cap every call.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 0.6 0.88 -5 0 0", expect: "[editor.cam.pose: seat 1", name: "authoring-live-pose-near-for-cap");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringTinyCap}", expect: "candidate/layout/preview levers live now", name: "authoring-live-set-tiny-cap");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "cand=3 (r 32u, cap 3)", name: "authoring-live-cap-shrinks-ring");

            // Restore the default cap — the ring widens back to 16 on the next read.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringDefault}", expect: "candidate/layout/preview levers live now", name: "authoring-live-set-restore-cap");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "cand=16 (r 32u, cap 16)", name: "authoring-live-cap-restores-ring");

            // The boot-consumed half of the SAME mutation kind: a headroom change narrates "next boot" in the SAME
            // accept line as the live narration above — the honest split, never two separate mutations.
            const string authoringGrownHeadroom = "{\"authoringHeadroomRows\":40,\"authoringHeadroomScreens\":4,\"authoringHeadroomPlacements\":8,\"maxRepeatPerSegment\":8,\"minPlacementScale\":0.2,\"maxPlacementScale\":5,\"candidateRadius\":32,\"candidateCap\":16,\"workbenchFraction\":0.7,\"previewDeadlineFrames\":12}";

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringGrownHeadroom}", expect: "headroom + max-repeat-per-segment apply at next boot", name: "authoring-boot-consumed-narrates-next-boot");

            // A malformed row (min scale above max) rejects loudly before it ever reaches the frozen probe.
            const string authoringInvertedScale = "{\"authoringHeadroomRows\":32,\"authoringHeadroomScreens\":4,\"authoringHeadroomPlacements\":8,\"maxRepeatPerSegment\":8,\"minPlacementScale\":6,\"maxPlacementScale\":5,\"candidateRadius\":32,\"candidateCap\":16,\"workbenchFraction\":0.7,\"previewDeadlineFrames\":12}";

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringInvertedScale}", expect: "exceeds authoring.maxPlacementScale", name: "authoring-validator-rejects-inverted-scale");

            // Restore the byte-identical default row so the session's authoring policy ends where it started.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.authoring.set {authoringDefault}", expect: "candidate/layout/preview levers live now", name: "authoring-live-set-final-restore");

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "authoring-round-refused-only-the-inverted-scale", expected: 1);

            // (k) No loud GPU/runtime faults anywhere in the session (both streams).
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: deselectAPath);
            ComposedShotKit.TryDelete(path: selectedPath);
            ComposedShotKit.TryDelete(path: deselectBPath);
            ComposedShotKit.TryDelete(path: rejectPath);
        }

        return passed;
    }

    static string ShotPath(int pid, string tag, string name) {
        return Path.Combine(Path.GetTempPath(), $"puck-editor-edit-{pid}-{tag}-{name}.png");
    }

    static readonly Regex SelectionEcho = new(pattern: @"sel=[^)]+\)", options: RegexOptions.Compiled);

    // Sends editor.status and returns the seat-1 selection clause ("sel=scene 'x' at (a, b, c)") — the document-pose
    // witness the rejection round compares before/after.
    static string? ReadSelectionPosition(ComposedShotKit.Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "editor.status");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[editor.status: seat 1 editing"), deadlineSeconds: 15.0);
        var clause = ((line is not null) ? SelectionEcho.Match(input: line) : null);
        var value = (((clause is { Success: true })) ? clause.Value : null);

        _ = ComposedShotKit.Check(name: name, ok: (value is not null), detail: (value ?? "(no selection clause in editor.status)"));

        return value;
    }

    // Sends world.status and parses the journal dirty counter (the read-after-write barrier makes it settled).
    static int ReadDirty(ComposedShotKit.Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.status");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => DirtyEcho.IsMatch(input: candidate), deadlineSeconds: 15.0);

        if (line is null) {
            _ = ComposedShotKit.Check(name: name, ok: false, detail: "(no world.status dirty echo)");

            return -1;
        }

        var dirty = int.Parse(s: DirtyEcho.Match(input: line).Groups[1].Value, provider: ProofApp.Inv);

        _ = ComposedShotKit.Check(name: name, ok: true, detail: $"dirty {dirty}");

        return dirty;
    }

    // Mean absolute per-channel difference over a region (the highlight witness; the world is pinned static).
    static double MeanAbsDiff((int Width, int Height, byte[] Rgba) a, (int Width, int Height, byte[] Rgba) b, int x, int y, int w, int h) {
        var sum = 0L;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * a.Width) + col) * 4);

                sum += Math.Abs(value: (a.Rgba[i] - b.Rgba[i]));
                sum += Math.Abs(value: (a.Rgba[(i + 1)] - b.Rgba[(i + 1)]));
                sum += Math.Abs(value: (a.Rgba[(i + 2)] - b.Rgba[(i + 2)]));
            }
        }

        return ((double)sum / ((long)w * h * 3));
    }

    // The danger-hue population (the ui-floor discriminator): red clearly dominating BOTH other channels.
    static int CountDangerRed((int Width, int Height, byte[] Rgba) image, int x, int y, int w, int h) {
        var count = 0;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * image.Width) + col) * 4);
                int r = image.Rgba[i];
                int g = image.Rgba[(i + 1)];
                int b = image.Rgba[(i + 2)];

                if ((r > (g + 40)) && (r > (b + 40))) {
                    count++;
                }
            }
        }

        return count;
    }
}

// ============================================================================================
// EDITOR-CAMERAS — the camera live-apply proof: camera rows edit LIVE. The
// baked default declares two View screens (0 → 'overhead', unanchored with a lookAt rig; 2 →
// 'first-person', entity-anchored with a firstPerson rig),
// so the offscreen pool boots with two registered camera views — world.view-refresh's count
// echo is the pipe-observable witness. A pose/aim edit rewrites the running view's rig in
// place ('pose updated live'), a dimension change recreates it ('recreated live (WxH)'), a
// screen re-point (View→View) binds the new camera and releases the orphan, and the
// View→None transition unbinds the slot AND releases the registration — the count drops and
// no stale offscreen render survives. Runs on BOTH backends like editor-mode.
//
// The DOCUMENT side is read off world.cameras, the camera table itself, rather than off the
// reconcile narration: the boot rows must parse (anchor keyword, a rig token from the closed
// vocabulary, dimensions), a live add must APPEAR in the table and an undo must take it away.
// A third section boots every shipped world and reads its camera table back the same way,
// checking the segment count against world.status's — the instrument OQ-12 never had.
// ============================================================================================
static class EditorCamerasProof {
    // One world.cameras row: '<name> anchor=<kind> rig=<kind> <W>x<H>', the rig token a CLOSED vocabulary.
    static readonly Regex CameraSegment = new(options: RegexOptions.Compiled,
        pattern: @"^(\S+) anchor=(entity:\d+|entityLeaf:\S+|placement:\S+|group:\S+|none) rig=(chase|firstPerson|orbit|lookAt|dolly) (\d+)x(\d+)$");
    static readonly Regex StatusCameras = new(pattern: @"cameras (\d+)", options: RegexOptions.Compiled);

    public static int RunEditorCameras(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 150, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        Console.WriteLine(value: "[proof] === editor-cameras (a): Direct3D 12 (the default backend) ===");
        var directXPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === editor-cameras (b): Vulkan ===");
        var vulkanPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === editor-cameras (c): the camera table across every shipped world ===");
        var shippedPassed = RunShippedWorlds(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        var passed = (directXPassed && vulkanPassed && shippedPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] editor-cameras proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Baseline: both declared View screens registered their cameras' offscreen renders at boot.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.view-refresh", expect: "2 camera view(s) registered", name: "boot-two-camera-views");

            // The DOCUMENT-side baseline, read off the camera table itself rather than off a narration string: both
            // declared rows, each with a closed-vocabulary rig token and its render dimensions.
            var bootTable = ReadCameraTable(ctx: ctx);

            passed &= ComposedShotKit.Check(name: "boot-camera-table",
                ok: (WellFormedCameraTable(table: bootTable) && TableNames(table: bootTable).SetEquals(other: ["overhead", "first-person"])),
                detail: (bootTable?.Trim() ?? "(no world.cameras echo)"));

            // (a) LIVE POSE EDIT (unanchored): re-aim 'overhead'. The mutation applies, the client reconcile rewrites
            // the running view's rig in place, and a stale "applies at next boot" narration must never appear.
            var mark = ctx.Collector.Count;

            ComposedShotKit.Send(ctx: ctx, line: "world.camera.set {\"name\":\"overhead\",\"anchor\":null,\"offset\":[0,18,0],\"rig\":{\"$type\":\"lookAt\",\"target\":[0,0.5,-2.5],\"fieldOfViewRadians\":0.96},\"renderWidth\":256,\"renderHeight\":144}");

            var poseLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
                predicate: l => l.Contains(value: "[world.camera: 'overhead' pose updated live]"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "unanchored-pose-updates-live", ok: (poseLine is not null),
                detail: (poseLine?.Trim() ?? "(no reconcile line for 'overhead')"));
            passed &= AssertNoNextBoot(ctx: ctx, mark: mark, name: "no-next-boot-narration");

            // (b) The anchored camera's pose edit rides the same live lane (rig property writes + a fresh anchor id).
            mark = ctx.Collector.Count;
            ComposedShotKit.Send(ctx: ctx, line: "world.camera.set {\"name\":\"first-person\",\"anchor\":{\"$type\":\"entity\",\"index\":0},\"offset\":[0,1.5,0],\"rig\":{\"$type\":\"firstPerson\",\"eyeOffset\":[0,0,0],\"focusDistance\":0,\"fieldOfViewRadians\":1.4},\"renderWidth\":256,\"renderHeight\":144}");

            var anchoredLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
                predicate: l => l.Contains(value: "[world.camera: 'first-person' pose updated live]"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "anchored-pose-updates-live", ok: (anchoredLine is not null),
                detail: (anchoredLine?.Trim() ?? "(no reconcile line for 'first-person')"));

            // (c) NEW ROW + RE-POINT (View→View): 'birdseye' enters the document, then screen 0 films it. The new
            // camera registers, the orphaned 'overhead' releases, and the pool count holds at 2.
            const string BirdseyeRow = "world.camera.set {\"name\":\"birdseye\",\"anchor\":null,\"offset\":[12,10,0],\"rig\":{\"$type\":\"lookAt\",\"target\":[0,0,0],\"fieldOfViewRadians\":0.9},\"renderWidth\":256,\"renderHeight\":144}";

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: BirdseyeRow,
                expect: "[world.mutation: UpsertCamera 'birdseye' applied]", name: "new-camera-row-applies");

            // THE TABLE IS THE READ-BACK. A live add must APPEAR in world.cameras and an undo must take it away —
            // measured on the document's own camera listing, not on the mutation's narration.
            var addedTable = ReadCameraTable(ctx: ctx);

            passed &= ComposedShotKit.Check(name: "live-add-appears-in-camera-table",
                ok: (WellFormedCameraTable(table: addedTable) && TableNames(table: addedTable).SetEquals(other: ["overhead", "first-person", "birdseye"])),
                detail: (addedTable?.Trim() ?? "(no world.cameras echo)"));
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.undo", expect: "[world.undo: dropped 1,", name: "undo-the-new-row");

            var undoneTable = ReadCameraTable(ctx: ctx);

            passed &= ComposedShotKit.Check(name: "undo-removes-it-from-camera-table",
                ok: (WellFormedCameraTable(table: undoneTable) && TableNames(table: undoneTable).SetEquals(other: ["overhead", "first-person"])),
                detail: (undoneTable?.Trim() ?? "(no world.cameras echo)"));

            // Put it back — the rest of the session films it.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: BirdseyeRow,
                expect: "[world.mutation: UpsertCamera 'birdseye' applied]", name: "re-add-after-undo");

            mark = ctx.Collector.Count;
            ComposedShotKit.Send(ctx: ctx, line: "world.screen.set {\"index\":0,\"origin\":[-3,1.2,-3],\"right\":[1,0,0],\"up\":[0,1,0],\"halfWidth\":1.3,\"halfHeight\":1,\"halfDepth\":0.12,\"round\":0.08,\"source\":{\"$type\":\"view\",\"cameraName\":\"birdseye\"},\"route\":{\"engageable\":true,\"engageRadius\":2.5}}");

            var repointLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
                predicate: l => l.Contains(value: "[world.screen: screen 0 showing camera 'birdseye']"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "screen0-repoints-live", ok: (repointLine is not null),
                detail: (repointLine?.Trim() ?? "(no 'showing camera birdseye' line)"));

            var orphanLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
                predicate: l => (l.Contains(value: "[world.screen: camera view 'overhead' released") && l.Contains(value: "no remaining screen references it")), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "orphaned-overhead-released", ok: (orphanLine is not null),
                detail: (orphanLine?.Trim() ?? "(no released line for 'overhead')"));
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.view-refresh", expect: "2 camera view(s) registered", name: "pool-count-holds-after-repoint");

            // (d) DIMENSION CHANGE: an offscreen render target cannot resize — the registration recreates in place.
            passed &= ComposedShotKit.SendAwait(ctx: ctx,
                line: "world.camera.set {\"name\":\"birdseye\",\"anchor\":null,\"offset\":[12,10,0],\"rig\":{\"$type\":\"lookAt\",\"target\":[0,0,0],\"fieldOfViewRadians\":0.9},\"renderWidth\":320,\"renderHeight\":180}",
                expect: "[world.camera: 'birdseye' recreated live (320x180)]", name: "dimension-change-recreates");

            // (e) THE VIEW→NONE TRANSITION: the slot unbinds AND the camera registration releases — the pool
            // count drops and world.screens reads the slot honestly none/unbound (no stale offscreen render).
            mark = ctx.Collector.Count;
            ComposedShotKit.Send(ctx: ctx, line: "world.screen.set {\"index\":0,\"origin\":[-3,1.2,-3],\"right\":[1,0,0],\"up\":[0,1,0],\"halfWidth\":1.3,\"halfHeight\":1,\"halfDepth\":0.12,\"round\":0.08,\"source\":{\"$type\":\"none\"},\"route\":{\"engageable\":true,\"engageRadius\":2.5}}");

            var unboundLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
                predicate: l => l.Contains(value: "[world.screen: screen 0 unbound]"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "screen0-unbinds", ok: (unboundLine is not null),
                detail: (unboundLine?.Trim() ?? "(no 'screen 0 unbound' line)"));

            var releaseLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
                predicate: l => (l.Contains(value: "[world.screen: camera view 'birdseye' released") && l.Contains(value: "no remaining screen references it")), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "cr6-transition-releases-view", ok: (releaseLine is not null),
                detail: (releaseLine?.Trim() ?? "(no released line for 'birdseye' — the view stayed registered)"));
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.view-refresh", expect: "1 camera view(s) registered", name: "pool-count-drops");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.screens", expect: "0 none unbound", name: "screen0-reads-none-unbound");

            // (f) REMOVE: the row is unreferenced (validator-clean) and already released — the document-side removal
            // applies with no view work left to do.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.camera.remove birdseye",
                expect: "[world.mutation: RemoveCamera 'birdseye' applied]", name: "camera-remove-applies");

            // Every line above was meant to SUCCEED: a stale payload the validator refuses would leave a nonzero count
            // here even when the awaits above time out into vacuous passes.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "no-silent-rejections", expected: 0);

            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return passed;
    }

    // (g) THE SHIPPED WORLDS, through the same table. Every checked-in world boots and its camera rows are read back
    // off world.cameras: the segment count must match world.status's camera count (a row that failed to re-encode
    // would go missing or double), and every segment must carry an anchor keyword, a rig token from the CLOSED
    // vocabulary, and render dimensions. This is the instrument OQ-12 (the camera re-encoding across the shipped
    // worlds) never had — it reads what the documents actually decoded to, on one backend, one boot each.
    static bool RunShippedWorlds(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var worldsDir = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World", path4: Path.Combine("Assets", "worlds"));
        var passed = true;

        foreach (var worldName in new[] { "default.world.json", "kart-remap.world.json", "expo.world.json", "kiosk.world.json", "planetoid.world.json" }) {
            var worldPath = Path.Combine(path1: worldsDir, path2: worldName);

            if (!File.Exists(path: worldPath)) {
                Console.WriteLine(value: $"[proof]   (skip) {worldName} not present — author it first");

                continue;
            }

            passed &= ReadShippedWorldCameras(exe: exe, repoRoot: repoRoot, worldName: worldName, worldPath: worldPath, width: width, height: height, exitAfterSeconds: exitAfterSeconds);
        }

        return passed;
    }

    static bool ReadShippedWorldCameras(string exe, string repoRoot, string worldName, string worldPath, int width, int height, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height,
            exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", worldPath]);
        var process = ctx.Process;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            var status = ComposedShotKit.Await(collector: ctx.Collector, mark: SendStatus(ctx: ctx), predicate: l => l.Contains(value: "[world.status:"), deadlineSeconds: 15.0);
            var declared = ((status is null) ? -1 : (StatusCameras.Match(input: status) is { Success: true } m ? int.Parse(s: m.Groups[1].Value, provider: ProofApp.Inv) : -1));
            var table = ReadCameraTable(ctx: ctx);
            var names = TableNames(table: table);
            var wellFormed = ComposedShotKit.Check(name: $"{worldName}-camera-table",
                ok: (WellFormedCameraTable(table: table) && (declared >= 0) && (names.Count == declared)),
                detail: $"world.status declares {declared} camera(s), world.cameras lists {names.Count}: {table?.Trim() ?? "(no echo)"}");

            return (wellFormed & ComposedShotKit.SettleWireErrors(ctx: ctx, name: $"{worldName}-reads-refused-nothing", expected: 0));
        }
        finally {
            ComposedShotKit.KillQuietly(process: process);
        }
    }

    static int SendStatus(ComposedShotKit.Ctx ctx) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.status");

        return mark;
    }

    // The camera table's echoed line, read Immediate (the stdin barrier holds it behind any pending mutation).
    static string? ReadCameraTable(ComposedShotKit.Ctx ctx) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.cameras");

        return ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.cameras:"), deadlineSeconds: 15.0);
    }

    // Every listed camera must carry an anchor keyword, a rig token from the CLOSED vocabulary, and dimensions.
    // An empty table ("none declared") is well-formed; a table whose rows do not parse is not.
    static bool WellFormedCameraTable(string? table) {
        if (table is null) {
            return false;
        }

        if (table.Contains(value: "[world.cameras: none declared]")) {
            return true;
        }

        return CameraSegments(table: table).All(predicate: segment => CameraSegment.IsMatch(input: segment));
    }

    // The camera NAMES the table lists — the identity set an add must join and an undo must leave.
    static HashSet<string> TableNames(string? table) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if ((table is null) || table.Contains(value: "[world.cameras: none declared]")) {
            return names;
        }

        foreach (var segment in CameraSegments(table: table)) {
            if (CameraSegment.Match(input: segment) is { Success: true } match) {
                _ = names.Add(item: match.Groups[1].Value);
            }
        }

        return names;
    }

    // The listing's payload split on its ' | ' row separator: everything between '[world.cameras:' and the closing ']'.
    static IEnumerable<string> CameraSegments(string table) {
        var open = table.IndexOf(value: "[world.cameras:", comparisonType: StringComparison.Ordinal);
        var close = table.LastIndexOf(value: ']');

        if ((open < 0) || (close <= open)) {
            return [];
        }

        return table[(open + "[world.cameras:".Length)..close].Split(separator: '|', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // A stale camera "applies at next boot" narration must never resurface for a camera mutation.
    static bool AssertNoNextBoot(ComposedShotKit.Ctx ctx, int mark, string name) {
        var snapshot = ctx.Collector.Snapshot();

        for (var i = mark; (i < snapshot.Length); i++) {
            if (snapshot[i].Contains(value: "[world.camera: applies at next boot]")) {
                return ComposedShotKit.Check(name: name, ok: false, detail: snapshot[i].Trim());
            }
        }

        return ComposedShotKit.Check(name: name, ok: true, detail: "no next-boot narration");
    }
}

// ============================================================================================
// PLACEMENTS — the creations/placements proof: import a PROOF-AUTHORED creation
// through the strict canonicalizer, stamp it (pixel evidence over empty grass), corrupt the
// hash pin (loud reject), drag it (one journal entry), undo it, reject the no-cascade
// creation removal, walk the animated fixture's timeline (pixel motion), flood the reserved
// headroom (the word-exact envelope ceiling), and prove the ouroboros WITH creations
// (save -> reload -> save byte-identity of the inline-canonical embeds). Runs on BOTH
// backends like editor-mode. See the header's subcommand block for the assertion list.
// ============================================================================================
static class PlacementsProof {
    static readonly Regex DirtyEcho = new(pattern: @"dirty (\d+) ", options: RegexOptions.Compiled);
    static readonly Regex AtEcho = new(pattern: @"at \((-?[0-9.]+), (-?[0-9.]+), (-?[0-9.]+)\)", options: RegexOptions.Compiled);

    public static int RunPlacements(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 240, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        Console.WriteLine(value: "[proof] === placements (a): Direct3D 12 (the default backend) ===");
        var directXPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === placements (b): Vulkan ===");
        var vulkanPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        var passed = (directXPassed && vulkanPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] placements proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var tag = ((backend ?? "directx") + "-placements");
        var controlAPath = ShotPath(pid: pid, tag: tag, name: "control-a");
        var controlBPath = ShotPath(pid: pid, tag: tag, name: "control-b");
        var stampPath = ShotPath(pid: pid, tag: tag, name: "stamp");
        var critterAPath = ShotPath(pid: pid, tag: tag, name: "critter-a");
        var critterBPath = ShotPath(pid: pid, tag: tag, name: "critter-b");
        var savedPath = Path.Combine(Path.GetTempPath(), $"puck-placements-{tag}-{pid}-1.world.json");
        var resavedPath = Path.Combine(Path.GetTempPath(), $"puck-placements-{tag}-{pid}-2.world.json");
        // The proof AUTHORS its own creations — Demo content never ships as World content, so no
        // docs/examples fixture enters a World proof. Both cross the same strict import door a player file would.
        var stampFixture = Path.Combine(Path.GetTempPath(), $"puck-placements-{tag}-{pid}-stamp.creation.json");
        var critterFixture = Path.Combine(Path.GetTempPath(), $"puck-placements-{tag}-{pid}-critter.creation.json");

        File.WriteAllText(path: stampFixture, contents: StaticProbeCreationJson);
        File.WriteAllText(path: critterFixture, contents: AnimatedProbeCreationJson);
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Pin the stage: console panel off, roster to seat 1, zero census — the asserted bands must read ONLY
            // the placements under test.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

            for (var seat = 2; (seat <= 4); seat++) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-leave-{seat}");
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0", expect: "[world.population:", name: "census-zero");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "enter-editor");

            // (a) IMPORT: the proof-authored stamp probe crosses the strict canonicalizer — one UpsertCreation entry.
            var dirty0 = ReadDirty(ctx: ctx, name: "dirty-baseline");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"editor.import {stampFixture}",
                expect: "[world.mutation: UpsertCreation 'probe-stamp' applied]", name: "import-stamp-applies");
            passed &= ComposedShotKit.Check(name: "import-one-journal-entry", ok: (ReadDirty(ctx: ctx, name: "dirty-after-import") == (dirty0 + 1)), detail: "import = one journal entry");

            // (b) THE STAMP in pixels: aim at empty grass (+Z of the spawn plaza — no screens, no crowd), bound the
            // static noise floor with a control pair, place, and demand a decisive central-band repaint.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 0 2 10 0 0", expect: "[editor.cam.pose: seat 1", name: "pose-at-grass");
            Thread.Sleep(millisecondsTimeout: 3400); // let the import toast + shimmer decay before the control pair
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "control-shot-a", path: controlAPath);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "control-shot-b", path: controlBPath);

            var placeMark = ctx.Collector.Count;

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.place probe-stamp 0 1.5",
                expect: "[world.mutation: UpsertPlacement 'place-1' applied]", name: "place-stamp-applies");

            var placeEcho = ComposedShotKit.Await(collector: ctx.Collector, mark: placeMark,
                predicate: l => (l.Contains(value: "[editor.place: seat 1 placement 'place-1'") && AtEcho.IsMatch(input: l)), deadlineSeconds: 15.0);
            var placedAt = ((placeEcho is not null) ? AtEcho.Match(input: placeEcho).Value : null);

            passed &= ComposedShotKit.Check(name: "place-echo-carries-position", ok: (placedAt is not null), detail: (placedAt ?? "(no position echo)"));
            Thread.Sleep(millisecondsTimeout: 3400); // toast + shimmer decay: the stamp shot reads geometry only
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "stamp-shot", path: stampPath);

            var controlA = ComposedShotKit.DecodePng(path: controlAPath);
            var controlB = ComposedShotKit.DecodePng(path: controlBPath);
            var stamp = ComposedShotKit.DecodePng(path: stampPath);
            // The stamp band sits ABOVE screen center: the level editor camera puts the horizon at mid-frame and a
            // placed creation's body rises from its origin, so the upper-middle band is where the stamp paints.
            var bandX = (int)(width * 0.375);
            var bandY = (int)(height * 0.08);
            var bandW = (int)(width * 0.25);
            var bandH = (int)(height * 0.37);
            var noise = MeanAbsDiff(a: controlB, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);
            var stampDiff = MeanAbsDiff(a: stamp, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);

            passed &= ComposedShotKit.Check(
                name: "stamp-visible",
                // The MEAN over the whole band stays modest even for a bulky stamp; decisiveness comes from the
                // 4x noise guard over a pinned-static control pair.
                ok: ((stampDiff > 0.8) && (stampDiff > (noise * 4.0))),
                detail: $"stamp band diff {stampDiff.ToString(format: "F2", provider: ProofApp.Inv)} vs noise {noise.ToString(format: "F2", provider: ProofApp.Inv)} (want > 0.8 and > 4x noise)"
            );

            // (c) THE HASH PIN: the same fixture with a zeroed hash must reject loudly naming the canonical sha256,
            // and the journal must not move — a hash the pipeline did not itself compute is never accepted.
            var dirtyBeforeBad = ReadDirty(ctx: ctx, name: "dirty-before-bad-hash");
            var badRow = BuildCorruptCreationRow(fixturePath: stampFixture);

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.creation.set {badRow}",
                expect: "does not match the canonical sha256", name: "corrupt-hash-rejects-loudly");
            passed &= ComposedShotKit.Check(name: "corrupt-hash-changes-nothing", ok: (ReadDirty(ctx: ctx, name: "dirty-after-bad-hash") == dirtyBeforeBad), detail: "journal unchanged");

            // The zeroed hash is the round's ONE deliberate refusal.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "hash-pin-round-refused-only-its-one", expected: 1);

            // (d) THE DRAG CHANNEL on a placement: grab + multi-step motion crosses NO wire (dirty frozen), release
            // commits EXACTLY one whole-row mutation and the frozen preview retires on its own apply.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select placements place-1", expect: "[editor.select: seat 1 placements 'place-1'", name: "select-placement");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab", expect: "dragging placements 'place-1'", name: "grab-placement");

            var dirtyMidDragBase = ReadDirty(ctx: ctx, name: "dirty-at-grab");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 2 0 1", expect: "[editor.drag: seat 1", name: "drag-step-1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 1 0 0", expect: "[editor.drag: seat 1", name: "drag-step-2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 0 0 -1", expect: "[editor.drag: seat 1", name: "drag-step-3");
            passed &= ComposedShotKit.Check(name: "drag-motion-crosses-no-wire", ok: (ReadDirty(ctx: ctx, name: "dirty-mid-drag") == dirtyMidDragBase), detail: "dirty unchanged mid-drag");

            var releaseMark = ctx.Collector.Count;

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.release", expect: "one mutation submitted", name: "release-commits");

            var retired = ComposedShotKit.Await(collector: ctx.Collector, mark: releaseMark,
                predicate: l => l.Contains(value: "frozen placement 'place-1' retired: applied"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "frozen-preview-retires-applied", ok: (retired is not null), detail: (retired?.Trim() ?? "(no retire line)"));
            passed &= ComposedShotKit.Check(name: "release-is-one-journal-entry", ok: (ReadDirty(ctx: ctx, name: "dirty-after-release") == (dirtyMidDragBase + 1)), detail: "release = one journal entry");

            // (e) UNDO restores the pre-drag position (the placement echo's exact coordinates).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.undo", expect: "[world.undo: dropped 1", name: "undo-drag");

            if (placedAt is not null) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.select placements place-1", expect: placedAt, name: "undo-restores-position");
            }

            // (f) NO CASCADE: removing a creation with a live placement rejects loudly.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.creation.remove probe-stamp",
                expect: "has 1 live placement(s)", name: "remove-referenced-creation-rejects");

            // The no-cascade guard is the round's ONE deliberate refusal.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "no-cascade-round-refused-only-its-one", expected: 1);

            // (g) THE ANIMATED PROBE walks its timeline: stamp the 4-frame critter over its own patch of grass
            // and demand pixel motion between two shots while a critter-free corner band stays at the noise floor.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"editor.import {critterFixture}",
                expect: "[world.mutation: UpsertCreation 'probe-critter' applied]", name: "import-critter-applies");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 12 2 10 0 0", expect: "[editor.cam.pose: seat 1", name: "pose-at-critter-grass");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.place probe-critter",
                expect: "[world.mutation: UpsertPlacement 'place-2' applied]", name: "place-critter-applies");
            Thread.Sleep(millisecondsTimeout: 3400); // toast + shimmer decay: motion below is the timeline alone
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "critter-shot-a", path: critterAPath);
            Thread.Sleep(millisecondsTimeout: 700);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "critter-shot-b", path: critterBPath);

            var critterA = ComposedShotKit.DecodePng(path: critterAPath);
            var critterB = ComposedShotKit.DecodePng(path: critterBPath);
            var critterMotion = MeanAbsDiff(a: critterB, b: critterA, x: bandX, y: bandY, w: bandW, h: bandH);
            var cornerStill = MeanAbsDiff(a: critterB, b: critterA, x: (int)(width * 0.02), y: (int)(height * 0.70), w: (int)(width * 0.15), h: (int)(height * 0.20));

            passed &= ComposedShotKit.Check(
                name: "animated-fixture-walks-timeline",
                ok: ((critterMotion > 0.8) && (critterMotion > (cornerStill * 4.0))),
                detail: $"critter band motion {critterMotion.ToString(format: "F2", provider: ProofApp.Inv)} vs still corner {cornerStill.ToString(format: "F2", provider: ProofApp.Inv)} (want > 0.8 and > 4x the still band)"
            );

            // (i-1) SAVE the furnished world (2 creations, 2 placements) for the ouroboros half below.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.save {savedPath}", expect: "[world.save:", name: "save-furnished-world");

            // (h) CAPACITY HONESTY: flood placements past the reserved headroom — the ceiling line is word-exact,
            // and a further placement leaves the journal unchanged.
            const int floodPlacements = 9;
            var dirtyBeforeFlood = ReadDirty(ctx: ctx, name: "dirty-before-flood");
            var floodMark = ctx.Collector.Count;

            for (var extra = 0; (extra < floodPlacements); extra++) {
                ComposedShotKit.Send(ctx: ctx, line: "editor.place probe-stamp");
                Thread.Sleep(millisecondsTimeout: 250);
            }

            var ceiling = ComposedShotKit.Await(collector: ctx.Collector, mark: floodMark,
                predicate: l => (l.Contains(value: "[world.mutation rejected: UpsertPlacement") && l.Contains(value: "exceed the probed render envelope")), deadlineSeconds: 30.0);

            passed &= ComposedShotKit.Check(name: "flood-hits-envelope-ceiling", ok: (ceiling is not null), detail: (ceiling?.Trim() ?? "(no envelope rejection)"));

            var dirtyAtCeiling = ReadDirty(ctx: ctx, name: "dirty-at-ceiling");

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.place probe-stamp", expect: "exceed the probed render envelope", name: "past-ceiling-rejects-again");
            passed &= ComposedShotKit.Check(name: "past-ceiling-changes-nothing", ok: (ReadDirty(ctx: ctx, name: "dirty-past-ceiling") == dirtyAtCeiling), detail: "journal unchanged past the ceiling");

            // Every placement the envelope turned away is one refusal; the journal counts the ones that landed.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "flood-refused-only-the-over-ceiling-rows",
                expected: ((floodPlacements + 1) - (dirtyAtCeiling - dirtyBeforeFlood)));
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "placements-png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: controlAPath);
            ComposedShotKit.TryDelete(path: controlBPath);
            ComposedShotKit.TryDelete(path: stampPath);
            ComposedShotKit.TryDelete(path: critterAPath);
            ComposedShotKit.TryDelete(path: critterBPath);
            ComposedShotKit.TryDelete(path: stampFixture);
            ComposedShotKit.TryDelete(path: critterFixture);
        }

        // (i-2) THE OUROBOROS WITH CREATIONS: reload the furnished save and save again — byte identity proves the
        // inline-canonical embeds and the world.save hash recompute are stable end to end.
        passed &= RunReloadOuroboros(exe: exe, repoRoot: repoRoot, backend: backend, savedPath: savedPath, resavedPath: resavedPath, exitAfterSeconds: exitAfterSeconds);
        ComposedShotKit.TryDelete(path: savedPath);
        ComposedShotKit.TryDelete(path: resavedPath);

        return passed;
    }

    static bool RunReloadOuroboros(string exe, string repoRoot, string? backend, string savedPath, string resavedPath, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: 640, height: 480, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", savedPath]);
        var process = ctx.Process;
        var passed = true;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: "creations 2 placements 2", name: "reload-carries-creations");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.save {resavedPath}", expect: "[world.save:", name: "resave-furnished-world");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "reload-refused-nothing", expected: 0);
        }
        finally {
            ComposedShotKit.KillQuietly(process: process);
        }

        if (!passed) {
            return false;
        }

        var savedHash = Convert.ToHexStringLower(SHA256.HashData(source: File.ReadAllBytes(path: savedPath)));
        var resavedHash = Convert.ToHexStringLower(SHA256.HashData(source: File.ReadAllBytes(path: resavedPath)));

        return ComposedShotKit.Check(
            name: "creations-ouroboros-byte-stable",
            ok: string.Equals(a: savedHash, b: resavedHash, comparisonType: StringComparison.Ordinal),
            detail: $"sha256 {savedHash[..12]} vs {resavedHash[..12]}"
        );
    }

    // THE STATIC PROBE — a bulky three-primitive beacon authored HERE (Demo content never ships as
    // World content; the proof owns its art). Vector3/Quaternion members use the creation serializer's field shape.
    const string StaticProbeCreationJson = """
        {
          "schema": "puck.creation.v1",
          "name": "probe-stamp",
          "palette": [
            { "albedo": { "x": 0.8, "y": 0.3, "z": 0.2 } },
            { "albedo": { "x": 0.9, "y": 0.7, "z": 0.2 } },
            { "albedo": { "x": 0.2, "y": 0.6, "z": 0.9 }, "emissive": 0.4 },
            { "albedo": { "x": 0.9, "y": 0.9, "z": 0.9 } }
          ],
          "shapes": [
            { "id": 0, "type": "Box", "position": { "x": 0, "y": 0.3, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.6, "y": 0.8, "z": 1.6 }, "material": 1 },
            { "id": 1, "type": "Sphere", "position": { "x": 0, "y": 1.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 2.4, "y": 2.4, "z": 2.4 }, "material": 2 },
            { "id": 2, "type": "RoundCone", "position": { "x": 0, "y": 2.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.2, "y": 1.2, "z": 1.2 }, "material": 3 }
          ]
        }
        """;

    // THE ANIMATED PROBE — a body + two fins with a 4-frame timeline swinging the body: hold-style stepping at the
    // 8-tick cadence lands a visibly different frame across the proof's 700 ms shot gap.
    const string AnimatedProbeCreationJson = """
        {
          "schema": "puck.creation.v1",
          "name": "probe-critter",
          "palette": [
            { "albedo": { "x": 0.2, "y": 0.8, "z": 0.5 } },
            { "albedo": { "x": 0.9, "y": 0.4, "z": 0.7 } }
          ],
          "shapes": [
            { "id": 0, "type": "Sphere", "position": { "x": 0, "y": 1.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 2.0, "y": 2.0, "z": 2.0 }, "material": 0 },
            { "id": 1, "type": "Ellipsoid", "position": { "x": -1.1, "y": 1.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.0, "y": 1.0, "z": 1.0 }, "material": 1 },
            { "id": 2, "type": "Ellipsoid", "position": { "x": 1.1, "y": 1.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.0, "y": 1.0, "z": 1.0 }, "material": 1 }
          ],
          "frames": [
            { "name": "f1", "transforms": [ { "id": 0, "position": { "x": 0.8, "y": 1.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 2.0, "y": 2.0, "z": 2.0 } } ] },
            { "name": "f2", "transforms": [ { "id": 0, "position": { "x": 0, "y": 2.2, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 2.0, "y": 2.0, "z": 2.0 } } ] },
            { "name": "f3", "transforms": [ { "id": 0, "position": { "x": -0.8, "y": 1.6, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 2.0, "y": 2.0, "z": 2.0 } } ] },
            { "name": "f4", "transforms": [ { "id": 0, "position": { "x": 0, "y": 1.0, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 2.0, "y": 2.0, "z": 2.0 } } ] }
          ]
        }
        """;

    // One compact (single-token) WorldCreation JSON row wrapping the fixture's document with a ZEROED hash — the
    // corrupt-hash probe world.creation.set must reject.
    static string BuildCorruptCreationRow(string fixturePath) {
        var document = System.Text.Json.Nodes.JsonNode.Parse(json: File.ReadAllText(path: fixturePath))!;
        var row = new System.Text.Json.Nodes.JsonObject {
            ["id"] = "bad-lamp",
            ["document"] = document,
            ["hash"] = new string(c: '0', count: 64),
        };

        return row.ToJsonString();
    }

    static string ShotPath(int pid, string tag, string name) {
        return Path.Combine(Path.GetTempPath(), $"puck-placements-{tag}-{pid}-{name}.png");
    }

    static double MeanAbsDiff((int Width, int Height, byte[] Rgba) a, (int Width, int Height, byte[] Rgba) b, int x, int y, int w, int h) {
        var sum = 0L;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * a.Width) + col) * 4);

                sum += Math.Abs(value: (a.Rgba[i] - b.Rgba[i]));
                sum += Math.Abs(value: (a.Rgba[(i + 1)] - b.Rgba[(i + 1)]));
                sum += Math.Abs(value: (a.Rgba[(i + 2)] - b.Rgba[(i + 2)]));
            }
        }

        return ((double)sum / ((long)w * h * 3));
    }

    // Sends world.status and parses the journal dirty counter (the stdin barrier makes it settled).
    static int ReadDirty(ComposedShotKit.Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.status");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => DirtyEcho.IsMatch(input: candidate), deadlineSeconds: 15.0);

        if (line is null) {
            _ = ComposedShotKit.Check(name: name, ok: false, detail: "(no world.status dirty echo)");

            return -1;
        }

        var dirty = int.Parse(s: DirtyEcho.Match(input: line).Groups[1].Value, provider: ProofApp.Inv);

        _ = ComposedShotKit.Check(name: name, ok: true, detail: $"dirty {dirty}");

        return dirty;
    }
}

// ============================================================================================
// POPULATION — the crowd-shape proof: the LOOK table (a row's appearance as data), the
// live kit hot-swap (a retune must not teleport a standing body), the SPAWN POLICY (an authored
// policy places future activations where it says), and INHABITATION end to end (a placement's
// inhabit facet claims a body over the loopback link, its kit's attend producer drives that body
// toward the nearest seat, detach frees the slot, undo reconstructs it) plus the two loud
// admission refusals. One windowed session on the default backend; the appearance half is
// asserted in PIXELS (a census/looks read-back alone would pass on a look table nothing renders).
// See the header's subcommand block for the assertion list.
// ============================================================================================
static class PopulationProof {
    static readonly Regex DirtyEcho = new(pattern: @"dirty (\d+) ", options: RegexOptions.Compiled);

    // The pinned stage: seat 1 alone plus a 24-strong idle census, every entity on the `runner` kit. Every count this
    // proof asserts is derived from these two numbers, never inherited from boot (the census boots at 0 and kit
    // assignment is hash policy, so an unpinned run measures a different crowd every time).
    const int Census = 24;
    const int ActiveEntities = (Census + 1);        // the 24 stand-ins plus the one local seat this proof keeps
    const int PeerIndex = 5;                        // player.where's 1-based index for census entry 0 (slot 4)
    // The look table's exact split under `table stocky stocky tiny` over the active entity set: slot 0 (seat 1) and
    // census slots 4..27 take table[slot % 3], so tiny lands on slots 5, 8, ... 26 — eight of them.
    const int StockyCount = 17;
    const int TinyCount = 8;
    // The inhabited body claims the HIGHEST free slot (127 downward), so its player.where index is fixed at 128.
    const int InhabitantIndex = 128;
    const string InhabitedPlacement = "dock-1";
    const string ProbeCreation = "probe-walker";
    // The authored spawn point the `points` policy cycles — far outside the disc the default phyllotaxis policy
    // scatters census slot 0 into, so the landing assertion cannot be satisfied by the policy it replaces.
    const double DockX = 25.0;
    const double DockZ = -18.0;
    const double LandingEpsilon = 0.5;              // u — the points policy carries jitter 0, so the landing is exact
    const double FrozenEpsilon = 0.02;              // u — an unmoved body must not drift at all (2-decimal echo precision)
    // u — the attend producer's convergence over the sampled window. The inhabited body starts ~10.8 u from seat 1 and
    // closes to the kit's 2 u standoff; nothing else can move it (its intent source is Attend, never Wander), so the
    // floor discriminates against a producer that stopped driving, not against ambient drift.
    const double AttendCloseEpsilon = 4.0;

    public static int RunPopulation(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 300, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        var passed = RunSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] population proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool RunSession(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var controlAPath = ShotPath(pid: pid, name: "control-a");
        var controlBPath = ShotPath(pid: pid, name: "control-b");
        var scaledPath = ShotPath(pid: pid, name: "scaled");
        var creationFixture = Path.Combine(Path.GetTempPath(), $"puck-population-{pid}-walker.creation.json");

        File.WriteAllText(path: creationFixture, contents: WalkerProbeCreationJson);

        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= PinStage(ctx: ctx);
            passed &= RunLookRound(ctx: ctx, width: width, height: height, controlAPath: controlAPath, controlBPath: controlBPath, scaledPath: scaledPath);
            passed &= RunKitHotSwapRound(ctx: ctx);
            passed &= RunSpawnPolicyRound(ctx: ctx);
            passed &= RunInhabitRound(ctx: ctx, creationFixture: creationFixture);
            passed &= RunAdmissionRefusalRound(ctx: ctx);

            // Every deliberate refusal above was settled and cleared by its own round. A nonzero count here is a line
            // this suite MEANT to succeed and the wire refused — the failure mode that reads as green everywhere else.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "no-silent-rejections", expected: 0);
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "population-png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: controlAPath);
            ComposedShotKit.TryDelete(path: controlBPath);
            ComposedShotKit.TryDelete(path: scaledPath);
            ComposedShotKit.TryDelete(path: creationFixture);
        }

        return passed;
    }

    // PIN the stage before anything is asserted about it: console panel off (the shots read the world), seat 1 alone
    // (a dev-machine pad auto-seats extra players), every entity on ONE kit, and a fixed idle census. Nothing below
    // inherits a boot value.
    static bool PinStage(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

        for (var seat = 2; (seat <= 4); seat++) {
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-leave-{seat}");
        }

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.kit.assign table runner",
            expect: "[world.mutation: SetKitAssignment 'table' applied]", name: "pin-kit-table");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.population {Census} idle",
            expect: $"[world.population: {Census} network-human stand-ins active", name: "pin-census");

        return passed;
    }

    // (a) THE LOOK TABLE. The census read-back is the bookkeeping half — it must agree with the authored assignment
    // exactly, not approximately — and the pixel band is the behavioral half: a look row nothing renders would pass
    // every count above and change no frame.
    static bool RunLookRound(ComposedShotKit.Ctx ctx, int width, int height, string controlAPath, string controlBPath, string scaledPath) {
        // The implicit single row a world with no `looks` section wears, over the pinned entity set — the baseline the
        // assignment below has to move.
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: "world.looks",
            expect: $"[world.looks: catalog=catalog(index-derived):{ActiveEntities}]", name: "looks-baseline-implicit-row");

        var dirty0 = ReadDirty(ctx: ctx, name: "dirty-before-looks");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.look.set {LookRowJson(name: "stocky", catalogIndex: 42)}",
            expect: "[world.mutation: UpsertLook 'stocky' applied]", name: "look-set-stocky");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.look.set {LookRowJson(name: "tiny", catalogIndex: 7)}",
            expect: "[world.mutation: UpsertLook 'tiny' applied]", name: "look-set-tiny");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.look.assign table stocky stocky tiny",
            expect: "[world.mutation: SetLookAssignment 'table' applied]", name: "look-assign-table");
        passed &= ComposedShotKit.Check(name: "looks-are-three-journal-entries", ok: (ReadDirty(ctx: ctx, name: "dirty-after-looks") == (dirty0 + 3)), detail: "two rows + one assignment = three entries");

        // THE READ-BACK AGREES: the 2:1 cycle over 25 entities is 17/8 — a split the hash policy it replaced does not
        // produce, so the assertion fails if the assignment silently stayed on hash.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.looks",
            expect: $"[world.looks: stocky=catalog(index 42):{StockyCount} tiny=catalog(index 7):{TinyCount}]", name: "looks-census-matches-assignment");

        // THE PIXEL HALF: bound the static noise floor with a control pair (the census is idle and seat 1 is stopped,
        // so the stage holds still), then quadruple the majority row's render scale and demand a decisive repaint.
        Thread.Sleep(millisecondsTimeout: 3400); // toast + shimmer decay: the control pair must read geometry only
        passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "look-control-shot-a", path: controlAPath);
        passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "look-control-shot-b", path: controlBPath);
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.look.tune stocky scale 4",
            expect: "[world.mutation: UpsertLook 'stocky' applied]", name: "look-tune-scale");
        Thread.Sleep(millisecondsTimeout: 3400);
        passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "look-scaled-shot", path: scaledPath);

        var controlA = ComposedShotKit.DecodePng(path: controlAPath);
        var controlB = ComposedShotKit.DecodePng(path: controlBPath);
        var scaled = ComposedShotKit.DecodePng(path: scaledPath);
        // The crowd fills the middle of the frame under the seat-1 chase camera; the band excludes the outer eighth so
        // an edge-of-frame overlay element cannot carry the diff.
        var bandX = (width / 8);
        var bandY = (height / 8);
        var bandW = (width - (width / 4));
        var bandH = (height - (height / 4));
        var noise = MeanAbsDiff(a: controlB, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);
        var scaleDiff = MeanAbsDiff(a: scaled, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);

        passed &= ComposedShotKit.Check(
            name: "look-scale-repaints-the-crowd",
            // The floor is calibrated against a measured REVERT: with the look scale stripped from the render path the
            // band still reads ~2.9 (the assignment's own rig churn), so the absolute floor sits well above that, not at
            // the noise pair's heels.
            ok: ((scaleDiff > 4.0) && (scaleDiff > (noise * 4.0))),
            detail: $"look band diff {scaleDiff.ToString(format: "F2", provider: ProofApp.Inv)} vs noise {noise.ToString(format: "F2", provider: ProofApp.Inv)} (want > 4.0 and > 4x noise)"
        );

        // NO CASCADE: a look row the assignment table still names cannot be removed — loud, and the journal holds.
        var dirtyBeforeRemove = ReadDirty(ctx: ctx, name: "dirty-before-look-remove");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.look.remove stocky",
            expect: "lookAssignment.table[0] 'stocky' names no look row", name: "look-remove-referenced-rejects");
        passed &= ComposedShotKit.Check(name: "look-remove-changes-nothing", ok: (ReadDirty(ctx: ctx, name: "dirty-after-look-remove") == dirtyBeforeRemove), detail: "journal unchanged");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "look-round-refused-only-its-one", expected: 1);

        return passed;
    }

    // (b) THE KIT HOT-SWAP CONTRACT: a live kit retune recompiles a standing body's tuning IN PLACE. The body is
    // warped OFF its spawn footprint first — a body still standing at its spawn would satisfy "pose survives" even if
    // the retune rebuilt it from scratch, which is exactly the vacuous check this premise closes.
    static bool RunKitHotSwapRound(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"player.warp 12 -7 {PeerIndex}", expect: "[player.warp: (12.00, -7.00)]", name: "peer-warped-off-spawn");

        var beforeTune = ReadWhere(ctx: ctx, index: PeerIndex);
        // The premise, measured rather than assumed: the body is nowhere near where a rebuild would put it (census
        // slot 0's phyllotaxis spawn sits within a few units of the origin).
        var spawnDistance = Distance(a: beforeTune, b: Origin);

        passed &= ComposedShotKit.Check(name: "hot-swap-premise-off-spawn", ok: (spawnDistance > 10.0),
            detail: $"p{PeerIndex} {Fmt(pose: beforeTune)} is {spawnDistance.ToString(format: "F2", provider: ProofApp.Inv)} u from the origin (want > 10)");

        var dirtyBefore = ReadDirty(ctx: ctx, name: "dirty-before-kit-tune");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.kit.tune runner moveSpeed 9",
            expect: "[world.mutation: UpsertKit 'runner' applied]", name: "kit-retuned-mid-run");
        passed &= ComposedShotKit.Check(name: "kit-tune-is-one-journal-entry", ok: (ReadDirty(ctx: ctx, name: "dirty-after-kit-tune") == (dirtyBefore + 1)), detail: "retune = one journal entry");

        var afterTune = ReadWhere(ctx: ctx, index: PeerIndex);
        var drift = Distance(a: beforeTune, b: afterTune);

        passed &= ComposedShotKit.Check(name: "hot-swap-preserves-pose", ok: (drift <= FrozenEpsilon),
            detail: $"p{PeerIndex} {Fmt(pose: beforeTune)} -> {Fmt(pose: afterTune)} (delta {drift.ToString(format: "F3", provider: ProofApp.Inv)} u, want <= {FrozenEpsilon})");

        return passed;
    }

    // (c) THE SPAWN POLICY: authored placement of FUTURE activations. Both halves of the verb's contract are asserted
    // — the standing body does not move when the policy changes, and the next activation lands on the named point.
    static bool RunSpawnPolicyRound(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"world.spawns.set {SpawnPointsJson}",
            expect: "[world.mutation: SetSpawns applied]", name: "spawn-point-authored");

        var beforePolicy = ReadWhere(ctx: ctx, index: PeerIndex);

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population.spawn points 0 proof-dock",
            expect: "spawn policy live for future activations, standing bodies unmoved", name: "spawn-policy-applied");

        var afterPolicy = ReadWhere(ctx: ctx, index: PeerIndex);
        var policyDrift = Distance(a: beforePolicy, b: afterPolicy);

        passed &= ComposedShotKit.Check(name: "standing-body-unmoved-by-policy", ok: (policyDrift <= FrozenEpsilon),
            detail: $"p{PeerIndex} {Fmt(pose: beforePolicy)} -> {Fmt(pose: afterPolicy)} (delta {policyDrift.ToString(format: "F3", provider: ProofApp.Inv)} u, want <= {FrozenEpsilon})");

        // Re-activate the census slot: deactivate to zero, then admit one entry again — the ONLY moment the policy is
        // read. The landing is exact (jitter 0) and tens of units from where the phyllotaxis default would place it.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0", expect: "[world.population: 0 network-human stand-ins active", name: "census-drained");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 1 idle", expect: "[world.population: 1 network-human stand-ins active", name: "census-readmits-one");

        var landed = ReadWhere(ctx: ctx, index: PeerIndex);
        var landingError = Distance(a: landed, b: new Pose(X: DockX, Y: 0.0, Z: DockZ, Yaw: 0, Pitch: 0, Roll: 0));

        passed &= ComposedShotKit.Check(name: "activation-lands-on-authored-point", ok: (landingError <= LandingEpsilon),
            detail: $"p{PeerIndex} {Fmt(pose: landed)} vs the authored ({ProofApp.F(value: DockX, format: "0.00")}, 0.00, {ProofApp.F(value: DockZ, format: "0.00")}) — error {landingError.ToString(format: "F3", provider: ProofApp.Inv)} u (want <= {LandingEpsilon})");

        return passed;
    }

    // (d) INHABITATION END TO END: a placement's inhabit facet joins a body over the loopback link, the kit's attend
    // producer drives it, the declared creation face derives a screen, detach frees the slot, and undo reconstructs it.
    static bool RunInhabitRound(ComposedShotKit.Ctx ctx, string creationFixture) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"editor.import {creationFixture}",
            expect: $"[world.mutation: UpsertCreation '{ProbeCreation}' applied]", name: "import-walker-probe");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.set {PlacementJson}",
            expect: $"[world.mutation: UpsertPlacement '{InhabitedPlacement}' applied]", name: "place-walker-probe");

        // The default world declares no attend flavor on any kit, so the facet's Attend source would be rejected
        // outright: give `runner` one first (release 18 > notice 12 >= standoff 2), targeting the nearest seat.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.kit.attend runner 12 18 2 0.8 0.2 face seat",
            expect: "[world.mutation: UpsertKit 'runner' applied]", name: "kit-declares-attend-flavor");

        // The derived-face census: the creation declares one face, so the placement derives one screen slot. The
        // census read-back is what a derived-face regression would silence.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.faces",
            expect: $"{InhabitedPlacement}/visor[screen=", name: "faces-census-names-derived-face");

        var dirtyBeforeFace = ReadDirty(ctx: ctx, name: "dirty-before-face-override");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.face {InhabitedPlacement} visor test",
            expect: "source applies at next boot", name: "face-override-reaches-the-screen-slot");
        passed &= ComposedShotKit.Check(name: "face-override-is-one-journal-entry", ok: (ReadDirty(ctx: ctx, name: "dirty-after-face-override") == (dirtyBeforeFace + 1)), detail: "override = one journal entry");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.face {InhabitedPlacement} no-such-face test",
            expect: $"'no-such-face' names no declared face on creation '{ProbeCreation}'", name: "face-override-unknown-face-rejects");
        passed &= ComposedShotKit.Check(name: "bad-face-changes-nothing", ok: (ReadDirty(ctx: ctx, name: "dirty-after-bad-face") == (dirtyBeforeFace + 1)), detail: "journal unchanged");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "face-round-refused-only-its-one", expected: 1);

        // THE CLAIM: the facet admits one body at the highest free slot (127 -> player index 128).
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.inhabit {InhabitedPlacement} runner attend 1 0",
            expect: $"[world.mutation: UpsertPlacement '{InhabitedPlacement}' applied]", name: "inhabit-facet-applied");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.inhabitants",
            expect: $"{InhabitedPlacement}[creation={ProbeCreation} kit=runner source=Attend body=127", name: "inhabitant-claims-a-body");

        // THE ATTEND PRODUCER DRIVES: the body starts ~10.8 u from seat 1 and closes on the kit's 2 u standoff. Its
        // intent source is Attend, so no wander producer can touch it — the closure is the attend lane's alone.
        var seat = ReadWhere(ctx: ctx, index: 1);
        var beforeAttend = ReadWhere(ctx: ctx, index: InhabitantIndex);

        Thread.Sleep(millisecondsTimeout: 2500);

        var afterAttend = ReadWhere(ctx: ctx, index: InhabitantIndex);
        var openRange = Distance(a: beforeAttend, b: seat);
        var closedRange = Distance(a: afterAttend, b: seat);
        var closed = (openRange - closedRange);

        passed &= ComposedShotKit.Check(name: "attend-producer-closes-on-the-seat", ok: (closed > AttendCloseEpsilon),
            detail: $"p{InhabitantIndex} range to p1 {openRange.ToString(format: "F2", provider: ProofApp.Inv)} -> {closedRange.ToString(format: "F2", provider: ProofApp.Inv)} u (closed {closed.ToString(format: "F2", provider: ProofApp.Inv)}, want > {AttendCloseEpsilon})");

        // DETACH frees the slot; UNDO reconstructs the registration from the journal.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.inhabit {InhabitedPlacement} -",
            expect: $"[world.mutation: UpsertPlacement '{InhabitedPlacement}' applied]", name: "detach-applied");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.inhabitants", expect: "[world.inhabitants: none]", name: "detach-frees-the-slot");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.undo", expect: "[world.undo: dropped 1", name: "undo-drops-the-detach");
        Thread.Sleep(millisecondsTimeout: 500); // the reconcile runs on the tick that installs the restored document
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.inhabitants",
            expect: $"{InhabitedPlacement}[creation={ProbeCreation} kit=runner source=Attend body=127", name: "undo-reconstructs-the-inhabitant");

        return passed;
    }

    // (e) THE TWO LOUD REFUSALS: an inhabit facet naming a kit the world does not declare is a DOCUMENT error (the
    // validator refuses it and the journal holds), while a claim against a genuinely full entity table is a RUNTIME
    // admission failure (the document is valid, so it applies — and the server says loudly that no slot was free).
    static bool RunAdmissionRefusalRound(ComposedShotKit.Ctx ctx) {
        var dirtyBefore = ReadDirty(ctx: ctx, name: "dirty-before-refusals");
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.inhabit {InhabitedPlacement} no-such-kit",
            expect: "inhabit names no kit; the world declares: flyer, swimmer, jumper, runner, kart", name: "inhabit-unresolved-kit-rejects");

        passed &= ComposedShotKit.Check(name: "unresolved-kit-changes-nothing", ok: (ReadDirty(ctx: ctx, name: "dirty-after-unresolved-kit") == dirtyBefore), detail: "journal unchanged");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "unresolved-kit-refused-only-its-one", expected: 1);

        // THE FULL TABLE: retire the inhabitant, fill every peer slot with census stand-ins, and claim again — 124
        // peers plus the local seat leave no free slot for an inhabited body.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.inhabit {InhabitedPlacement} -",
            expect: $"[world.mutation: UpsertPlacement '{InhabitedPlacement}' applied]", name: "retire-before-flood");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 124 idle", expect: "[world.population: 124 network-human stand-ins active", name: "census-fills-the-table", deadlineSeconds: 30.0);
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.placement.inhabit {InhabitedPlacement} runner attend 1 0",
            expect: $"inhabited '{InhabitedPlacement}' has no free entity slot", name: "full-table-claim-rejects-loudly", deadlineSeconds: 30.0);
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.inhabitants", expect: "[world.inhabitants: none]", name: "full-table-claim-admitted-nobody");

        // Drain back to a small census so the closing wire.errors settle is not racing 124 producers.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 1 idle", expect: "[world.population: 1 network-human stand-ins active", name: "census-drained-after-flood", deadlineSeconds: 30.0);

        return passed;
    }

    // One WorldLook row as compact inline JSON — a catalog-pinned rig at unit scale and full gait.
    static string LookRowJson(string name, int catalogIndex) {
        return $"{{\"name\":\"{name}\",\"source\":{{\"$type\":\"catalog\",\"index\":{catalogIndex}}},\"scale\":1.0,\"motion\":{{\"gaitAmplitude\":1.0,\"replayFrames\":false,\"secondsPerFrame\":0.125}}}}";
    }

    static readonly Pose Origin = new(X: 0.0, Y: 0.0, Z: 0.0, Yaw: 0, Pitch: 0, Roll: 0);

    // The four authored seat spawns (order maps slots, so they lead unchanged) plus the far dock the points policy
    // cycles. world.spawns.set replaces the whole list.
    const string SpawnPointsJson =
        "[{\"id\":\"seat-1\",\"position\":[0,0,0]},{\"id\":\"seat-2\",\"position\":[-3,0,2]},{\"id\":\"seat-3\",\"position\":[3,0,2]},{\"id\":\"seat-4\",\"position\":[0,0,4]},{\"id\":\"proof-dock\",\"position\":[25,0,-18]}]";

    // The inhabited placement: 10 u up-Z of the spawn plaza, inside the attend flavor's 12 u notice radius.
    const string PlacementJson =
        "{\"id\":\"dock-1\",\"creationId\":\"probe-walker\",\"position\":[0,0,10],\"yawDegrees\":0,\"scale\":1}";

    // THE WALKER PROBE — a two-primitive figure authored HERE (the proof owns its art), declaring one face so the
    // placement derives a screen slot. Vector3/Quaternion members use the creation serializer's field shape.
    const string WalkerProbeCreationJson = """
        {
          "schema": "puck.creation.v1",
          "name": "probe-walker",
          "palette": [
            { "albedo": { "x": 0.9, "y": 0.5, "z": 0.2 } },
            { "albedo": { "x": 0.2, "y": 0.5, "z": 0.9 }, "emissive": 0.3 }
          ],
          "shapes": [
            { "id": 0, "type": "Box", "position": { "x": 0, "y": 1.0, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.2, "y": 2.0, "z": 1.2 }, "material": 0 },
            { "id": 1, "type": "Sphere", "position": { "x": 0, "y": 2.4, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.4, "y": 1.4, "z": 1.4 }, "material": 1 }
          ],
          "behavior": { "locomotion": "runner", "faces": [ { "name": "visor", "shapeId": 1, "defaultSource": "none" } ] }
        }
        """;

    static Pose? ReadWhere(ComposedShotKit.Ctx ctx, int index) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: $"player.where {index}");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => ProofApp.WhereEcho.IsMatch(input: l), deadlineSeconds: 10.0);

        if (line is null) {
            return null;
        }

        var match = ProofApp.WhereEcho.Match(input: line);

        return new Pose(
            X: double.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv),
            Y: double.Parse(s: match.Groups[3].Value, provider: ProofApp.Inv),
            Z: double.Parse(s: match.Groups[4].Value, provider: ProofApp.Inv),
            Yaw: int.Parse(s: match.Groups[5].Value, provider: ProofApp.Inv),
            Pitch: int.Parse(s: match.Groups[6].Value, provider: ProofApp.Inv),
            Roll: int.Parse(s: match.Groups[7].Value, provider: ProofApp.Inv));
    }

    // Euclidean distance between two samples, or NaN when either read failed — a NaN fails BOTH the "moved beyond
    // epsilon" and "held within epsilon" comparisons, which is the correct verdict for a missing sample.
    static double Distance(Pose? a, Pose? b) {
        if ((a is not { } pa) || (b is not { } pb)) {
            return double.NaN;
        }

        var dx = (pb.X - pa.X);
        var dy = (pb.Y - pa.Y);
        var dz = (pb.Z - pa.Z);

        return Math.Sqrt(d: ((dx * dx) + (dy * dy) + (dz * dz)));
    }

    static string Fmt(Pose? pose) {
        return (pose is { } p ? $"({ProofApp.F(value: p.X, format: "0.00")}, {ProofApp.F(value: p.Y, format: "0.00")}, {ProofApp.F(value: p.Z, format: "0.00")})" : "(?)");
    }

    static string ShotPath(int pid, string name) {
        return Path.Combine(Path.GetTempPath(), $"puck-population-{pid}-{name}.png");
    }

    static double MeanAbsDiff((int Width, int Height, byte[] Rgba) a, (int Width, int Height, byte[] Rgba) b, int x, int y, int w, int h) {
        var sum = 0L;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * a.Width) + col) * 4);

                sum += Math.Abs(value: (a.Rgba[i] - b.Rgba[i]));
                sum += Math.Abs(value: (a.Rgba[(i + 1)] - b.Rgba[(i + 1)]));
                sum += Math.Abs(value: (a.Rgba[(i + 2)] - b.Rgba[(i + 2)]));
            }
        }

        return ((double)sum / ((long)w * h * 3));
    }

    // Sends world.status and parses the journal dirty counter (the stdin barrier makes it settled).
    static int ReadDirty(ComposedShotKit.Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.status");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => DirtyEcho.IsMatch(input: candidate), deadlineSeconds: 15.0);

        if (line is null) {
            _ = ComposedShotKit.Check(name: name, ok: false, detail: "(no world.status dirty echo)");

            return -1;
        }

        var dirty = int.Parse(s: DirtyEcho.Match(input: line).Groups[1].Value, provider: ProofApp.Inv);

        _ = ComposedShotKit.Check(name: name, ok: true, detail: $"dirty {dirty}");

        return dirty;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// sculpt — the creation sub-editor proof, on BOTH backends: sculpt a creation from NOTHING over stdin
// (editor.sculpt.* — primitives, palette, a chain/IK pose), commit it as ONE canonicalized UpsertCreation, stamp it
// at the exact bench origin, and demand PIXEL IDENTITY between the workbench preview and the committed stamp (the
// same document at the same transform through the same emission path renders the same pixels — the
// stamp-equals-preview contract). Then the two undo domains (local ring vs world journal) assert as distinct, a
// second sculpt authors a 2-frame timeline whose stamp ANIMATES, re-sculpting it live-refreshes the placement
// (recreate on a palette change — still animating; release when the frames delete — motion stops), an imported
// carrier's cameras/behavior/extensions members survive a model round-trip, the easel
// verb wires a bench camera onto a screen row, and the furnished save reloads byte-stably. DISPOSABLE probes only.
static class SculptProof {
    static readonly Regex DirtyEcho = new(pattern: @"dirty (\d+) ", options: RegexOptions.Compiled);
    static readonly Regex HashEcho = new(pattern: @"sha256 ([0-9a-f]{12})", options: RegexOptions.Compiled);

    public static int RunSculpt(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 300, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        Console.WriteLine(value: "[proof] === sculpt (a): Direct3D 12 (the default backend) ===");
        var directXPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === sculpt (b): Vulkan ===");
        var vulkanPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        var passed = (directXPassed && vulkanPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] sculpt proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var pid = Environment.ProcessId;
        var tag = ((backend ?? "directx") + "-sculpt");
        var controlAPath = ShotPath(pid: pid, tag: tag, name: "control-a");
        var controlBPath = ShotPath(pid: pid, tag: tag, name: "control-b");
        var previewPath = ShotPath(pid: pid, tag: tag, name: "preview");
        var stampPath = ShotPath(pid: pid, tag: tag, name: "stamp");
        var motionAPath = ShotPath(pid: pid, tag: tag, name: "motion-a");
        var motionBPath = ShotPath(pid: pid, tag: tag, name: "motion-b");
        var recolorAPath = ShotPath(pid: pid, tag: tag, name: "recolor-a");
        var recolorBPath = ShotPath(pid: pid, tag: tag, name: "recolor-b");
        var stillAPath = ShotPath(pid: pid, tag: tag, name: "still-a");
        var stillBPath = ShotPath(pid: pid, tag: tag, name: "still-b");
        var savedPath = Path.Combine(Path.GetTempPath(), $"puck-sculpt-{tag}-{pid}-1.world.json");
        var resavedPath = Path.Combine(Path.GetTempPath(), $"puck-sculpt-{tag}-{pid}-2.world.json");
        // The carrier fixture is proof-authored (no external content enters a World proof): it exists
        // ONLY to prove the cameras/behavior/extensions members survive a sculpt round-trip.
        var carrierFixture = Path.Combine(Path.GetTempPath(), $"puck-sculpt-{tag}-{pid}-carrier.creation.json");

        File.WriteAllText(path: carrierFixture, contents: CarrierCreationJson);
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Pin the stage: console panel off, roster to seat 1, zero census, and the editor camera at a fixed
            // vantage over empty grass BEFORE the bench opens (the workbench orbit seeds from this pose, so the
            // preview/stamp shots share one deterministic camera).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

            for (var seat = 2; (seat <= 4); seat++) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-leave-{seat}");
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0", expect: "[world.population:", name: "census-zero");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter", expect: "[editor.enter: seat 1 editing", name: "enter-editor");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 0 2 10 0 0", expect: "[editor.cam.pose: seat 1", name: "pose-at-grass");

            var dirty0 = ReadDirty(ctx: ctx, name: "dirty-baseline");

            // (a) SCULPT FROM NOTHING: open the bench on empty grass, control-pair the empty bench (an empty model
            // stamps no geometry), then build a small beacon — primitives, blend, palette, and a posed 2-bone limb.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.new probe-sculpt 0 0 16",
                expect: "[editor.sculpt.new: seat 1 sculpting 'probe-sculpt'", name: "bench-opens");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.status", expect: "group=sculpt page=sculpt 'Sculpt'", name: "bar-flips-to-sculpt-group");
            Thread.Sleep(millisecondsTimeout: 2200); // let the entry toast decay before the control pair
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "control-shot-a", path: controlAPath);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "control-shot-b", path: controlBPath);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.add box", expect: "shape 1 (Box)", name: "add-box");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.scale 1.8 0.7 1.8", expect: "scale=(1.80, 0.70, 1.80)", name: "scale-box");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.add sphere 0 1.7 0", expect: "shape 2 (Sphere)", name: "add-sphere");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.scale 2.2", expect: "scale=(2.20, 2.20, 2.20)", name: "scale-sphere");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.blend smoothunion", expect: "SmoothUnion", name: "blend-sphere");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.smooth 0.3", expect: "0.30", name: "smooth-sphere");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.palette 0 0.92 0.30 0.18", expect: "slot 0 rgb=(0.92, 0.30, 0.18)", name: "palette-0");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.palette 1 0.20 0.55 0.95 0.4", expect: "slot 1 rgb=(0.20, 0.55, 0.95)", name: "palette-1");
            // The limb: three capsules off to the side, chained root-to-tip and POSED through the analytic solver
            // (the solved transforms land in ordinary shapes, so the commit carries the pose).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.add capsule 2.2 0.4 0", expect: "shape 3 (Capsule)", name: "add-limb-root");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.add capsule 2.2 1.4 0", expect: "shape 4 (Capsule)", name: "add-limb-mid");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.add capsule 2.2 2.4 0", expect: "shape 5 (Capsule)", name: "add-limb-tip");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.chain arm 3 4 5 limb", expect: "chain 1 'arm' (limb, 3 shapes)", name: "define-limb");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.goal arm 3.4 1.2 0.6", expect: "goal=(3.40, 1.20, 0.60) — pose re-solved", name: "pose-goal");

            // (b) THE TWO UNDO DOMAINS, mid-sculpt: a nudge + local undo restores the exact position while the world
            // journal never moves; world.undo remains the journal's verb (asserted after commit below).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.select 2", expect: "shape 2 (Sphere) at (0.00, 1.70, 0.00)", name: "reselect-sphere");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.nudge 0.9 0 0", expect: "at (0.90, 1.70, 0.00)", name: "nudge-sphere");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.undo", expect: "local ring — restored", name: "local-undo-restores");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.select 2", expect: "shape 2 (Sphere) at (0.00, 1.70, 0.00)", name: "local-undo-exact-position");
            passed &= ComposedShotKit.Check(name: "local-ring-never-touches-journal", ok: (ReadDirty(ctx: ctx, name: "dirty-mid-sculpt") == dirty0), detail: "dirty unchanged across sculpt edits + local undo");

            // (c) THE PREVIEW in pixels, then COMMIT: one canonicalized UpsertCreation with the hash echo.
            Thread.Sleep(millisecondsTimeout: 3400); // let toasts decay: the preview shot reads geometry only
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "preview-shot", path: previewPath);

            var commitMark = ctx.Collector.Count;

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.commit",
                expect: "[world.mutation: UpsertCreation 'probe-sculpt' applied]", name: "commit-applies");

            var commitEcho = ComposedShotKit.Await(collector: ctx.Collector, mark: commitMark,
                predicate: l => (l.Contains(value: "[editor.sculpt.commit: seat 1 'probe-sculpt'") && HashEcho.IsMatch(input: l)), deadlineSeconds: 15.0);
            var commitHash = ((commitEcho is not null) ? HashEcho.Match(input: commitEcho).Groups[1].Value : null);

            passed &= ComposedShotKit.Check(name: "commit-echoes-canonical-hash", ok: (commitHash is not null), detail: (commitHash ?? "(no hash echo)"));
            passed &= ComposedShotKit.Check(name: "commit-is-one-journal-entry", ok: (ReadDirty(ctx: ctx, name: "dirty-after-commit") == (dirty0 + 1)), detail: "commit = one journal entry");

            // The world's creation catalog pins the SAME canonical hash and stamp cost the commit echoed.
            if (commitHash is not null) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.creations", expect: $"probe-sculpt: 5 stamp shapes sha256 {commitHash}", name: "catalog-pins-commit-hash");
            }

            // (d) STAMP = PREVIEW, byte-for-byte in pixels: close the bench (the preview vanishes; the camera holds
            // its trailing pose), stamp the committed row at the EXACT bench transform, and demand the stamp shot
            // repaint the preview shot's pixels to the noise floor — same document, same transform, same emission.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.exit", expect: "closed 'probe-sculpt'", name: "bench-closes");
            passed &= ComposedShotKit.SendAwait(ctx: ctx,
                line: """world.placement.set {"id":"place-sculpt","creationId":"probe-sculpt","position":[0,0,16],"yawDegrees":0,"scale":1}""",
                expect: "[world.mutation: UpsertPlacement 'place-sculpt' applied]", name: "stamp-at-bench-origin");
            Thread.Sleep(millisecondsTimeout: 3400); // change shimmer + toast decay
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "stamp-shot", path: stampPath);

            var controlA = ComposedShotKit.DecodePng(path: controlAPath);
            var controlB = ComposedShotKit.DecodePng(path: controlBPath);
            var preview = ComposedShotKit.DecodePng(path: previewPath);
            var stamp = ComposedShotKit.DecodePng(path: stampPath);
            // The identity band: center-right, clear of the editor HUD (top-left), the binding bar (bottom), the
            // toast strip (top edge), AND the default world's capture-fed screen slab (a LIVE desktop feed at the
            // frame's right-middle under this vantage — measured at x 0.68..0.76, y 0.50..0.72 — which would leak
            // real-time desktop change into a determinism band). The sculpt fills the frame center under the
            // pivot-locked orbit camera.
            var bandX = (int)(width * 0.46);
            var bandY = (int)(height * 0.15);
            var bandW = (int)(width * 0.28);
            var bandH = (int)(height * 0.27);
            var noise = MeanAbsDiff(a: controlB, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);
            var previewDiff = MeanAbsDiff(a: preview, b: controlA, x: bandX, y: bandY, w: bandW, h: bandH);
            var identityDiff = MeanAbsDiff(a: stamp, b: preview, x: bandX, y: bandY, w: bandW, h: bandH);

            passed &= ComposedShotKit.Check(
                name: "preview-visible",
                ok: ((previewDiff > 0.8) && (previewDiff > (noise * 4.0))),
                detail: $"preview band diff {previewDiff.ToString(format: "F2", provider: ProofApp.Inv)} vs noise {noise.ToString(format: "F2", provider: ProofApp.Inv)} (want > 0.8 and > 4x noise)"
            );
            passed &= ComposedShotKit.Check(
                name: "stamp-equals-preview-pixels",
                // The stamp must repaint the preview's band at the static noise floor: a generous absolute ceiling
                // plus a relative guard against the preview's own repaint magnitude.
                ok: ((identityDiff < Math.Max(val1: (noise * 4.0), val2: 0.45)) && (identityDiff < (previewDiff * 0.10))),
                detail: $"stamp-vs-preview band diff {identityDiff.ToString(format: "F2", provider: ProofApp.Inv)} (noise {noise.ToString(format: "F2", provider: ProofApp.Inv)}, preview repaint {previewDiff.ToString(format: "F2", provider: ProofApp.Inv)})"
            );

            // Post-commit undo is the JOURNAL's domain: world.undo drops the stamp placement (the last entry).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.undo", expect: "[world.undo: dropped 1", name: "world-undo-is-journal-domain");
            passed &= ComposedShotKit.SendAwait(ctx: ctx,
                line: """world.placement.set {"id":"place-sculpt","creationId":"probe-sculpt","position":[0,0,16],"yawDegrees":0,"scale":1}""",
                expect: "[world.mutation: UpsertPlacement 'place-sculpt' applied]", name: "restamp-after-undo");

            // (e) A FRAMED TIMELINE sculpted from nothing (four vertical-bounce holds — four frames is the
            // robust shot scheme at the FIXED 8-tick replay cadence: a ~700 ms shot gap crosses 5..7 holds, and any
            // of those counts mod 4 is nonzero, so the second shot ALWAYS lands a different frame index; a
            // 2-frame loop can land the same frame on an even crossing count).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.cam.pose 12 2 10 0 0", expect: "[editor.cam.pose: seat 1", name: "pose-at-motion-grass");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.new probe-motion 12 0 16",
                expect: "[editor.sculpt.new: seat 1 sculpting 'probe-motion'", name: "motion-bench-opens");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.add sphere 0 1.6 0", expect: "shape 1 (Sphere)", name: "motion-add-body");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.scale 2.4", expect: "scale=(2.40, 2.40, 2.40)", name: "motion-scale-body");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.record", expect: "frame 1/1 recorded", name: "record-f1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame 0", expect: "frame 0/1 (rest)", name: "back-to-rest-f2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.move 0 2.5 0", expect: "at (0.00, 2.50, 0.00)", name: "pose-f2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.record", expect: "frame 2/2 recorded", name: "record-f2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame 0", expect: "frame 0/2 (rest)", name: "back-to-rest-f3");
            // Every frame pose is DISTINCT (a repeated pose would let a frame-delta that maps it onto its twin
            // render two identical shots and fake a stall).
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.move 0 3.2 0", expect: "at (0.00, 3.20, 0.00)", name: "pose-f3");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.record", expect: "frame 3/3 recorded", name: "record-f3");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame 0", expect: "frame 0/3 (rest)", name: "back-to-rest-f4");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.move 0 0.9 0", expect: "at (0.00, 0.90, 0.00)", name: "pose-f4");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.record", expect: "frame 4/4 recorded", name: "record-f4");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.commit",
                expect: "[world.mutation: UpsertCreation 'probe-motion' applied]", name: "motion-commit-applies");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.exit", expect: "closed 'probe-motion'", name: "motion-bench-closes");
            passed &= ComposedShotKit.SendAwait(ctx: ctx,
                line: """world.placement.set {"id":"place-motion","creationId":"probe-motion","position":[12,0,16],"yawDegrees":0,"scale":1}""",
                expect: "[world.mutation: UpsertPlacement 'place-motion' applied]", name: "stamp-motion");
            Thread.Sleep(millisecondsTimeout: 3400);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "motion-shot-a", path: motionAPath);
            Thread.Sleep(millisecondsTimeout: 700); // crosses the 8-tick hold cadence: a different frame lands
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "motion-shot-b", path: motionBPath);

            var motionA = ComposedShotKit.DecodePng(path: motionAPath);
            var motionB = ComposedShotKit.DecodePng(path: motionBPath);
            var motion = MeanAbsDiff(a: motionB, b: motionA, x: bandX, y: bandY, w: bandW, h: bandH);
            var cornerStill = MeanAbsDiff(a: motionB, b: motionA, x: (int)(width * 0.02), y: (int)(height * 0.70), w: (int)(width * 0.15), h: (int)(height * 0.20));

            passed &= ComposedShotKit.Check(
                name: "sculpted-timeline-animates",
                ok: ((motion > 0.8) && (motion > (cornerStill * 4.0))),
                detail: $"motion band {motion.ToString(format: "F2", provider: ProofApp.Inv)} vs still corner {cornerStill.ToString(format: "F2", provider: ProofApp.Inv)} (want > 0.8 and > 4x)"
            );

            // (f) LIVE REFRESH of the animated placement, twice over: a re-sculpted PALETTE (a content/hash change —
            // the animator recreates the replay; the stamp keeps animating in its new skin), then FRAME DELETION
            // (the row goes static — the replay releases and the motion STOPS). Both land through ordinary commits.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.edit probe-motion 12 0 16",
                expect: "[editor.sculpt.edit: seat 1 sculpting 'probe-motion'", name: "reopen-motion");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.palette 0 0.95 0.90 0.10", expect: "slot 0 rgb=(0.95, 0.90, 0.10)", name: "recolor-body");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.commit",
                expect: "[world.mutation: UpsertCreation 'probe-motion' applied]", name: "recolor-commit-applies");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.exit", expect: "closed 'probe-motion'", name: "recolor-bench-closes");
            Thread.Sleep(millisecondsTimeout: 3400);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "recolor-shot-a", path: recolorAPath);
            Thread.Sleep(millisecondsTimeout: 700);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "recolor-shot-b", path: recolorBPath);

            var recolorA = ComposedShotKit.DecodePng(path: recolorAPath);
            var recolorB = ComposedShotKit.DecodePng(path: recolorBPath);
            var recolorMotion = MeanAbsDiff(a: recolorB, b: recolorA, x: bandX, y: bandY, w: bandW, h: bandH);

            passed &= ComposedShotKit.Check(
                name: "recreated-replay-still-animates",
                ok: ((recolorMotion > 0.8) && (recolorMotion > (cornerStill * 4.0))),
                detail: $"post-recolor motion band {recolorMotion.ToString(format: "F2", provider: ProofApp.Inv)} (want > 0.8 — the hash-diff recreate kept the timeline live)"
            );

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.edit probe-motion 12 0 16",
                expect: "[editor.sculpt.edit: seat 1 sculpting 'probe-motion'", name: "reopen-motion-again");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame 1", expect: "frame 1/4", name: "cursor-f1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.remove", expect: "frame removed — 3 left", name: "delete-f1");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.remove", expect: "frame removed — 2 left", name: "delete-f2");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.remove", expect: "frame removed — 1 left", name: "delete-f3");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.frame.remove", expect: "frame removed — 0 left", name: "delete-f4");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.commit",
                expect: "[world.mutation: UpsertCreation 'probe-motion' applied]", name: "static-commit-applies");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.exit", expect: "closed 'probe-motion'", name: "static-bench-closes");
            Thread.Sleep(millisecondsTimeout: 3400);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "still-shot-a", path: stillAPath);
            Thread.Sleep(millisecondsTimeout: 700);
            passed &= ComposedShotKit.Screenshot(ctx: ctx, name: "still-shot-b", path: stillBPath);

            var stillA = ComposedShotKit.DecodePng(path: stillAPath);
            var stillB = ComposedShotKit.DecodePng(path: stillBPath);
            var stillness = MeanAbsDiff(a: stillB, b: stillA, x: bandX, y: bandY, w: bandW, h: bandH);

            passed &= ComposedShotKit.Check(
                name: "released-replay-stops-moving",
                ok: ((stillness < Math.Max(val1: (noise * 4.0), val2: 0.45)) && ((motion <= 0) || (stillness < (motion * 0.10)))),
                detail: $"post-release stillness {stillness.ToString(format: "F2", provider: ProofApp.Inv)} vs prior motion {motion.ToString(format: "F2", provider: ProofApp.Inv)} — the frames-deleted commit released the replay live"
            );

            // (g) THE CARRIER: cameras/behavior/extensions members survive a full sculpt
            // round-trip — import, re-sculpt one nudge, commit; the saved world must still carry them verbatim.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"editor.import {carrierFixture}",
                expect: "[world.mutation: UpsertCreation 'probe-carrier' applied]", name: "import-carrier");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.edit probe-carrier 20 0 16",
                expect: "[editor.sculpt.edit: seat 1 sculpting 'probe-carrier'", name: "carrier-bench-opens");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.select 1", expect: "shape 1", name: "carrier-select");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.nudge 0.3 0 0", expect: "at (0.60, 0.90, 0.00)", name: "carrier-nudge");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.commit",
                expect: "[world.mutation: UpsertCreation 'probe-carrier' applied]", name: "carrier-commit-applies");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.exit", expect: "closed 'probe-carrier'", name: "carrier-bench-closes");

            // (h) THE EASEL: a bench camera + an existing screen row re-pointed at its view — the first composed
            // diegetic surface. Asserted over the live reconcile echo, the same mechanism editor-cameras proves; a
            // fresh bench is opened for it and closed after.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.edit probe-sculpt 0 0 16",
                expect: "[editor.sculpt.edit: seat 1 sculpting 'probe-sculpt'", name: "easel-bench-opens");

            var easelMark = ctx.Collector.Count;

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.easel",
                expect: "camera 'easel-1' + screen 0 re-pointed", name: "easel-authors");

            var easelBound = ComposedShotKit.Await(collector: ctx.Collector, mark: easelMark,
                predicate: l => l.Contains(value: "[world.screen: screen 0 showing camera 'easel-1']"), deadlineSeconds: 15.0);

            passed &= ComposedShotKit.Check(name: "easel-screen-binds-live", ok: (easelBound is not null), detail: (easelBound?.Trim() ?? "(no live screen bind echo)"));
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.screens", expect: "0 view bound", name: "easel-screen-listed");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.sculpt.exit", expect: "closed 'probe-sculpt'", name: "easel-bench-closes");

            // (i) SAVE the furnished world for the ouroboros; the carrier's opaque members must persist verbatim.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.save {savedPath}", expect: "[world.save:", name: "save-sculpted-world");

            var savedJson = File.ReadAllText(path: savedPath);

            passed &= ComposedShotKit.Check(name: "carried-cameras-persist", ok: savedJson.Contains(value: "\"cameras\""), detail: "saved world carries the creation's cameras member");
            passed &= ComposedShotKit.Check(name: "carried-behavior-persists", ok: savedJson.Contains(value: "\"swim\""), detail: "saved world carries the swim locomotion");
            passed &= ComposedShotKit.Check(name: "carried-extension-persists", ok: savedJson.Contains(value: "proofExtension"), detail: "saved world carries the unknown extension member");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "sculpt-session-refused-nothing", expected: 0);
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        catch (InvalidDataException exception) {
            passed = ComposedShotKit.Check(name: "sculpt-png-decode", ok: false, detail: exception.Message);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
            ComposedShotKit.TryDelete(path: controlAPath);
            ComposedShotKit.TryDelete(path: controlBPath);
            ComposedShotKit.TryDelete(path: previewPath);
            ComposedShotKit.TryDelete(path: stampPath);
            ComposedShotKit.TryDelete(path: motionAPath);
            ComposedShotKit.TryDelete(path: motionBPath);
            ComposedShotKit.TryDelete(path: recolorAPath);
            ComposedShotKit.TryDelete(path: recolorBPath);
            ComposedShotKit.TryDelete(path: stillAPath);
            ComposedShotKit.TryDelete(path: stillBPath);
            ComposedShotKit.TryDelete(path: carrierFixture);
        }

        // (j) THE OUROBOROS: reload the sculpted save and save again — byte identity proves the sculpted embeds
        // (posed chains, carried members, the recomputed hash pins) are stable end to end.
        passed &= RunReloadOuroboros(exe: exe, repoRoot: repoRoot, backend: backend, savedPath: savedPath, resavedPath: resavedPath, exitAfterSeconds: exitAfterSeconds);
        ComposedShotKit.TryDelete(path: savedPath);
        ComposedShotKit.TryDelete(path: resavedPath);

        return passed;
    }

    static bool RunReloadOuroboros(string exe, string repoRoot, string? backend, string savedPath, string resavedPath, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: 640, height: 480, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", savedPath]);
        var process = ctx.Process;
        var passed = true;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: "creations 3 placements 2", name: "reload-carries-sculpted-rows");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.save {resavedPath}", expect: "[world.save:", name: "resave-sculpted-world");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "sculpt-reload-refused-nothing", expected: 0);
        }
        finally {
            ComposedShotKit.KillQuietly(process: process);
        }

        if (!passed) {
            return false;
        }

        var savedHash = Convert.ToHexStringLower(SHA256.HashData(source: File.ReadAllBytes(path: savedPath)));
        var resavedHash = Convert.ToHexStringLower(SHA256.HashData(source: File.ReadAllBytes(path: resavedPath)));

        return ComposedShotKit.Check(
            name: "sculpt-ouroboros-byte-stable",
            ok: string.Equals(a: savedHash, b: resavedHash, comparisonType: StringComparison.Ordinal),
            detail: $"sha256 {savedHash[..12]} vs {resavedHash[..12]}"
        );
    }

    // THE CARRIER — two shapes plus the members the sculpt model must carry verbatim: an
    // anchored camera eye, a swim/face behavior manifest, and an unknown extension section. Proof-authored.
    const string CarrierCreationJson = """
        {
          "schema": "puck.creation.v1",
          "name": "probe-carrier",
          "shapes": [
            { "id": 1, "type": "Ellipsoid", "position": { "x": 0.3, "y": 0.9, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 1.6, "y": 1.0, "z": 1.0 }, "material": 0 },
            { "id": 2, "type": "Sphere", "position": { "x": 1.1, "y": 0.9, "z": 0 }, "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }, "scale": { "x": 0.6, "y": 0.6, "z": 0.6 }, "material": 1 }
          ],
          "cameras": [
            { "id": 1, "shapeId": 2, "position": { "x": 0.2, "y": 0.1, "z": 0 }, "yaw": 15, "fov": 70, "feed": "carrier-eye" }
          ],
          "behavior": { "locomotion": "swim", "faces": [ { "name": "face", "shapeId": 1, "defaultSource": "named:emotes" } ] },
          "proofExtension": { "keeper": "round-trip witness" }
        }
        """;

    static string ShotPath(int pid, string tag, string name) {
        return Path.Combine(Path.GetTempPath(), $"puck-sculpt-{tag}-{pid}-{name}.png");
    }

    static double MeanAbsDiff((int Width, int Height, byte[] Rgba) a, (int Width, int Height, byte[] Rgba) b, int x, int y, int w, int h) {
        var sum = 0L;

        for (var row = y; (row < (y + h)); row++) {
            for (var col = x; (col < (x + w)); col++) {
                var i = (((row * a.Width) + col) * 4);

                sum += Math.Abs(value: (a.Rgba[i] - b.Rgba[i]));
                sum += Math.Abs(value: (a.Rgba[(i + 1)] - b.Rgba[(i + 1)]));
                sum += Math.Abs(value: (a.Rgba[(i + 2)] - b.Rgba[(i + 2)]));
            }
        }

        return ((double)sum / ((long)w * h * 3));
    }

    // Sends world.status and parses the journal dirty counter (the stdin barrier makes it settled).
    static int ReadDirty(ComposedShotKit.Ctx ctx, string name) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.status");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => DirtyEcho.IsMatch(input: candidate), deadlineSeconds: 15.0);

        if (line is null) {
            _ = ComposedShotKit.Check(name: name, ok: false, detail: "(no world.status dirty echo)");

            return -1;
        }

        var dirty = int.Parse(s: DirtyEcho.Match(input: line).Groups[1].Value, provider: ProofApp.Inv);

        _ = ComposedShotKit.Check(name: name, ok: true, detail: $"dirty {dirty}");

        return dirty;
    }
}

// ============================================================================================
// AUDIO — the audio document-side proof: the audio sections (speakers/tunes/patches/audio),
// emission facets, creation sounds, the hash pins, the validator rejection table, the
// no-cascade guards, undo/grants rounds, the audio.emitters derivation listing, and the
// ouroboros with audio sections. Session/console machinery lives in ComposedShotKit.
// ============================================================================================
static class AudioProof {
    static readonly Regex CanonicalSha = new(options: RegexOptions.Compiled, pattern: @"canonical sha256 '([0-9a-f]{64})'");
    static readonly Regex DirtyEcho = new(options: RegexOptions.Compiled, pattern: @"\[world\.status:.*dirty (\d+) undoable");

    const string ChirpDoc = """{"schema":"puck.synth.v1","oscillator":"Pulse","pitchMillihertz":1320000,"durationFrames":9600}""";
    const string DroneDoc = """{"schema":"puck.synth.v1","oscillator":"Noise","polynomial":40,"pitchMillihertz":1000}""";
    const string JingleDoc = """{"schema":"puck.audio.v1","name":"jingle"}""";
    const string StatueDoc = """{"schema":"puck.creation.v1","name":"statue","shapes":[{"id":1,"type":"Sphere","position":{"x":0,"y":0.6,"z":0},"rotation":{"x":0,"y":0,"z":0,"w":1},"scale":{"x":1,"y":1,"z":1}}],"behavior":{"locomotion":"hover","sounds":[{"name":"hum","shapeId":1,"patch":{"schema":"puck.synth.v1","oscillator":"Sine","pitchMillihertz":220000},"level":1,"radius":6}]}}""";

    // THE CUE TABLE: the full audio-defaults row re-asserted with cue rows — drone everywhere (a looping
    // patch takes the 2 s transient cap, a reliable polling window), covering all three placements. Compact JSON
    // (the console tokenizer rule).
    const string CueTableLine =
        """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1","cues":[{"event":"mutation.applied","patchId":"drone","gainThousandths":800,"placement":"at-site"},{"event":"grant.denied","patchId":"drone","placement":"listener"},{"event":"player.footstep","patchId":"drone","gainThousandths":500,"placement":"at-site"},{"event":"screen.fault","patchId":"drone","placement":"listener"},{"event":"screen.boot","patchId":"drone","placement":"emitter:left"}]}""";
    // The (b) boulder row resubmitted VERBATIM: the applied echo fires the mutation.applied cue while the document
    // stays byte-identical (the later save/pin sees no drift from this round).
    const string BoulderResubmitLine =
        """world.scene.row.set {"$type":"boulder","id":"boulder-1","center":[-1.2,0.72,-0.3],"emission":{"patchId":"chirp","level":1,"radius":8},"radius":0.9,"smooth":0.5}""";

    // The fresh-boot derivation pin (session B): stable ids in document order — the whole listing, byte-for-byte.
    const string ExpectedEmittersAfterReboot =
        "[audio.emitters: 1 speaker:left point tune:jingle left gain=1 min=0 max=8" +
        " | 2 speaker:right point tune:jingle right gain=1 min=0 max=8" +
        " | 3 speaker:wind bed synth:drone mix gain=0.7 min=2 max=5" +
        " | 4 speaker:hand-bell point synth:chirp mix gain=1 min=0 max=8" +
        " | 5 speaker:statue-voice point synth:chirp mix gain=1 min=0 max=8" +
        " | 6 scene:boulder-1 point synth:chirp mix gain=1 min=0 max=8" +
        " | 7 placement:statue-1 point synth:drone mix gain=0.5 min=0 max=8" +
        " | 8 sound:statue-1:hum point synth:sound:statue-1:hum mix gain=1 min=0 max=6]";

    public static int RunAudio(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 240, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 2;
        }

        var pid = Environment.ProcessId;
        var savePath = Path.Combine(Path.GetTempPath(), $"puck-world-audio-{pid}.world.json");
        var resavePath = Path.Combine(Path.GetTempPath(), $"puck-world-audio-resave-{pid}.world.json");
        var passed = true;

        try {
            passed &= SessionA(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, savePath: savePath);
            passed &= (File.Exists(path: savePath)
                ? SessionB(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, savePath: savePath, resavePath: resavePath)
                : ComposedShotKit.Check(name: "session-b", ok: false, detail: $"{savePath} was never written"));
        }
        finally {
            ComposedShotKit.TryDelete(path: savePath);
            ComposedShotKit.TryDelete(path: resavePath);
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] audio proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool SessionA(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string savePath) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: ctx.Process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: ctx.Process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            Console.WriteLine(value: "[proof] === audio (a): the hash-pin handshake — a bogus pin rejects loudly naming the canonical sha256 ===");

            var chirpHash = ProbeCanonicalHash(ctx: ctx, name: "patch-pin-rejects", line: WithHash(verb: "world.patch.set", id: "chirp", doc: ChirpDoc, hash: "0"), rejectNeedle: "UpsertPatch 'chirp'");
            var droneHash = ProbeCanonicalHash(ctx: ctx, name: "patch-pin-rejects-drone", line: WithHash(verb: "world.patch.set", id: "drone", doc: DroneDoc, hash: "0"), rejectNeedle: "UpsertPatch 'drone'");
            var jingleHash = ProbeCanonicalHash(ctx: ctx, name: "tune-pin-rejects", line: WithHash(verb: "world.tune.set", id: "jingle", doc: JingleDoc, hash: "0"), rejectNeedle: "UpsertTune 'jingle'");
            var statueHash = ProbeCanonicalHash(ctx: ctx, name: "creation-pin-rejects", line: WithHash(verb: "world.creation.set", id: "statue", doc: StatueDoc, hash: "0"), rejectNeedle: "UpsertCreation 'statue'");

            passed &= ((chirpHash is not null) && (droneHash is not null) && (jingleHash is not null) && (statueHash is not null));
            passed &= ExpectDirty(ctx: ctx, name: "pins-changed-nothing", dirty: 0);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "pin-round-refused-only-its-four", expected: 4);

            if (!passed) {
                return false;
            }

            Console.WriteLine(value: "[proof] === audio (b): authoring the furnished set — every $type, all source kinds, facets, a sound-bearing creation ===");
            passed &= Mutate(ctx: ctx, name: "patch-chirp", line: WithHash(verb: "world.patch.set", id: "chirp", doc: ChirpDoc, hash: chirpHash!), needle: "[world.mutation: UpsertPatch 'chirp' applied]", dirty: 1);
            passed &= Mutate(ctx: ctx, name: "patch-drone", line: WithHash(verb: "world.patch.set", id: "drone", doc: DroneDoc, hash: droneHash!), needle: "[world.mutation: UpsertPatch 'drone' applied]", dirty: 2);
            passed &= Mutate(ctx: ctx, name: "tune-jingle", line: WithHash(verb: "world.tune.set", id: "jingle", doc: JingleDoc, hash: jingleHash!), needle: "[world.mutation: UpsertTune 'jingle' applied]", dirty: 3);
            passed &= Mutate(ctx: ctx, name: "speaker-left", line: """world.speaker.set {"$type":"fixed","name":"left","position":[-1.5,1,0],"feed":{"source":{"$type":"tune","tuneId":"jingle"},"channel":"left","gain":1}}""", needle: "[world.mutation: UpsertSpeaker 'left' applied]", dirty: 4);
            passed &= Mutate(ctx: ctx, name: "speaker-right", line: """world.speaker.set {"$type":"fixed","name":"right","position":[1.5,1,0],"feed":{"source":{"$type":"tune","tuneId":"jingle"},"channel":"right","gain":1}}""", needle: "[world.mutation: UpsertSpeaker 'right' applied]", dirty: 5);
            passed &= Mutate(ctx: ctx, name: "speaker-wind-bed", line: """world.speaker.set {"$type":"bed","name":"wind","center":[0,0,-6],"radius":5,"innerRadius":2,"feed":{"source":{"$type":"synth","patchId":"drone"},"channel":"mix","gain":0.7}}""", needle: "[world.mutation: UpsertSpeaker 'wind' applied]", dirty: 6);
            passed &= Mutate(ctx: ctx, name: "speaker-leaf-anchor", line: """world.speaker.set {"$type":"anchored","name":"hand-bell","anchor":{"$type":"entityLeaf","index":0,"leaf":"left-hand"},"offset":[0,0.1,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":1}}""", needle: "[world.mutation: UpsertSpeaker 'hand-bell' applied]", dirty: 7);
            passed &= Mutate(ctx: ctx, name: "scene-emission-facet", line: """world.scene.row.set {"$type":"boulder","id":"boulder-1","center":[-1.2,0.72,-0.3],"emission":{"patchId":"chirp","level":1,"radius":8},"radius":0.9,"smooth":0.5}""", needle: "[world.mutation: UpsertSceneRow 'boulder-1' applied]", dirty: 8);
            passed &= Mutate(ctx: ctx, name: "audio-defaults", line: """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1"}""", needle: "[world.mutation: SetAudioDefaults applied]", dirty: 9);
            passed &= Mutate(ctx: ctx, name: "creation-with-sounds", line: WithHash(verb: "world.creation.set", id: "statue", doc: StatueDoc, hash: statueHash!), needle: "[world.mutation: UpsertCreation 'statue' applied]", dirty: 10);
            passed &= Mutate(ctx: ctx, name: "placement-with-emission", line: """world.placement.set {"id":"statue-1","creationId":"statue","position":[0,0,3],"yawDegrees":0,"scale":1,"emission":{"patchId":"drone","level":0.5}}""", needle: "[world.mutation: UpsertPlacement 'statue-1' applied]", dirty: 11);
            passed &= Mutate(ctx: ctx, name: "speaker-placement-anchor", line: """world.speaker.set {"$type":"anchored","name":"statue-voice","anchor":{"$type":"placement","placementId":"statue-1","shapeId":1},"offset":[0,0.25,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":1}}""", needle: "[world.mutation: UpsertSpeaker 'statue-voice' applied]", dirty: 12);

            var emitters = AwaitEcho(ctx: ctx, line: "audio.emitters", needle: "[audio.emitters:");

            passed &= ComposedShotKit.Check(name: "derivation-covers-every-family", ok: ((emitters is not null)
                && emitters.Contains(value: "speaker:left point tune:jingle left")
                && emitters.Contains(value: "speaker:wind bed synth:drone mix")
                && emitters.Contains(value: "speaker:hand-bell point synth:chirp mix")
                && emitters.Contains(value: "speaker:statue-voice point synth:chirp mix")
                && emitters.Contains(value: "scene:boulder-1 point synth:chirp mix")
                && emitters.Contains(value: "placement:statue-1 point synth:drone mix")
                && emitters.Contains(value: "sound:statue-1:hum point synth:sound:statue-1:hum mix")), detail: (emitters?.Trim() ?? "(no audio.emitters echo)"));

            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "authoring-round-refused-nothing", expected: 0);

            Console.WriteLine(value: "[proof] === audio (c): the validator rejection table — each loud, the document unchanged ===");
            passed &= Reject(ctx: ctx, name: "unknown-patch", line: """world.speaker.set {"$type":"fixed","name":"bad","position":[0,0,0],"feed":{"source":{"$type":"synth","patchId":"nope"},"channel":"mix","gain":1}}""", needle: "names no patch row");
            passed &= Reject(ctx: ctx, name: "unknown-tune", line: """world.speaker.set {"$type":"fixed","name":"bad","position":[0,0,0],"feed":{"source":{"$type":"tune","tuneId":"nope"},"channel":"mix","gain":1}}""", needle: "names no tune row");
            passed &= Reject(ctx: ctx, name: "undeclared-screen", line: """world.speaker.set {"$type":"fixed","name":"bad","position":[0,0,0],"feed":{"source":{"$type":"machine","screenIndex":99},"channel":"mix","gain":1}}""", needle: "names no declared screen");
            passed &= Reject(ctx: ctx, name: "bad-leaf-token", line: """world.speaker.set {"$type":"anchored","name":"bad","anchor":{"$type":"entityLeaf","index":0,"leaf":"tail"},"offset":[0,0,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":1}}""", needle: "names no humanoid role");
            passed &= Reject(ctx: ctx, name: "unknown-placement-anchor", line: """world.speaker.set {"$type":"anchored","name":"bad","anchor":{"$type":"placement","placementId":"ghost"},"offset":[0,0,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":1}}""", needle: "names no placement row");
            passed &= Reject(ctx: ctx, name: "bed-radius-rule", line: """world.speaker.set {"$type":"bed","name":"bad","center":[0,0,0],"radius":5,"innerRadius":5,"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":1}}""", needle: "less than radius");
            passed &= Reject(ctx: ctx, name: "gain-ceiling", line: """world.speaker.set {"$type":"fixed","name":"bad","position":[0,0,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":99}}""", needle: "must be within [0, 8]");
            passed &= Reject(ctx: ctx, name: "bad-channel", line: """world.speaker.set {"$type":"fixed","name":"bad","position":[0,0,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"middle","gain":1}}""", needle: "must be 'mix', 'left', or 'right'");
            passed &= Reject(ctx: ctx, name: "bogus-listener", line: """world.audio.set {"masterGain":1,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"moon"}""", needle: "is not 'focus', 'seat:<n>', or a declared camera name");
            passed &= Reject(ctx: ctx, name: "facet-unknown-patch", line: """world.scene.row.set {"$type":"boulder","id":"boulder-2","center":[0.6,0.88,0.5],"emission":{"patchId":"nope","level":1},"radius":1.1,"smooth":0.5}""", needle: "names no patch row");
            passed &= ExpectDirty(ctx: ctx, name: "rejections-changed-nothing", dirty: 12);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "validator-round-refused-only-its-ten", expected: 10);

            Console.WriteLine(value: "[proof] === audio (d): the no-cascade guards name their dependents ===");
            passed &= Reject(ctx: ctx, name: "tune-remove-guard", line: "world.tune.remove jingle", needle: "feeds speaker(s) 'left', 'right'");
            passed &= Reject(ctx: ctx, name: "patch-remove-guard", line: "world.patch.remove drone", needle: "referenced by speaker(s) 'wind', placement 'statue-1'");
            passed &= Reject(ctx: ctx, name: "placement-remove-guard", line: "world.placement.remove statue-1", needle: "anchors speaker(s) 'statue-voice'");
            passed &= ExpectDirty(ctx: ctx, name: "guards-changed-nothing", dirty: 12);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "guard-round-refused-only-its-three", expected: 3);

            Console.WriteLine(value: "[proof] === audio (e): undo + grants rounds ===");
            passed &= Mutate(ctx: ctx, name: "undo-drops-speaker", line: "world.undo", needle: "[world.undo: dropped 1, 11 remaining]", dirty: 11);

            var afterUndo = AwaitEcho(ctx: ctx, line: "audio.emitters", needle: "[audio.emitters:");

            passed &= ComposedShotKit.Check(name: "undo-leaves-derivation", ok: ((afterUndo is not null) && !afterUndo.Contains(value: "statue-voice")), detail: ((afterUndo is null) ? "(no echo)" : "statue-voice departed the derivation"));
            passed &= Mutate(ctx: ctx, name: "speaker-reapplied", line: """world.speaker.set {"$type":"anchored","name":"statue-voice","anchor":{"$type":"placement","placementId":"statue-1","shapeId":1},"offset":[0,0.25,0],"feed":{"source":{"$type":"synth","patchId":"chirp"},"channel":"mix","gain":1}}""", needle: "[world.mutation: UpsertSpeaker 'statue-voice' applied]", dirty: 12);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.revoke console mutate section:speakers", expect: "[world.revoke: console mutate section:speakers]", name: "revoke-speakers-section");
            passed &= Reject(ctx: ctx, name: "denied-without-grant", line: "world.speaker.remove wind", needle: "cannot mutate section:speakers", rejectPrefix: "[world.grant denied:");
            passed &= ExpectDirty(ctx: ctx, name: "denial-changed-nothing", dirty: 12);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "denial-round-refused-only-its-one", expected: 1);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.grant console mutate section:speakers", expect: "[world.grant: console mutate section:speakers]", name: "regrant-speakers-section");
            passed &= Mutate(ctx: ctx, name: "applies-after-regrant", line: "world.speaker.remove wind", needle: "[world.mutation: RemoveSpeaker 'wind' applied]", dirty: 13);
            passed &= Mutate(ctx: ctx, name: "undo-restores-wind", line: "world.undo", needle: "[world.undo: dropped 1, 12 remaining]", dirty: 12);

            Console.WriteLine(value: "[proof] === audio (f): THE CUE TABLE — world events tie to sound as data ===");
            passed &= Mutate(ctx: ctx, name: "cue-table-applies", line: CueTableLine, needle: "[world.mutation: SetAudioDefaults applied]", dirty: 13);

            var idleState = AwaitEcho(ctx: ctx, line: "speaker.state", needle: "[speaker.state:");

            passed &= ComposedShotKit.Check(name: "cues-idle-before-any-event", ok: ((idleState is not null) && idleState.Contains(value: "cues 0")), detail: (idleState?.Trim() ?? "(no speaker.state echo)"));
            passed &= Mutate(ctx: ctx, name: "mutation-fires-cue", line: BoulderResubmitLine, needle: "[world.mutation: UpsertSceneRow 'boulder-1' applied]", dirty: 14);
            passed &= AwaitSpeakerCue(ctx: ctx, name: "mutation-applied-cue-lands", needle: "cue:mutation.applied=drone");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "cue-table-round-refused-nothing", expected: 0);

            Console.WriteLine(value: "[proof] === audio (g): the cue validator table — each loud, the document unchanged ===");
            passed &= Reject(ctx: ctx, name: "cue-bad-token", line: """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1","cues":[{"event":"volcano.erupts","patchId":"drone","placement":"listener"}]}""", needle: "is not a published cue event token");
            passed &= Reject(ctx: ctx, name: "cue-unknown-patch", line: """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1","cues":[{"event":"mutation.applied","patchId":"nope","placement":"listener"}]}""", needle: "names no patch row");
            passed &= Reject(ctx: ctx, name: "cue-unknown-emitter", line: """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1","cues":[{"event":"mutation.applied","patchId":"drone","placement":"emitter:ghost"}]}""", needle: "names no declared speaker");
            passed &= Reject(ctx: ctx, name: "cue-bad-placement", line: """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1","cues":[{"event":"mutation.applied","patchId":"drone","placement":"sideways"}]}""", needle: "must be 'at-site', 'listener', or 'emitter:");
            passed &= Reject(ctx: ctx, name: "cue-gain-ceiling", line: """world.audio.set {"masterGain":0.8,"defaultSpeakerRadius":8,"defaultCurve":"smoothstep","defaultBedFadeSeconds":0.5,"listener":"seat:1","cues":[{"event":"mutation.applied","patchId":"drone","gainThousandths":9001,"placement":"listener"}]}""", needle: "must be within [0, 8000]");
            passed &= ExpectDirty(ctx: ctx, name: "cue-rejections-changed-nothing", dirty: 14);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "cue-validator-round-refused-only-its-five", expected: 5);

            Console.WriteLine(value: "[proof] === audio (h): the cue producers — denial, footsteps, the binder fault lane ===");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.revoke console mutate section:speakers", expect: "[world.revoke: console mutate section:speakers]", name: "revoke-for-denied-cue");
            passed &= Reject(ctx: ctx, name: "denied-mutation-rejects", line: "world.speaker.remove wind", needle: "cannot mutate section:speakers", rejectPrefix: "[world.grant denied:");
            passed &= AwaitSpeakerCue(ctx: ctx, name: "grant-denied-cue-lands", needle: "cue:grant.denied=drone");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.grant console mutate section:speakers", expect: "[world.grant: console mutate section:speakers]", name: "regrant-after-denied-cue");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.run 1 0 0 1.5", expect: "[player.run:", name: "walk-for-footsteps");
            passed &= AwaitSpeakerCue(ctx: ctx, name: "footstep-cues-advance", needle: "cue:player.footstep=drone");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.stop 1", expect: "[player.stop:", name: "walk-stops");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.insert 0 {Path.Combine(path1: Path.GetTempPath(), path2: "puck-audio-missing.gb")}", expect: "[screen.insert:", name: "insert-missing-content");
            passed &= AwaitSpeakerCue(ctx: ctx, name: "screen-fault-cue-lands", needle: "cue:screen.fault=drone");
            passed &= ExpectDirty(ctx: ctx, name: "producers-changed-nothing", dirty: 14);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "producer-round-refused-only-its-two", expected: 2);

            Console.WriteLine(value: "[proof] === audio (i): speakers through the editor — select, drag, undo, the numeric twins ===");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.enter 1", expect: "[editor.enter: seat 1 editing", name: "editor-enters");
            var speakerXBefore = ReadSelectedSpeakerX(ctx: ctx, name: "speaker-selects");

            passed &= (speakerXBefore is not null);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.grab 1", expect: "dragging speakers 'left'", name: "speaker-grabs");
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.drag 1 0 0", expect: "[editor.drag: seat 1 speaker 'left'", name: "speaker-drags");
            passed &= Mutate(ctx: ctx, name: "speaker-release-commits", line: "editor.release 1", needle: "[world.mutation: UpsertSpeaker 'left' applied]", dirty: 15);

            // THE BEHAVIORAL HALF: editor.release commits the pending row whether or not the delta landed, so read the
            // committed center back. A no-op DragHandler passes every echo above and fails exactly here.
            var speakerXAfter = ReadSelectedSpeakerX(ctx: ctx, name: "speaker-reselects");

            passed &= ComposedShotKit.Check(name: "speaker-drag-moves-the-row",
                ok: ((speakerXBefore is { } beforeX) && (speakerXAfter is { } afterX) && (Math.Abs(value: ((afterX - beforeX) - 1.0)) < 0.25)),
                detail: $"x {(speakerXBefore?.ToString(format: "0.00", provider: ProofApp.Inv) ?? "?")} -> {(speakerXAfter?.ToString(format: "0.00", provider: ProofApp.Inv) ?? "?")} (drag dx 1)");
            passed &= Mutate(ctx: ctx, name: "speaker-drag-undoes", line: "world.undo", needle: "[world.undo: dropped 1, 14 remaining]", dirty: 14);
            passed &= Mutate(ctx: ctx, name: "speaker-place-verb", line: "editor.speaker.place probe synth:chirp 4", needle: "[world.mutation: UpsertSpeaker 'probe' applied]", dirty: 15);
            passed &= Mutate(ctx: ctx, name: "speaker-gain-verb", line: "editor.speaker.gain probe 0.5", needle: "[world.mutation: UpsertSpeaker 'probe' applied]", dirty: 16);
            passed &= Mutate(ctx: ctx, name: "speaker-delete-verb", line: "editor.speaker.delete probe", needle: "[world.mutation: RemoveSpeaker 'probe' applied]", dirty: 17);
            passed &= Mutate(ctx: ctx, name: "speaker-verbs-unwind", line: "world.undo 3", needle: "[world.undo: dropped 3, 14 remaining]", dirty: 14);
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "editor.exit 1", expect: "[editor.exit: seat 1", name: "editor-exits");

            Console.WriteLine(value: "[proof] === audio (j): the master-volume session lever (the render-levers asymmetry) ===");

            var volumeRead = AwaitEcho(ctx: ctx, line: "world.volume", needle: "[world.volume:");

            passed &= ComposedShotKit.Check(name: "volume-reads-document", ok: ((volumeRead is not null) && volumeRead.Contains(value: "0.8 (document audio.masterGain)")), detail: (volumeRead?.Trim() ?? "(no world.volume echo)"));
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.volume 0.5", expect: "[world.volume: 0.5 (session lever", name: "volume-lever-engages");

            var driftStatus = AwaitEcho(ctx: ctx, line: "world.status", needle: "[world.status:");

            passed &= ComposedShotKit.Check(name: "volume-names-audio-drift", ok: ((driftStatus is not null) && driftStatus.Contains(value: "session-drift audio ")), detail: (driftStatus?.Trim() ?? "(no world.status echo)"));

            Console.WriteLine(value: "[proof] === audio (k): world.save compacts the furnished world (cues + the volume fold inside) ===");

            var mark = ctx.Collector.Count;

            ComposedShotKit.Send(ctx: ctx, line: $"world.save {savePath}");

            var saveLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);

            passed &= ComposedShotKit.Check(name: "save-writes", ok: ((saveLine is not null) && !saveLine.Contains(value: "could not write")), detail: (saveLine?.Trim() ?? "(no world.save echo)"));
            passed &= ExpectDirty(ctx: ctx, name: "save-compacts", dirty: 0);

            var savedJson = (File.Exists(path: savePath) ? File.ReadAllText(path: savePath) : string.Empty);

            passed &= ComposedShotKit.Check(name: "save-folds-volume-lever", ok: savedJson.Contains(value: "\"masterGain\": 0.5"), detail: (savedJson.Contains(value: "\"masterGain\": 0.5") ? "audio.masterGain carries the 0.5 lever" : "saved masterGain is not the lever value"));
            passed &= ComposedShotKit.Check(name: "save-carries-cues", ok: (savedJson.Contains(value: "\"cues\":") && savedJson.Contains(value: "mutation.applied")), detail: "the cue table persisted in the audio section");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "editor-and-save-rounds-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: ctx.Process);
        }

        return passed;
    }

    static bool SessionB(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string savePath, string resavePath) {
        Console.WriteLine();
        Console.WriteLine(value: "[proof] === audio (g): relaunch — the fresh-boot derivation pin + the ouroboros with audio sections ===");

        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", savePath]);
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: ctx.Process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: ctx.Process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            var bootLine = ComposedShotKit.Await(collector: ctx.Collector, mark: 0, predicate: l => (l.Contains(value: "[world] definition:") && l.Contains(value: savePath)), deadlineSeconds: 30.0);

            passed &= ComposedShotKit.Check(name: "boots-from-saved-file", ok: (bootLine is not null), detail: (bootLine?.Trim() ?? "(no boot line naming the saved file)"));

            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            var emitters = AwaitEcho(ctx: ctx, line: "audio.emitters", needle: "[audio.emitters:");

            // The collector stamps each line with its elapsed-time prefix, so the byte-exact pin matches the line TAIL.
            passed &= ComposedShotKit.Check(name: "fresh-boot-derivation-pinned", ok: ((emitters is not null) && emitters.Trim().EndsWith(value: ExpectedEmittersAfterReboot, comparisonType: StringComparison.Ordinal)), detail: (emitters?.Trim() ?? "(no audio.emitters echo)"));

            var speakers = AwaitEcho(ctx: ctx, line: "world.speakers", needle: "[world.speakers:");

            passed &= ComposedShotKit.Check(name: "speakers-listing", ok: ((speakers is not null)
                && speakers.Contains(value: "left fixed tune:jingle left gain=1")
                && speakers.Contains(value: "wind bed synth:drone mix gain=0.7")
                && speakers.Contains(value: "hand-bell anchored synth:chirp mix gain=1")
                && speakers.Contains(value: "statue-voice anchored synth:chirp mix gain=1")), detail: (speakers?.Trim() ?? "(no world.speakers echo)"));

            var mark = ctx.Collector.Count;

            ComposedShotKit.Send(ctx: ctx, line: $"world.save {resavePath}");

            var saveLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.save:"), deadlineSeconds: 30.0);

            passed &= ComposedShotKit.Check(name: "resave-writes", ok: ((saveLine is not null) && !saveLine.Contains(value: "could not write")), detail: (saveLine?.Trim() ?? "(no world.save echo)"));

            var identical = (File.Exists(path: resavePath) && File.ReadAllBytes(path: savePath).AsSpan().SequenceEqual(other: File.ReadAllBytes(path: resavePath)));

            passed &= ComposedShotKit.Check(name: "ouroboros-with-audio-sections", ok: identical, detail: (identical ? "save -> reboot -> save byte-identical (cue table + the folded volume lever included)" : "byte mismatch between the two saves"));

            // The folded lever is the REBOOTED document's master gain (the lever itself starts unengaged), and the
            // persisted cue table still FIRES — the behavioral half of the ouroboros.
            var volumeRead = AwaitEcho(ctx: ctx, line: "world.volume", needle: "[world.volume:");

            passed &= ComposedShotKit.Check(name: "reboot-wakes-on-folded-volume", ok: ((volumeRead is not null) && volumeRead.Contains(value: "0.5 (document audio.masterGain)")), detail: (volumeRead?.Trim() ?? "(no world.volume echo)"));
            passed &= Mutate(ctx: ctx, name: "reboot-mutation-applies", line: BoulderResubmitLine, needle: "[world.mutation: UpsertSceneRow 'boulder-1' applied]", dirty: 1);
            passed &= AwaitSpeakerCue(ctx: ctx, name: "reboot-cue-table-still-fires", needle: "cue:mutation.applied=drone");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "reboot-session-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: ctx.Process);
        }

        return passed;
    }

    // One inline-JSON asset-row submission: {"id":..,"document":<doc>,"hash":..} — shared by the pin probes (hash "0")
    // and the real submissions (the harvested canonical hash).
    static string WithHash(string verb, string id, string doc, string hash) =>
        (verb + " {\"id\":\"" + id + "\",\"document\":" + doc + ",\"hash\":\"" + hash + "\"}");

    // Submit a *set verb carrying hash "0" and harvest the canonical sha256 the loud rejection names — the pin proven
    // (a foreign hash never lands) and satisfied (the pipeline's own hash) in one gesture; dirty is unmoved.
    static string? ProbeCanonicalHash(ComposedShotKit.Ctx ctx, string name, string line, string rejectNeedle) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        var rejected = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => (l.Contains(value: "[world.mutation rejected:") && l.Contains(value: rejectNeedle)), deadlineSeconds: 20.0);
        var match = ((rejected is null) ? null : CanonicalSha.Match(input: rejected));
        var hash = (((match is not null) && match.Success) ? match.Groups[1].Value : null);

        _ = ComposedShotKit.Check(name: name, ok: (hash is not null), detail: ((hash is not null) ? $"canonical {hash[..12]}... harvested from the loud pin rejection" : (rejected?.Trim() ?? "(no rejection echo)")));

        return hash;
    }

    // A mutation followed by the barrier-ordered dirty read: the loud needle appeared and the journal length matches.
    static bool Mutate(ComposedShotKit.Ctx ctx, string name, string line, string needle, int dirty) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        var echoed = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: needle), deadlineSeconds: 20.0);
        var ok = ComposedShotKit.Check(name: name, ok: (echoed is not null), detail: (echoed?.Trim() ?? $"(no '{needle}' echo)"));

        return (ok & ExpectDirty(ctx: ctx, name: $"{name}-dirty", dirty: dirty));
    }

    // A rejected submission: the loud line carries the needle (validator/guard/grant), and nothing applied.
    static bool Reject(ComposedShotKit.Ctx ctx, string name, string line, string needle, string rejectPrefix = "[world.mutation rejected:") {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        var rejected = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => (l.Contains(value: rejectPrefix) && l.Contains(value: needle)), deadlineSeconds: 20.0);

        return ComposedShotKit.Check(name: name, ok: (rejected is not null), detail: (rejected?.Trim() ?? $"(no '{rejectPrefix} ...{needle}' echo)"));
    }

    static readonly Regex SpeakerSelectEcho = new(options: RegexOptions.Compiled,
        pattern: @"\[editor\.select: seat \d+ speakers '[^']+' at \((-?[0-9.]+), (-?[0-9.]+), (-?[0-9.]+)\)\]");

    // Re-selects the 'left' bed and parses the X of the echoed document position — the committed-pose witness the
    // drag round compares before/after. Selection is read-only, so this never perturbs the journal.
    static double? ReadSelectedSpeakerX(ComposedShotKit.Ctx ctx, string name) {
        var line = AwaitEcho(ctx: ctx, line: "editor.select speakers left", needle: "speakers 'left'");
        var match = ((line is not null) ? SpeakerSelectEcho.Match(input: line) : null);
        var value = (((match is { Success: true }) &&
            double.TryParse(s: match.Groups[1].Value, style: NumberStyles.Float, provider: ProofApp.Inv, result: out var parsed))
            ? parsed
            : (double?)null);

        _ = ComposedShotKit.Check(name: name, ok: (value is not null), detail: (line ?? "(no editor.select echo)"));

        return value;
    }

    static string? AwaitEcho(ComposedShotKit.Ctx ctx, string line, string needle) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        return ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: needle), deadlineSeconds: 20.0);
    }

    // Poll speaker.state for a live transient-cue token: a fired cue registers in the director SYNCHRONOUSLY with
    // its producing event, but the loudest producers (footsteps mid-run) land between polls — a bounded re-read
    // keeps the round honest without riding exact TTL timing (drone cues live the 2 s looping cap).
    static bool AwaitSpeakerCue(ComposedShotKit.Ctx ctx, string name, string needle) {
        for (var attempt = 0; (attempt < 10); attempt++) {
            var echo = AwaitEcho(ctx: ctx, line: "speaker.state", needle: "[speaker.state:");

            if ((echo is not null) && echo.Contains(value: needle)) {
                return ComposedShotKit.Check(name: name, ok: true, detail: $"{needle} live in speaker.state");
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return ComposedShotKit.Check(name: name, ok: false, detail: $"'{needle}' never appeared in speaker.state");
    }

    static bool ExpectDirty(ComposedShotKit.Ctx ctx, string name, int dirty) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.status");

        var statusLine = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => DirtyEcho.IsMatch(input: l), deadlineSeconds: 20.0);
        var actual = ((statusLine is null) ? -1 : int.Parse(s: DirtyEcho.Match(input: statusLine).Groups[1].Value, provider: ProofApp.Inv));

        return ComposedShotKit.Check(name: name, ok: (actual == dirty), detail: ((statusLine is null) ? "(no world.status echo)" : $"dirty {actual} (want {dirty})"));
    }
}

// ============================================================================================
// COLLISION — the solidity/contact + host-section proof (Arc 1, Arc 2, Beat A). Every check is a
// BEHAVIOR, not an echo: a body that stops against a solid row and stops stopping when the facet
// is turned off, a displacement DELTA between two response tables, the field provider's
// gradient-derived up (the planetoid signature the analytic provider cannot produce), and the
// host section's three-column read / drift / undo / denial / reject table. Runs on BOTH backends
// because the host read's RESOLVED column names the one actually hosting.
// ============================================================================================
static class CollisionProof {
    // The proof-authored wall: a wide, thin solid slab across the clear ground north of the boulder field, with the run
    // lane (z 8 -> 12) empty of every checked-in row.
    const string WallId = "proof-wall";
    const string WallJson = """{"$type":"slab","id":"proof-wall","center":[0,1,12],"halfExtents":[6,2,0.5],"round":0.05,"smooth":0,"albedo":[0.5,0.5,0.5],"solid":{"margin":0}}""";
    // The proof-authored planetoid: a solid sphere floating far above the ground plane, so the plane never wins the
    // union anywhere the walk happens and "up" is the sphere's own gradient.
    const string PlanetoidJson = """{"$type":"boulder","id":"proof-planetoid","center":[0,40,60],"radius":8,"smooth":0,"solid":{"margin":0}}""";
    const double PlanetoidCenterY = 40.0;
    const double PlanetoidCenterZ = 60.0;
    const double PlanetoidRadius = 8.0;
    // The lane the wall crosses: the body starts here and runs +Z into the slab's face at z = 11.5.
    const double LaneStartZ = 8.0;
    // u — the wall's near face (11.5) minus the capsule radius and skin lands the body at ~11.13; a body that passed
    // through the slab is out past 20 within the same segment (measured: 26.00 at ~9 u/s over 2 s).
    const double BlockedCeilingZ = 12.0;
    const double PassedFloorZ = 20.0;
    // u — the radial distance from the planetoid center must HOLD across the walk (measured: 8.02 -> 8.02 under the
    // field provider, 8.02 -> 33.0 under analytic).
    const double RadialHoldEpsilon = 0.35;
    const double RadialDivergeFloor = 10.0;
    // u — the height the walk must shed while that radius holds (measured: 12.89 over a two-second segment).
    const double PlanetoidDropFloor = 5.0;

    static readonly Regex PlanarSpeedEcho = new(options: RegexOptions.Compiled, pattern: @"planarSpeed=(-?[0-9.]+)");
    static readonly Regex DriftEcho = new(options: RegexOptions.Compiled, pattern: @"session-drift (\S+)");
    static readonly Regex FieldInstructionsEcho = new(options: RegexOptions.Compiled, pattern: @"instructions=(\d+)");
    static readonly Regex FieldRevisionEcho = new(options: RegexOptions.Compiled, pattern: @"revision=(\d+)");

    public static int RunCollision(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 640, name: "--width");
        var height = opts.GetInt(fallback: 480, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 600, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        // D3D12 FIRST (World's default backend), then Vulkan — the host read's resolved column names each one.
        Console.WriteLine(value: "[proof] === collision (a): Direct3D 12 (the default backend) ===");

        var directXPassed = RunBackend(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === collision (b): Vulkan ===");

        var vulkanPassed = RunBackend(exe: exe, repoRoot: repoRoot, backend: "vulkan", width: width, height: height, exitAfterSeconds: exitAfterSeconds);
        var passed = (directXPassed && vulkanPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] collision proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // One backend: the driving session, then the relaunch that reads its saved host row back.
    static bool RunBackend(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds) {
        var tag = (backend ?? "directx");
        var savedPath = Path.Combine(Path.GetTempPath(), $"puck-world-collision-{Environment.ProcessId}-{tag}.world.json");

        try {
            var sessionPassed = RunSession(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, savedPath: savedPath);
            var reloadPassed = RunReload(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, savedPath: savedPath);

            return (sessionPassed && reloadPassed);
        }
        finally {
            TryDelete(path: savedPath);
        }
    }

    // The driving session: solidity, the response table, the collider/model facets, the provider swap, the rejection
    // table, and the host round — one boot, every claim measured on a pose or a counter.
    static bool RunSession(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds, string savedPath) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Mask seat 1's device stream: every displacement below must be a SUBMITTED tape segment's, never a held
            // key leaking in from the machine this runs on.
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.control idle 1", expect: "[player.control: p1 idle]", name: "seat-parked-idle");

            passed &= RunSolidityRound(ctx: ctx);
            passed &= RunResponseRound(ctx: ctx);
            passed &= RunColliderRound(ctx: ctx);
            passed &= RunFieldRound(ctx: ctx);
            passed &= RunRejectionRound(ctx: ctx);
            passed &= RunHostRound(ctx: ctx, backend: backend, width: width, height: height, savedPath: savedPath);

            // Every deliberate refusal above was settled and cleared by its own round. A nonzero count here is a line
            // this scenario MEANT to succeed and the wire refused — the silent no-op that reads green everywhere else.
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "no-silent-rejections", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return passed;
    }

    // (a) SOLIDITY IS DATA. A proof-authored slab crosses the run lane; the body stops dead against it, and the SAME
    // segment carries the body clean past the slab's plane once the facet is dropped. The mid-run world.contacts read is
    // the second half: the solver zeroes the velocity driving into the surface, so planar speed reads 0 while the tape
    // is still commanding full forward.
    static bool RunSolidityRound(ComposedShotKit.Ctx ctx) {
        var passed = ExpectEcho(ctx: ctx, name: "contacts-census-boot", line: "world.contacts", needle: "[world.contacts: collision on — 10 solid rows (5 spheres, 5 boxes)]");

        passed &= ExpectMutation(ctx: ctx, name: "wall-authored", line: $"world.scene.row.set {WallJson}", needle: $"[world.mutation: UpsertSceneRow '{WallId}' applied]");
        passed &= ExpectEcho(ctx: ctx, name: "contacts-census-counts-the-wall", line: "world.contacts", needle: "[world.contacts: collision on — 11 solid rows (5 spheres, 6 boxes)]");

        var (blocked, blockedSpeed) = RunLane(ctx: ctx, seconds: 2.0);

        passed &= Check(name: "solid-wall-stops-body", ok: ((blocked is { } b) && (b.Z < BlockedCeilingZ)),
            detail: $"p1 z {Fmt(value: blocked?.Z)} after a two-second forward segment from z {ProofApp.F(value: LaneStartZ)} (want < {ProofApp.F(value: BlockedCeilingZ)})");
        passed &= Check(name: "solid-wall-kills-planar-speed", ok: ((blockedSpeed is { } s) && (s < 0.5)),
            detail: $"world.contacts planarSpeed {Fmt(value: blockedSpeed)} mid-segment (want < 0.5 — the tape still commands full forward)");

        // Drop the facet, keeping the row: solidity is the FACET, not the geometry.
        passed &= ExpectMutation(ctx: ctx, name: "wall-facet-dropped", line: $"world.scene.solid {WallId} off", needle: $"[world.mutation: UpsertSceneRow '{WallId}' applied]");
        passed &= ExpectEcho(ctx: ctx, name: "contacts-census-drops-the-wall", line: "world.contacts", needle: "[world.contacts: collision on — 10 solid rows (5 spheres, 5 boxes)]");

        var (passedThrough, freeSpeed) = RunLane(ctx: ctx, seconds: 2.0);

        passed &= Check(name: "decorative-wall-stops-nothing", ok: ((passedThrough is { } p) && (p.Z > PassedFloorZ)),
            detail: $"p1 z {Fmt(value: passedThrough?.Z)} on the IDENTICAL segment (want > {ProofApp.F(value: PassedFloorZ)})");
        passed &= Check(name: "decorative-wall-leaves-speed-alone", ok: ((freeSpeed is { } fs) && (fs > 4.0)),
            detail: $"world.contacts planarSpeed {Fmt(value: freeSpeed)} mid-segment (want > 4)");

        // Restore the facet for the collider round, which re-uses the same wall.
        passed &= ExpectMutation(ctx: ctx, name: "wall-facet-restored", line: $"world.scene.solid {WallId} 0", needle: $"[world.mutation: UpsertSceneRow '{WallId}' applied]");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "solidity-round-refused-nothing", expected: 0);

        return passed;
    }

    // (b) THE RESPONSE TABLE SHAPES MOMENTUM. The same 0.6-second segment, from the same warp, over empty ground: an
    // empty table snaps to full speed instantly, an authored slow-converging row ramps. The DELTA is the feature — a
    // build that ignored the table would post the same displacement twice.
    static bool RunResponseRound(ComposedShotKit.Ctx ctx) {
        var passed = ExpectMutation(ctx: ctx, name: "response-cleared", line: "world.kit.response runner none", needle: "[world.mutation: UpsertKit 'runner' applied]");

        var snap = RunOpenGround(ctx: ctx, seconds: 0.6);

        passed &= ExpectMutation(ctx: ctx, name: "response-table-authored", line: """world.kit.response runner [{"engageRate":1.5,"releaseRate":1.5}]""", needle: "[world.mutation: UpsertKit 'runner' applied]");

        var ramped = RunOpenGround(ctx: ctx, seconds: 0.6);
        var delta = (snap - ramped);

        passed &= Check(name: "empty-table-snaps", ok: (snap > 4.0), detail: $"{Fmt(value: snap)} u travelled in 0.6 s with no response rows (want > 4)");
        passed &= Check(name: "authored-table-ramps", ok: (ramped < 1.5), detail: $"{Fmt(value: ramped)} u travelled on the IDENTICAL segment through an engageRate-1.5 row (want < 1.5)");
        passed &= Check(name: "response-table-shapes-momentum", ok: (delta > 3.0), detail: $"delta {Fmt(value: delta)} u between the two tables (want > 3)");

        // Back to the snap table so the later lanes travel at full speed.
        passed &= ExpectMutation(ctx: ctx, name: "response-restored", line: "world.kit.response runner none", needle: "[world.mutation: UpsertKit 'runner' applied]");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "response-round-refused-nothing", expected: 0);

        return passed;
    }

    // (c) THE KIT FACETS. A volumeless kit is not solved against the contact field at all (it walks through the restored
    // solid wall); the motion model is a live document switch (a grounded body ignores the up channel, a free one
    // integrates it); and collision.off drops solidity world-wide while the rows keep their facets in the document.
    static bool RunColliderRound(ComposedShotKit.Ctx ctx) {
        var passed = ExpectMutation(ctx: ctx, name: "collider-removed", line: "world.kit.collider runner none", needle: "[world.mutation: UpsertKit 'runner' applied]");

        passed &= ExpectEcho(ctx: ctx, name: "status-drops-the-runner-collider", line: "world.collision.status", needle: "colliders=[jumper(r=0.35 h=1.7), kart(r=0.35 h=1.7)]");

        var (volumeless, _) = RunLane(ctx: ctx, seconds: 2.0);

        passed &= Check(name: "volumeless-kit-ignores-solids", ok: ((volumeless is { } v) && (v.Z > PassedFloorZ)),
            detail: $"p1 z {Fmt(value: volumeless?.Z)} through the SOLID wall (want > {ProofApp.F(value: PassedFloorZ)})");

        passed &= ExpectMutation(ctx: ctx, name: "collider-restored", line: "world.kit.collider runner 0.35 1.7", needle: "[world.mutation: UpsertKit 'runner' applied]");
        passed &= ExpectEcho(ctx: ctx, name: "status-regains-the-runner-collider", line: "world.collision.status", needle: "colliders=[jumper(r=0.35 h=1.7), runner(r=0.35 h=1.7), kart(r=0.35 h=1.7)]");

        var (revolumed, _) = RunLane(ctx: ctx, seconds: 2.0);

        passed &= Check(name: "restored-collider-stops-again", ok: ((revolumed is { } r) && (r.Z < BlockedCeilingZ)),
            detail: $"p1 z {Fmt(value: revolumed?.Z)} (want < {ProofApp.F(value: BlockedCeilingZ)})");

        // The motion model: the up channel is inert under grounded and integrated under free — the DOCUMENT facet, not
        // the per-body player.motion override, is what flips it.
        var groundedRise = RunUpChannel(ctx: ctx);

        passed &= Check(name: "grounded-model-pins-the-up-channel", ok: ((groundedRise is { } g) && (Math.Abs(value: g) < 0.05)),
            detail: $"p1 y {Fmt(value: groundedRise)} after a one-second up segment (want ~0)");

        passed &= ExpectMutation(ctx: ctx, name: "model-set-free", line: "world.kit.model runner free", needle: "[world.mutation: UpsertKit 'runner' applied]");
        passed &= ExpectEcho(ctx: ctx, name: "body-reads-free", line: "player.motion 1", needle: "[player.motion: player 1 is free]");

        var freeRise = RunUpChannel(ctx: ctx);

        passed &= Check(name: "free-model-integrates-the-up-channel", ok: ((freeRise is { } f) && (f > 4.0)),
            detail: $"p1 y {Fmt(value: freeRise)} on the IDENTICAL segment (want > 4)");

        passed &= ExpectMutation(ctx: ctx, name: "model-restored", line: "world.kit.model runner grounded", needle: "[world.mutation: UpsertKit 'runner' applied]");
        passed &= ExpectEcho(ctx: ctx, name: "body-reads-grounded", line: "player.motion 1", needle: "[player.motion: player 1 is grounded]");

        // Collision OFF: the rows keep their facets (the census still counts them) and nothing is solved.
        passed &= ExpectMutation(ctx: ctx, name: "collision-off", line: "world.collision.off", needle: "[world.mutation: SetCollision applied]");
        passed &= ExpectEcho(ctx: ctx, name: "census-reads-off-with-rows-intact", line: "world.contacts", needle: "[world.contacts: collision off — 11 solid rows (5 spheres, 6 boxes)]");

        var (unsolved, _) = RunLane(ctx: ctx, seconds: 2.0);

        passed &= Check(name: "collision-off-stops-nothing", ok: ((unsolved is { } u) && (u.Z > PassedFloorZ)),
            detail: $"p1 z {Fmt(value: unsolved?.Z)} with collision off (want > {ProofApp.F(value: PassedFloorZ)})");

        passed &= ExpectMutation(ctx: ctx, name: "collision-on", line: "world.collision.on", needle: "[world.mutation: SetCollision applied]");

        var (resolved, _) = RunLane(ctx: ctx, seconds: 2.0);

        passed &= Check(name: "collision-on-stops-again", ok: ((resolved is { } re) && (re.Z < BlockedCeilingZ)),
            detail: $"p1 z {Fmt(value: resolved?.Z)} (want < {ProofApp.F(value: BlockedCeilingZ)})");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "collider-round-refused-nothing", expected: 0);

        return passed;
    }

    // (d) THE FIELD PROVIDER. Its up axis is the compiled SDF's gradient, so a walk over a floating solid sphere sheds
    // height while the radius from the sphere's center HOLDS — the planetoid signature. The analytic provider answers a
    // constant +Y and cannot produce it: the identical pose and segment walk off the top and fall away.
    static bool RunFieldRound(ComposedShotKit.Ctx ctx) {
        var passed = ExpectMutation(ctx: ctx, name: "planetoid-authored", line: $"world.scene.row.set {PlanetoidJson}", needle: "[world.mutation: UpsertSceneRow 'proof-planetoid' applied]");

        passed &= ExpectMutation(ctx: ctx, name: "provider-switched-to-field", line: "world.collision.provider field", needle: "[world.mutation: SetCollision applied]");
        // The status is not pinned to a compiled SIZE (an ISA change may legitimately move it) — only to the two facts
        // the swap has to produce: a program with instructions in it, and a bumped field revision.
        var statusLine = ReadLine(ctx: ctx, command: "world.collision.status", needle: "[world.collision.status: on provider=field");
        var instructions = ((statusLine is null) ? -1 : ParseInt(line: statusLine, pattern: FieldInstructionsEcho));
        var revision = ((statusLine is null) ? -1 : ParseInt(line: statusLine, pattern: FieldRevisionEcho));

        passed &= Check(name: "status-reads-a-compiled-field", ok: ((instructions > 0) && (revision >= 1)),
            detail: (statusLine?.Trim() ?? "(no world.collision.status echo naming provider=field)"));

        // The gradient is GEOMETRY-derived, not a constant: probes around the sphere answer three different ups.
        passed &= ExpectEcho(ctx: ctx, name: "probe-up-at-north-pole", line: "world.collision.probe 0 48 60", needle: "distance=0.000 material=7 gradient=(0.000, 1.000, 0.000)");
        passed &= ExpectEcho(ctx: ctx, name: "probe-up-on-the-z-flank", line: "world.collision.probe 0 40 68", needle: "distance=0.000 material=7 gradient=(0.000, 0.000, 1.000)");
        passed &= ExpectEcho(ctx: ctx, name: "probe-up-on-the-x-flank", line: "world.collision.probe 8 40 60", needle: "distance=0.000 material=7 gradient=(1.000, 0.000, 0.000)");
        passed &= ExpectEcho(ctx: ctx, name: "probe-inside-reads-negative", line: "world.collision.probe 0 44 60", needle: "distance=-4.000");

        var fieldWalk = RunPlanetoidWalk(ctx: ctx);

        passed &= Check(name: "field-walk-sheds-height", ok: ((fieldWalk.Drop is { } d) && (d > PlanetoidDropFloor)),
            detail: $"y fell {Fmt(value: fieldWalk.Drop)} u over a two-second segment (want > {ProofApp.F(value: PlanetoidDropFloor)})");
        passed &= Check(name: "field-walk-holds-the-radius", ok: ((fieldWalk.RadialDelta is { } r) && (Math.Abs(value: r) <= RadialHoldEpsilon)),
            detail: $"radius from the planetoid center moved {Fmt(value: fieldWalk.RadialDelta)} u ({Fmt(value: fieldWalk.RadiusBefore)} -> {Fmt(value: fieldWalk.RadiusAfter)}, want <= {ProofApp.F(value: RadialHoldEpsilon)}) — the body walked OVER the surface, not off it");

        // The negative control on the same geometry: constant +Y cannot hold a curved surface.
        passed &= ExpectMutation(ctx: ctx, name: "provider-switched-to-analytic", line: "world.collision.provider analytic", needle: "[world.mutation: SetCollision applied]");

        var analyticWalk = RunPlanetoidWalk(ctx: ctx);

        passed &= Check(name: "analytic-walk-leaves-the-surface", ok: ((analyticWalk.RadialDelta is { } ar) && (ar > RadialDivergeFloor)),
            detail: $"radius from the planetoid center moved {Fmt(value: analyticWalk.RadialDelta)} u ({Fmt(value: analyticWalk.RadiusBefore)} -> {Fmt(value: analyticWalk.RadiusAfter)}, want > {ProofApp.F(value: RadialDivergeFloor)}) — the constant-+Y provider walked the body off the top");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "field-round-refused-nothing", expected: 0);

        return passed;
    }

    // (e) THE REJECTION TABLE. Each of these must be LOUD; the settle at the end is what proves nothing ELSE in the
    // rounds above quietly no-opped.
    static bool RunRejectionRound(ComposedShotKit.Ctx ctx) {
        var passed = ExpectEcho(ctx: ctx, name: "slope-out-of-range-rejected", line: "world.collision.slope 120",
            needle: "collision.maxSlopeDegrees must be in (0, 90)", deadlineSeconds: 20.0);

        passed &= ExpectEcho(ctx: ctx, name: "gradient-under-analytic-rejected", line: "world.collision.gradient 0.05",
            needle: "collision.gradientProbe > 0 is meaningless under provider 'analytic'", deadlineSeconds: 20.0);
        passed &= ExpectEcho(ctx: ctx, name: "probe-under-analytic-rejected", line: "world.collision.probe 0 48 60",
            needle: "[world.collision.probe: no field — set collision on with provider 'field']");
        passed &= ExpectEcho(ctx: ctx, name: "short-capsule-rejected", line: "world.kit.collider runner 1.0 1.0",
            needle: "is a capsule shorter than its diameter", deadlineSeconds: 20.0);
        passed &= ExpectEcho(ctx: ctx, name: "unknown-host-field-rejected", line: "world.host.tune bogus 1",
            needle: "[world.host.tune: unknown field 'bogus'");
        passed &= ExpectEcho(ctx: ctx, name: "host-width-out-of-range-rejected", line: "world.host.tune width 0",
            needle: "[world.host.tune: bad width '0' — an integer in 1..16384]");

        // The rejected collider must have left the kit table alone.
        passed &= ExpectEcho(ctx: ctx, name: "rejected-collider-left-the-kit-alone", line: "world.collision.status", needle: "runner(r=0.35 h=1.7)");
        // Six deliberate refusals: the two collision-tuning rejects, the fieldless probe, the short capsule, and the two
        // host tunes.
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "rejection-round-refused-only-its-six", expected: 6);

        return passed;
    }

    // (f) THE HOST SECTION. The three-column read separates what the document says from what the CLI resolved and what
    // the live levers hold; a tune moves the DOCUMENT (so world.status names 'host' drift against the untouched live
    // lever) and world.undo clears it; the section is grant-gated; the write verbs reject loudly. The closing save is
    // the serialization pin — presentMode PascalCase beside the lowercase backend/surfaceFormat tokens.
    static bool RunHostRound(ComposedShotKit.Ctx ctx, string? backend, int width, int height, string savedPath) {
        var hostedOn = (backend ?? "directx");
        var line = ReadLine(ctx: ctx, command: "world.host", needle: "[world.host:");

        // DOCUMENT vs RESOLVED: the checked-in row says 1280x800, the CLI flags this session booted with say otherwise,
        // and the read must show BOTH — the whole reason the verb has three columns.
        var passed = Check(name: "host-document-column-is-the-authored-row",
            ok: ((line is not null) && line.Contains(value: "document {backend=auto width=1280 height=800 surfaceFormat=r8g8b8a8 fullscreen=false presentMode=immediate targetHertz=0")),
            detail: (line?.Trim() ?? "(no world.host echo)"));

        passed &= Check(name: "host-resolved-column-carries-the-cli-override",
            ok: ((line is not null) && line.Contains(value: $"resolved {{backend={hostedOn} width={width.ToString(provider: ProofApp.Inv)} height={height.ToString(provider: ProofApp.Inv)} ")),
            detail: (line?.Trim() ?? "(no world.host echo)"));
        passed &= Check(name: "host-live-column-reads-the-levers", ok: ((line is not null) && line.Contains(value: "live {targetHertz=display timing=off}]")),
            detail: (line?.Trim() ?? "(no world.host echo)"));

        // A tune moves the document only — which is exactly what makes it DRIFT against the untouched live lever.
        passed &= ExpectStatusDrift(ctx: ctx, name: "host-baseline-no-drift", drift: "none");
        passed &= ExpectMutation(ctx: ctx, name: "host-target-tuned", line: "world.host.tune targetHertz 90", needle: "[world.mutation: SetHostDefaults applied");
        passed &= ExpectEcho(ctx: ctx, name: "host-tune-moves-the-document-not-the-lever", line: "world.host", needle: "targetHertz=90 exitAfterSeconds=0 rayQuery=true timing=false genlock=(none)} resolved");
        passed &= ExpectStatusDrift(ctx: ctx, name: "host-tune-marks-drift", drift: "host");
        passed &= ExpectEcho(ctx: ctx, name: "host-undo-accepted", line: "world.undo", needle: "[world.undo: dropped 1");
        passed &= ExpectStatusDrift(ctx: ctx, name: "host-undo-clears-drift", drift: "none");

        // The section is grant-gated like every other.
        passed &= ExpectEcho(ctx: ctx, name: "console-loses-host-mutate", line: "world.revoke console mutate section:host", needle: "[world.revoke: console mutate section:host]");
        passed &= ExpectEcho(ctx: ctx, name: "host-tune-denied-without-grant", line: "world.host.tune presentMode mailbox",
            needle: "[world.grant denied: console cannot mutate section:host", deadlineSeconds: 20.0);
        passed &= ExpectStatusDrift(ctx: ctx, name: "denied-host-tune-changed-nothing", drift: "none");
        // One deliberate refusal: the denied tune.
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "host-round-refused-only-the-denied-tune", expected: 1);
        passed &= ExpectEcho(ctx: ctx, name: "console-regains-host-mutate", line: "world.grant console mutate section:host", needle: "[world.grant: console mutate section:host]");
        passed &= ExpectMutation(ctx: ctx, name: "host-tune-applies-after-regrant", line: "world.host.tune presentMode mailbox", needle: "[world.mutation: SetHostDefaults applied");
        passed &= ExpectEcho(ctx: ctx, name: "present-mode-reads-back-lowercase", line: "world.host", needle: "presentMode=mailbox targetHertz=0");

        // The save: the host row's two token grammars land in the file in their own spellings. The relaunch that reads
        // this file back is what proves the PascalCase presentMode is not write-only.
        passed &= ExpectEcho(ctx: ctx, name: "host-save-writes", line: $"world.save {savedPath}", needle: "[world.save:", deadlineSeconds: 30.0);

        var json = (File.Exists(path: savedPath) ? File.ReadAllText(path: savedPath) : null);

        passed &= Check(name: "saved-present-mode-is-pascal-case", ok: ((json is not null) && json.Contains(value: "\"presentMode\": \"Mailbox\"")),
            detail: ((json is null) ? $"{savedPath} was never written" : "\"presentMode\": \"Mailbox\""));
        passed &= Check(name: "saved-backend-and-format-stay-lowercase", ok: ((json is not null) && json.Contains(value: "\"backend\": \"auto\"") && json.Contains(value: "\"surfaceFormat\": \"r8g8b8a8\"")),
            detail: ((json is null) ? $"{savedPath} was never written" : "\"backend\": \"auto\" + \"surfaceFormat\": \"r8g8b8a8\""));

        return passed;
    }

    // The other half of the serialization split: a relaunch against the saved file must READ the PascalCase presentMode
    // back as mailbox. A writer/reader disagreement would surface here as the built-in default (immediate) or a loud
    // load failure, not as a silent pass.
    static bool RunReload(string exe, string repoRoot, string? backend, int width, int height, int exitAfterSeconds, string savedPath) {
        if (!File.Exists(path: savedPath)) {
            return Check(name: "host-reload", ok: false, detail: $"{savedPath} was never written — the session did not reach world.save");
        }

        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: backend, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", savedPath]);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            var bootLine = ComposedShotKit.Await(collector: ctx.Collector, mark: 0, predicate: l => (l.Contains(value: "[world] definition:") && l.Contains(value: Path.GetFileName(path: savedPath))), deadlineSeconds: 40.0);

            passed &= Check(name: "reload-boots-the-saved-file", ok: (bootLine is not null), detail: (bootLine?.Trim() ?? $"(no '[world] definition: {savedPath}' boot line)"));

            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= ExpectEcho(ctx: ctx, name: "reloaded-present-mode-survives", line: "world.host", needle: "presentMode=mailbox");
            passed &= ExpectEcho(ctx: ctx, name: "reloaded-collision-section-survives", line: "world.collision.status", needle: "on provider=analytic");
            passed &= ExpectEcho(ctx: ctx, name: "reloaded-solid-rows-survive", line: "world.contacts", needle: "[world.contacts: collision on — 12 solid rows (6 spheres, 6 boxes)]");
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "reload-refused-nothing", expected: 0);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return passed;
    }

    // --- The measured journeys ---

    // The wall lane: warp to the lane start facing +Z, command a full-forward segment, sample world.contacts while it is
    // still live (the mid-run planar speed), and read the settled pose once it expires.
    static (Pose? End, double? MidSpeed) RunLane(ComposedShotKit.Ctx ctx, double seconds) {
        ComposedShotKit.Send(ctx: ctx, line: $"player.warp 0 {ProofApp.F(value: LaneStartZ)} 1");
        ComposedShotKit.Send(ctx: ctx, line: "player.face 180 1");
        Thread.Sleep(millisecondsTimeout: 600);
        ComposedShotKit.Send(ctx: ctx, line: $"player.run 1 0 0 {ProofApp.F(value: seconds)} 1");
        Thread.Sleep(millisecondsTimeout: (int)((seconds * 1000.0) * 0.7));

        var midSpeed = ReadPlanarSpeed(ctx: ctx);

        Thread.Sleep(millisecondsTimeout: (int)((seconds * 1000.0) * 0.6));

        return (ReadWhere(ctx: ctx), midSpeed);
    }

    // The open-ground journey: the response-round measurement, run far from every solid row so the only thing shaping
    // the displacement is the kit's response table.
    static double RunOpenGround(ComposedShotKit.Ctx ctx, double seconds) {
        ComposedShotKit.Send(ctx: ctx, line: "player.warp 40 40 1");
        ComposedShotKit.Send(ctx: ctx, line: "player.face 180 1");
        Thread.Sleep(millisecondsTimeout: 700);
        ComposedShotKit.Send(ctx: ctx, line: $"player.run 1 0 0 {ProofApp.F(value: seconds)} 1");
        Thread.Sleep(millisecondsTimeout: (int)((seconds * 1000.0) + 900.0));

        return ((ReadWhere(ctx: ctx) is { } pose) ? (pose.Z - 40.0) : double.NaN);
    }

    // The up-channel journey: a one-second player.fly up segment from the ground, answering how much height it bought.
    static double? RunUpChannel(ComposedShotKit.Ctx ctx) {
        ComposedShotKit.Send(ctx: ctx, line: "player.pose 40 0 40 180 0 0 1");
        Thread.Sleep(millisecondsTimeout: 600);
        ComposedShotKit.Send(ctx: ctx, line: "player.fly 0 0 1 0 0 0 1 1");
        Thread.Sleep(millisecondsTimeout: 1500);

        return (ReadWhere(ctx: ctx)?.Y);
    }

    // The planetoid journey: stand on the sphere's north pole, walk a two-second segment, and report the height shed and
    // the change in radius from the sphere's center.
    static (double? Drop, double? RadialDelta, double? RadiusBefore, double? RadiusAfter) RunPlanetoidWalk(ComposedShotKit.Ctx ctx) {
        ComposedShotKit.Send(ctx: ctx, line: $"player.pose 0 {ProofApp.F(value: (PlanetoidCenterY + PlanetoidRadius + 0.2))} {ProofApp.F(value: PlanetoidCenterZ)} 180 0 0 1");
        Thread.Sleep(millisecondsTimeout: 1000);

        var before = ReadWhere(ctx: ctx);

        ComposedShotKit.Send(ctx: ctx, line: "player.run 1 0 0 2 1");
        Thread.Sleep(millisecondsTimeout: 2500);

        var after = ReadWhere(ctx: ctx);

        if ((before is not { } b) || (after is not { } a)) {
            return (null, null, null, null);
        }

        var radiusBefore = Radius(pose: b);
        var radiusAfter = Radius(pose: a);

        return ((b.Y - a.Y), (radiusAfter - radiusBefore), radiusBefore, radiusAfter);
    }

    static double Radius(Pose pose) {
        var dy = (pose.Y - PlanetoidCenterY);
        var dz = (pose.Z - PlanetoidCenterZ);

        return Math.Sqrt(d: ((pose.X * pose.X) + (dy * dy) + (dz * dz)));
    }

    // --- Reads + assertions ---

    static Pose? ReadWhere(ComposedShotKit.Ctx ctx) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "player.where 1");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => ProofApp.WhereEcho.IsMatch(input: l), deadlineSeconds: 10.0);

        if (line is null) {
            return null;
        }

        var match = ProofApp.WhereEcho.Match(input: line);

        return new Pose(
            X: double.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv),
            Y: double.Parse(s: match.Groups[3].Value, provider: ProofApp.Inv),
            Z: double.Parse(s: match.Groups[4].Value, provider: ProofApp.Inv),
            Yaw: int.Parse(s: match.Groups[5].Value, provider: ProofApp.Inv),
            Pitch: int.Parse(s: match.Groups[6].Value, provider: ProofApp.Inv),
            Roll: int.Parse(s: match.Groups[7].Value, provider: ProofApp.Inv));
    }

    static int ParseInt(string line, Regex pattern) {
        return ((pattern.Match(input: line) is { Success: true } match) ? int.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv) : -1);
    }

    static double? ReadPlanarSpeed(ComposedShotKit.Ctx ctx) {
        var line = ReadLine(ctx: ctx, command: "world.contacts 1", needle: "[world.contacts: p1 ");

        if (line is null) {
            return null;
        }

        var match = PlanarSpeedEcho.Match(input: line);

        return (match.Success ? double.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv) : null);
    }

    static string? ReadLine(ComposedShotKit.Ctx ctx, string command, string needle, double deadlineSeconds = 15.0) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: command);

        return ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: needle), deadlineSeconds: deadlineSeconds);
    }

    static bool ExpectEcho(ComposedShotKit.Ctx ctx, string name, string line, string needle, double deadlineSeconds = 15.0) {
        var hit = ReadLine(ctx: ctx, command: line, needle: needle, deadlineSeconds: deadlineSeconds);

        return Check(name: name, ok: (hit is not null), detail: (hit?.Trim() ?? $"(no line containing '{needle}')"));
    }

    // A Simulation-routed mutation acks quietly; the server's own loud accept line is the settle point, and the stdin
    // barrier holds every following read behind it.
    static bool ExpectMutation(ComposedShotKit.Ctx ctx, string name, string line, string needle) {
        return ExpectEcho(ctx: ctx, name: name, line: line, needle: needle, deadlineSeconds: 20.0);
    }

    static bool ExpectStatusDrift(ComposedShotKit.Ctx ctx, string name, string drift) {
        var line = ReadLine(ctx: ctx, command: "world.status", needle: "[world.status:");
        var actual = ((line is null) ? null : (DriftEcho.Match(input: line) is { Success: true } match ? match.Groups[1].Value : null));

        return Check(name: name, ok: string.Equals(a: actual, b: drift, comparisonType: StringComparison.Ordinal),
            detail: $"session-drift {actual ?? "(no world.status echo)"} (want {drift})");
    }

    static bool Check(string name, bool ok, string detail) {
        return ComposedShotKit.Check(name: name, ok: ok, detail: detail);
    }

    static string Fmt(double? value) {
        return ((value is { } v) ? v.ToString(format: "0.000", provider: ProofApp.Inv) : "(?)");
    }

    static void TryDelete(string path) {
        try {
            if (File.Exists(path: path)) {
                File.Delete(path: path);
            }
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }
    }
}

// ============================================================================================
// WIRE — the console-wire contract proof: the three things every OTHER suite silently rests on.
//   (a) world.wait — the SEQUENCING primitive. Asserted behaviourally, not by its echo: the same
//       drive-then-read burst run twice with a wait must land on the SAME pose bit-for-bit and a
//       real distance from the start, while the identical burst WITHOUT the wait travels nowhere.
//       The stable-vs-racy contrast is the check — if the gate stopped holding, the waited rounds
//       collapse onto the control and both halves go red.
//   (b) WorldJsonPayload — the one inline-JSON parse seam. Four union-taking verb families x four
//       malformation shapes (absent, unknown, duplicate, misplaced $type). Every one must be NAMED
//       and REFUSED, and the host must still be answering afterwards: a console line may never kill
//       the host, and an absent discriminator once did exactly that (NotSupportedException past
//       every catch (JsonException)).
//   (c) The loud boot assertions — a bogus --world or --recording path exits non-zero naming
//       itself, a real path boots, and `--world baked` boots the in-code document by name.
// Runs one windowed session against --world baked (the in-code document declares no solidity, so a
// scripted run lane is clear ground) plus four short boot-only launches.
// ============================================================================================
static class WireProof {
    // [world.wait: 240 ticks from 1710 — releasing at tick 1950]
    static readonly Regex WaitEcho = new(pattern: @"\[world\.wait: (\d+) ticks from (\d+)\D+tick (\d+)\]", options: RegexOptions.Compiled);

    public static int RunWire(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 180, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        Console.WriteLine(value: "[proof] === wire (a): world.wait — the sequencing primitive / (b): WorldJsonPayload — the inline-JSON parse seam ===");
        var sessionPassed = RunSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

        Console.WriteLine();
        Console.WriteLine(value: "[proof] === wire (c): the loud boot assertions ===");
        var bootPassed = RunBootAssertions(exe: exe, repoRoot: repoRoot, width: width, height: height);

        var passed = (sessionPassed && bootPassed);

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] wire proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // ----- (a) + (b): one live session -----------------------------------------------------

    static bool RunSession(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height,
            exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", "baked"]);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            // Pin the stage: seat 1 alone on empty ground, no crowd to shoulder the runner off its lane.
            for (var seat = 2; (seat <= 4); seat++) {
                passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-leave-{seat}");
            }

            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0", expect: "[world.population:", name: "census-zero");
            passed &= RunWaitRounds(ctx: ctx);
            passed &= RunPayloadRejections(ctx: ctx);
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return passed;
    }

    // (a) THE SEQUENCING PRIMITIVE.
    static bool RunWaitRounds(ComposedShotKit.Ctx ctx) {
        var passed = true;

        // The release tick must be the requested span past the tick the wire was on — the arithmetic half.
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "world.wait 240");

        var echo = ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: l => l.Contains(value: "[world.wait:"), deadlineSeconds: 15.0);
        var match = ((echo is null) ? Match.Empty : WaitEcho.Match(input: echo));
        var spanOk = (match.Success
            && (long.Parse(s: match.Groups[1].Value, provider: ProofApp.Inv)
                == (long.Parse(s: match.Groups[3].Value, provider: ProofApp.Inv) - long.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv))));

        passed &= ComposedShotKit.Check(name: "release-tick-is-the-requested-span", ok: spanOk, detail: (echo?.Trim() ?? "(no world.wait echo)"));

        // The behavioural half. The SAME burst twice: with the gate holding, one second of world time separates the
        // two reads and the landing pose is a fixed point of the corpus, so the two rounds must agree EXACTLY.
        var waited1 = DriveRound(ctx: ctx, name: "waited-1", waitTicks: 240);
        var waited2 = DriveRound(ctx: ctx, name: "waited-2", waitTicks: 240);
        // The control: the identical burst with NO wait. Every line drains in the same frame, so the read-back
        // observes a pose one tick into the motion — the racy read the primitive exists to eliminate.
        var control = DriveRound(ctx: ctx, name: "control-no-wait", waitTicks: 0);

        var repeatable = ((waited1 is { } a) && (waited2 is { } b) && (a == b));

        passed &= ComposedShotKit.Check(name: "waited-read-is-repeatable", ok: repeatable,
            detail: $"two identical drive+wait+read bursts travelled {Fmt(value: waited1)} u and {Fmt(value: waited2)} u (want bit-identical)");

        var spanned = ((waited1 is { } d) && (d > 2.0));

        passed &= ComposedShotKit.Check(name: "the-wait-buys-a-real-span", ok: spanned,
            detail: $"{Fmt(value: waited1)} u over 240 held ticks (want > 2.0 — one second at the authored move speed)");

        var contrast = ((waited1 is { } w) && (control is { } c) && (c < (w / 4.0)));

        passed &= ComposedShotKit.Check(name: "no-wait-travels-nowhere", ok: contrast,
            detail: $"unheld burst travelled {Fmt(value: control)} u against the held {Fmt(value: waited1)} u (want under a quarter — the gate is what makes the span)");

        // Nothing above was a refusal.
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "wait-rounds-clean", expected: 0);

        return passed;
    }

    // One drive-then-read burst from a pinned pose. Returns the planar distance travelled, or null if a read was lost.
    static double? DriveRound(ComposedShotKit.Ctx ctx, string name, int waitTicks) {
        // Pin the start: a cleared tape, a known spot on open ground well clear of the plaza, a known heading.
        ComposedShotKit.Send(ctx: ctx, line: "player.stop 1");
        ComposedShotKit.Send(ctx: ctx, line: "player.warp 0 14 1");
        ComposedShotKit.Send(ctx: ctx, line: "player.face 0 1");

        var start = ReadPose(ctx: ctx);

        // The burst. Every line after the wait stays queued until the simulation has advanced <waitTicks> ticks, so
        // the stop cuts a segment that has run for a fixed, tick-counted span rather than a wall-clock one.
        ComposedShotKit.Send(ctx: ctx, line: "player.run 1 0 0 3 1");

        if (waitTicks > 0) {
            ComposedShotKit.Send(ctx: ctx, line: $"world.wait {waitTicks.ToString(provider: ProofApp.Inv)}");
        }

        ComposedShotKit.Send(ctx: ctx, line: "player.stop 1");

        var end = ReadPose(ctx: ctx);

        if ((start is not { } from) || (end is not { } to)) {
            _ = ComposedShotKit.Check(name: name, ok: false, detail: "(a player.where read was lost)");

            return null;
        }

        var travelled = Math.Sqrt(d: (((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z))));

        Console.WriteLine(value: $"[proof]   (note) {name}: start z={from.Z.ToString(format: "0.00", provider: ProofApp.Inv)} end z={to.Z.ToString(format: "0.00", provider: ProofApp.Inv)} travelled {Fmt(value: travelled)} u");

        return travelled;
    }

    static Pose? ReadPose(ComposedShotKit.Ctx ctx) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: "player.where 1");

        var line = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
            predicate: l => (l.Contains(value: "[player.where:") && ProofApp.WhereEcho.IsMatch(input: l)), deadlineSeconds: 20.0);

        if (line is null) {
            return null;
        }

        var match = ProofApp.WhereEcho.Match(input: line);

        return new Pose(
            X: double.Parse(s: match.Groups[2].Value, provider: ProofApp.Inv),
            Y: double.Parse(s: match.Groups[3].Value, provider: ProofApp.Inv),
            Z: double.Parse(s: match.Groups[4].Value, provider: ProofApp.Inv),
            Yaw: int.Parse(s: match.Groups[5].Value, provider: ProofApp.Inv),
            Pitch: int.Parse(s: match.Groups[6].Value, provider: ProofApp.Inv),
            Roll: int.Parse(s: match.Groups[7].Value, provider: ProofApp.Inv));
    }

    // (b) THE PARSE SEAM. Four union-taking verb families x the four discriminator malformations. The union is nested
    // one level down for look/screen/camera and is the payload's OWN top level for a scene row, so all four shapes are
    // exercised at both depths.
    static bool RunPayloadRejections(ComposedShotKit.Ctx ctx) {
        var passed = true;
        var refusals = 0;

        foreach (var row in MalformedPayloads()) {
            passed &= Reject(ctx: ctx, name: $"{row.Family}-{row.Shape}", line: $"{row.Verb} {row.Payload}", verb: row.Verb);
            refusals++;
        }

        // THE RULE: a console line may never kill the host. An absent discriminator used to take the process down, so
        // liveness is asserted at the wire, after every malformation above, and not merely inferred from the echoes.
        passed &= ComposedShotKit.Check(name: "host-process-alive", ok: !ctx.Process.HasExited, detail: $"{refusals} malformed payload(s) refused, process still running");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: "[world.status:", name: "host-still-answering");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.cameras", expect: "[world.cameras:", name: "host-document-intact");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "payload-refusals-counted", expected: refusals);

        return passed;
    }

    // The malformation table. Each row is (verb, family label, shape label, payload).
    //   absent    — no discriminator at all (the one that threw NotSupportedException past every catch (JsonException))
    //   unknown   — a discriminator naming no derived type
    //   duplicate — the discriminator twice
    //   misplaced — a discriminator that is not the first property, which the reader requires
    static IEnumerable<(string Verb, string Family, string Shape, string Payload)> MalformedPayloads() {
        yield return ("world.look.set", "look-source", "absent-type", "{\"name\":\"x\",\"source\":{},\"scale\":1}");
        yield return ("world.look.set", "look-source", "unknown-type", "{\"name\":\"x\",\"source\":{\"$type\":\"holograph\"},\"scale\":1}");
        yield return ("world.look.set", "look-source", "duplicate-type", "{\"name\":\"x\",\"source\":{\"$type\":\"catalog\",\"$type\":\"creation\"},\"scale\":1}");
        yield return ("world.look.set", "look-source", "misplaced-type", "{\"name\":\"x\",\"source\":{\"index\":0,\"$type\":\"catalog\"},\"scale\":1}");

        yield return ("world.screen.set", "screen-source", "absent-type", "{\"index\":0,\"source\":{}}");
        yield return ("world.screen.set", "screen-source", "unknown-type", "{\"index\":0,\"source\":{\"$type\":\"kinescope\"}}");
        yield return ("world.screen.set", "screen-source", "duplicate-type", "{\"index\":0,\"source\":{\"$type\":\"none\",\"$type\":\"testPattern\"}}");
        yield return ("world.screen.set", "screen-source", "misplaced-type", "{\"index\":0,\"source\":{\"cameraName\":\"overhead\",\"$type\":\"view\"}}");

        yield return ("world.camera.set", "camera-rig", "absent-type", "{\"name\":\"x\",\"rig\":{},\"renderWidth\":256,\"renderHeight\":144}");
        yield return ("world.camera.set", "camera-rig", "unknown-type", "{\"name\":\"x\",\"rig\":{\"$type\":\"crane\"},\"renderWidth\":256,\"renderHeight\":144}");
        yield return ("world.camera.set", "camera-rig", "duplicate-type", "{\"name\":\"x\",\"rig\":{\"$type\":\"orbit\",\"$type\":\"dolly\"},\"renderWidth\":256,\"renderHeight\":144}");
        yield return ("world.camera.set", "camera-anchor", "misplaced-type", "{\"name\":\"x\",\"anchor\":{\"index\":0,\"$type\":\"entity\"},\"rig\":{\"$type\":\"chase\"},\"renderWidth\":256,\"renderHeight\":144}");

        // The scene row's union IS the payload — the discriminator sits at the top level, not nested.
        yield return ("world.scene.row.set", "scene-row", "absent-type", "{\"id\":\"x\",\"radius\":1}");
        yield return ("world.scene.row.set", "scene-row", "unknown-type", "{\"$type\":\"obelisk\",\"id\":\"x\"}");
        yield return ("world.scene.row.set", "scene-row", "duplicate-type", "{\"$type\":\"boulder\",\"$type\":\"slab\",\"id\":\"x\"}");
        yield return ("world.scene.row.set", "scene-row", "misplaced-type", "{\"id\":\"x\",\"$type\":\"boulder\",\"radius\":1}");
    }

    // A deliberate refusal: the verb must NAME itself in its rejection, which is what proves the payload was refused
    // at the parse seam rather than swallowed, mis-parsed into a default row, or thrown past the handler.
    static bool Reject(ComposedShotKit.Ctx ctx, string name, string line, string verb) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        var seen = ComposedShotKit.Await(collector: ctx.Collector, mark: mark,
            predicate: l => l.Contains(value: $"[{verb}:"), deadlineSeconds: 15.0);

        return ComposedShotKit.Check(name: name, ok: (seen is not null), detail: (seen?.Trim() ?? $"(no '[{verb}: ...]' refusal — the payload was NOT named and refused)"));
    }

    // ----- (c) the loud boot assertions ----------------------------------------------------

    static bool RunBootAssertions(string exe, string repoRoot, int width, int height) {
        var pid = Environment.ProcessId;
        var missingWorld = Path.Combine(Path.GetTempPath(), $"puck-wire-missing-{pid}.world.json");
        var missingRecording = Path.Combine(Path.GetTempPath(), $"puck-wire-missing-{pid}.recording.json");
        var shippedWorld = Path.Combine(path1: repoRoot, path2: "src", path3: "Puck.World", path4: Path.Combine("Assets", "worlds", "default.world.json"));
        var passed = true;

        passed &= Boot(exe: exe, repoRoot: repoRoot, name: "missing-world-path-fails-loudly", width: width, height: height,
            args: ["--world", missingWorld], wantExitCode: 1, wantLine: $"[world] --world no file at {missingWorld}");
        passed &= Boot(exe: exe, repoRoot: repoRoot, name: "missing-recording-path-fails-loudly", width: width, height: height,
            args: ["--recording", missingRecording], wantExitCode: 1, wantLine: $"[recording] --recording no file at {missingRecording}");
        passed &= Boot(exe: exe, repoRoot: repoRoot, name: "real-world-path-boots", width: width, height: height,
            args: ["--world", shippedWorld], wantExitCode: 0, wantLine: $"[world] definition: {shippedWorld} (--world)");
        passed &= Boot(exe: exe, repoRoot: repoRoot, name: "baked-sentinel-boots", width: width, height: height,
            args: ["--world", "baked"], wantExitCode: 0, wantLine: "[world] definition: baked default (in-code; requested by --world baked)");

        return passed;
    }

    // Boot-only: launch, let it run its bounded life or fail out, and assert BOTH the exit code and the naming line.
    // The exit code alone would pass on any non-zero exit; the line alone would pass on a warn-and-continue.
    static bool Boot(string exe, string repoRoot, string name, string[] args, int wantExitCode, string wantLine, int width, int height) {
        var psi = new ProcessStartInfo {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        foreach (var arg in args) {
            psi.ArgumentList.Add(item: arg);
        }

        psi.ArgumentList.Add(item: "--width");
        psi.ArgumentList.Add(item: width.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--height");
        psi.ArgumentList.Add(item: height.ToString(provider: ProofApp.Inv));
        psi.ArgumentList.Add(item: "--exit-after-seconds");
        psi.ArgumentList.Add(item: "3");

        var process = new Process { StartInfo = psi };
        var stopwatch = new Stopwatch();
        var collector = new OutputCollector();

        _ = process.Start();
        stopwatch.Start();
        collector.Start(reader: process.StandardOutput, stopwatch: stopwatch);
        collector.Start(reader: process.StandardError, stopwatch: stopwatch);

        if (!process.WaitForExit(milliseconds: 90_000)) {
            ComposedShotKit.KillQuietly(process: process);

            return ComposedShotKit.Check(name: name, ok: false, detail: "the boot did not finish within 90 seconds");
        }

        Thread.Sleep(millisecondsTimeout: 400); // let the reader threads drain the closed pipes

        var code = process.ExitCode;
        var lineSeen = collector.Snapshot().Any(predicate: l => l.Contains(value: wantLine, comparisonType: StringComparison.Ordinal));

        return ComposedShotKit.Check(name: name, ok: ((code == wantExitCode) && lineSeen),
            detail: $"exit {code} (want {wantExitCode}), naming line {(lineSeen ? "present" : "ABSENT")}: {wantLine}");
    }

    static string Fmt(double? value) {
        return ((value is { } v) ? v.ToString(format: "0.000", provider: ProofApp.Inv) : "(?)");
    }
}

// ============================================================================================
// REPLAY — the deterministic-replay proof: the `replay.*` control plane driven end to end.
// The determinism claim is asserted as a NUMBER that two separate processes must agree on (the
// live tail pose hash), never as a narration string, and every verdict is proven to
// DISCRIMINATE: a driven capture's tail differs from an idle one's, a tape doctored by one
// flipped byte MISMATCHes beside an undoctored control that MATCHes, a mid-session capture
// honestly MISMATCHes, and four structurally broken tapes are each named and refused with the
// host still answering afterwards. Session machinery is ComposedShotKit's.
// ============================================================================================
static class ReplayProof {
    // [replay.stop: wrote <path> | 605 ticks | MATCH live tail=0x… — …]
    // [replay.stop: wrote <path> | 240 ticks | MISMATCH live tail=0x… replayed=0x… — …]
    static readonly Regex StopEcho = new(options: RegexOptions.Compiled,
        pattern: @"\[replay\.stop: wrote (.+?) \| (\d+) ticks \| (MATCH|MISMATCH) live tail=0x([0-9A-F]{16})(?: replayed=0x([0-9A-F]{16}))?");
    static readonly Regex VerifyMatchEcho = new(options: RegexOptions.Compiled,
        pattern: @"\[replay\.verify: MATCH '([^']+)' \| (\d+) ticks \| hash=0x([0-9A-F]{16})\]");
    static readonly Regex VerifyMismatchEcho = new(options: RegexOptions.Compiled,
        pattern: @"\[replay\.verify: MISMATCH '([^']+)' \| (\d+) ticks \| recorded=0x([0-9A-F]{16}) replayed=0x([0-9A-F]{16})\]");
    static readonly Regex RecordingStatusEcho = new(options: RegexOptions.Compiled,
        pattern: @"\[replay\.status: recording '([^']+)' \| (\d+) ticks captured\]");

    // The tape names this proof owns. Every one is deleted in the finally — the Replays directory is the user's real
    // local store (there is no override path), so the proof never leaves its litter behind.
    const string IdleTape = "proofidle";
    const string DrivenTape = "proofdriven";
    const string RerunTape = "proofrerun";
    const string MidTape = "proofmid";
    const string ControlTape = "proofcontrol";
    const string DoctoredTape = "proofdoctored";
    const string CancelTape = "proofcancelled";
    const string JunkTape = "proofjunk";
    const string TruncatedTape = "prooftruncated";
    const string RetiredTape = "proofretired";
    const string OversizedTape = "proofoversized";

    // The recorded spans, in fixed 240 Hz ticks. The driven span holds a 1-second tape and then waits TWO more
    // seconds: the tail hash is sampled at the last recorded tick, so it is only run-to-run stable once the body has
    // come to rest (a hash sampled mid-motion is a function of how many ticks the pipe happened to deliver).
    const int IdleSpanTicks = 240;
    const int DriveLeadTicks = 60;
    const int DriveSettleTicks = 480;
    const int MidSpanTicks = 240;
    // u — the displacement the 1-second forward tape must produce. The body is otherwise motionless (census 0), so a
    // capture that recorded nothing cannot fake it.
    const double DriveDisplacement = 1.0;

    public static int RunReplay(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 240, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        var passed = true;

        try {
            Console.WriteLine(value: "[proof] === replay (a): capture, re-verify, doctor, and survive a broken tape ===");

            var (sessionPassed, drivenHash) = RunSessionA(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            passed &= sessionPassed;

            Console.WriteLine();
            Console.WriteLine(value: "[proof] === replay (b): THE DETERMINISM CLAIM — a second process, the same input, the same tail ===");

            var (rerunPassed, rerunHash) = RunSessionB(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds);

            passed &= rerunPassed;
            // The claim itself: same document + same input -> bit-identical simulation state on every run. Two
            // independent processes each sampled their own LIVE tail; nothing is compared against a stored baseline.
            passed &= ComposedShotKit.Check(name: "tail-hash-identical-across-runs",
                ok: ((drivenHash is { } first) && (rerunHash is { } second) && (first == second)),
                detail: $"run A 0x{Hex(value: drivenHash)} vs run B 0x{Hex(value: rerunHash)}");
        }
        finally {
            foreach (var name in new[] { IdleTape, DrivenTape, RerunTape, MidTape, ControlTape, DoctoredTape, CancelTape, JunkTape, TruncatedTape, RetiredTape, OversizedTape }) {
                ComposedShotKit.TryDelete(path: TapePath(name: name));
            }
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] replay proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    // ----- (a) the driven session ------------------------------------------------------------

    static (bool Passed, ulong? DrivenHash) RunSessionA(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", "baked"]);
        var process = ctx.Process;
        var passed = true;
        ulong? drivenHash = null;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return (Passed: false, DrivenHash: null);
            }

            passed &= PinStage(ctx: ctx);

            var (capturePassed, idleHash, driven) = RunCaptureRounds(ctx: ctx, drivenTape: DrivenTape);

            passed &= capturePassed;
            drivenHash = driven;
            // The re-drive really consumes the recorded stream: an idle span and a driven span from the SAME boot
            // image settle on DIFFERENT tails, so a capture that dropped its input could not have MATCHed above.
            passed &= ComposedShotKit.Check(name: "driven-tail-differs-from-idle-tail",
                ok: ((idleHash is { } idle) && (driven is { } moved) && (idle != moved)),
                detail: $"idle 0x{Hex(value: idleHash)} vs driven 0x{Hex(value: driven)}");
            passed &= RunMidSessionRound(ctx: ctx, drivenHash: driven);
            passed &= RunDoctoredRound(ctx: ctx, drivenHash: driven);
            passed &= RunCorruptRound(ctx: ctx);
            passed &= RunTapeLifecycleRound(ctx: ctx);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "no-silent-rejections", expected: 0);
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return (Passed: passed, DrivenHash: drivenHash);
    }

    // ----- (b) the identical second run ------------------------------------------------------

    static (bool Passed, ulong? DrivenHash) RunSessionB(string exe, string repoRoot, int width, int height, int exitAfterSeconds) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", "baked"]);
        var process = ctx.Process;
        var passed = true;
        ulong? drivenHash = null;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return (Passed: false, DrivenHash: null);
            }

            passed &= PinStage(ctx: ctx);

            var (capturePassed, _, driven) = RunCaptureRounds(ctx: ctx, drivenTape: RerunTape);

            passed &= capturePassed;
            drivenHash = driven;
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "rerun-no-silent-rejections", expected: 0);
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return (Passed: passed, DrivenHash: drivenHash);
    }

    // PIN the stage before a single hash is read. A boot-anchored capture is only possible while the live world is
    // still AT its definition boot image, so the census is pinned to 0 (nothing autonomous may move) and seat 1 is
    // left alone (a dev-machine pad auto-seats extra players, and a seat that joins mid-recording changes the
    // captured starting state). Both sessions run this identical prefix — the determinism comparison is only a
    // comparison if the two runs start from the same pinned stage.
    static bool PinStage(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

        for (var seat = 2; (seat <= 4); seat++) {
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-leave-{seat}");
        }

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0 idle", expect: "[world.population: 0 network-human stand-ins active", name: "pin-population-zero");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "player.stop 1", expect: "[player.stop:", name: "pin-body-at-rest");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"world.wait {IdleSpanTicks}", expect: "[world.wait:", name: "pin-settle");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "replay.status", expect: "[replay.status: idle]", name: "tape-starts-idle");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "pin-round-refused-nothing", expected: 0);

        return passed;
    }

    // The two boot-anchored captures, in this order: an IDLE span (the boot image driven by nothing) and then a
    // DRIVEN span (a 1-second forward tape). The idle span leaves the world exactly where it found it, so the second
    // capture is still boot-anchored — and the two tails must differ.
    static (bool Passed, ulong? IdleHash, ulong? DrivenHash) RunCaptureRounds(ComposedShotKit.Ctx ctx, string drivenTape) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.record {IdleTape}", expect: $"recording '{IdleTape}'", name: "idle-record-arms");

        ComposedShotKit.Send(ctx: ctx, line: $"world.wait {IdleSpanTicks}");

        // The tape is ACCUMULATING, read off replay.status rather than assumed from the arming echo.
        var statusLine = SendAwait(ctx: ctx, line: "replay.status", pattern: RecordingStatusEcho);
        var capturedTicks = ((statusLine is null) ? -1 : int.Parse(s: RecordingStatusEcho.Match(input: statusLine).Groups[2].ValueSpan, provider: ProofApp.Inv));

        passed &= ComposedShotKit.Check(name: "status-counts-captured-ticks", ok: (capturedTicks >= IdleSpanTicks),
            detail: $"{capturedTicks} tick(s) captured after a {IdleSpanTicks}-tick span");

        var idle = ReadStop(ctx: ctx, name: "idle-capture-is-boot-anchored-match", wantMatch: true);

        passed &= idle.Passed;

        var before = ReadWhereZ(ctx: ctx);

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.record {drivenTape}", expect: $"recording '{drivenTape}'", name: "driven-record-arms");

        ComposedShotKit.Send(ctx: ctx, line: $"world.wait {DriveLeadTicks}");
        ComposedShotKit.Send(ctx: ctx, line: "player.run 1 0 0 1 1");
        ComposedShotKit.Send(ctx: ctx, line: $"world.wait {DriveSettleTicks}");

        var after = ReadWhereZ(ctx: ctx);

        // The premise of the whole round: the recorded span actually MOVED the body. Without it the driven tail could
        // equal the idle tail for an honest reason and the discrimination below would be vacuous.
        passed &= ComposedShotKit.Check(name: "driven-span-moved-the-body",
            ok: ((before is { } start) && (after is { } end) && (Math.Abs(value: (end - start)) > DriveDisplacement)),
            detail: $"z {Fmt(value: before)} -> {Fmt(value: after)} (want > {DriveDisplacement.ToString(format: "0.0", provider: ProofApp.Inv)} u)");

        var driven = ReadStop(ctx: ctx, name: "driven-capture-is-boot-anchored-match", wantMatch: true);

        passed &= driven.Passed;
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "capture-round-refused-nothing", expected: 0);

        return (Passed: passed, IdleHash: idle.Recorded, DrivenHash: driven.Recorded);
    }

    // THE DOCUMENTED FIDELITY BOUNDARY, asserted rather than papered over: a capture armed after the session has
    // already moved off its boot image re-drives its stream faithfully but from the boot image, so the verdict is
    // MISMATCH. Its recorded side must still be the LIVE tail the driven round left behind — the mismatch is the
    // starting state, not a second re-drive compared against itself.
    static bool RunMidSessionRound(ComposedShotKit.Ctx ctx, ulong? drivenHash) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.record {MidTape}", expect: $"recording '{MidTape}'", name: "mid-session-record-arms");

        ComposedShotKit.Send(ctx: ctx, line: $"world.wait {MidSpanTicks}");

        var mid = ReadStop(ctx: ctx, name: "mid-session-capture-mismatches", wantMatch: false);

        passed &= mid.Passed;
        passed &= ComposedShotKit.Check(name: "mid-session-recorded-side-is-the-live-tail",
            ok: ((mid.Recorded is { } recorded) && (drivenHash is { } driven) && (recorded == driven) && (mid.Replayed is { } replayed) && (replayed != recorded)),
            detail: $"recorded 0x{Hex(value: mid.Recorded)} (driven tail 0x{Hex(value: drivenHash)}) vs replayed 0x{Hex(value: mid.Replayed)}");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "mid-session-round-refused-nothing", expected: 0);

        return passed;
    }

    // THE DISCRIMINATION. One flipped byte in the stored reference hash — byte 8, the little-endian low byte of
    // RecordedTailHash — must turn MATCH into a loud MISMATCH, beside a byte-for-byte control copy of the same tape
    // that still MATCHes. Both re-drives recompute the SAME replayed tail, so the only thing separating the two
    // verdicts is the comparison the verb performs: a verify that stopped comparing passes neither pair.
    static bool RunDoctoredRound(ComposedShotKit.Ctx ctx, ulong? drivenHash) {
        var source = TapePath(name: DrivenTape);

        if (!File.Exists(path: source)) {
            return ComposedShotKit.Check(name: "doctored-round-source-tape", ok: false, detail: $"{source} was never written");
        }

        var bytes = File.ReadAllBytes(path: source);

        File.WriteAllBytes(path: TapePath(name: ControlTape), bytes: bytes);

        bytes[8] ^= 0xFF;

        File.WriteAllBytes(path: TapePath(name: DoctoredTape), bytes: bytes);

        var control = ReadVerify(ctx: ctx, tape: ControlTape, name: "undoctored-control-verifies-match", wantMatch: true);
        var doctored = ReadVerify(ctx: ctx, tape: DoctoredTape, name: "doctored-tape-verifies-mismatch", wantMatch: false);
        var passed = (control.Passed && doctored.Passed);

        passed &= ComposedShotKit.Check(name: "doctored-differs-only-in-the-stored-reference",
            ok: ((drivenHash is { } driven) && (control.Recorded == driven) && (doctored.Replayed == driven) && (doctored.Recorded == (driven ^ 0xFFUL))),
            detail: $"control 0x{Hex(value: control.Recorded)} | doctored recorded 0x{Hex(value: doctored.Recorded)} replayed 0x{Hex(value: doctored.Replayed)}");
        // The doctored verify is the round's ONE deliberate refusal (a MISMATCH marks IsError).
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "doctored-round-refused-only-its-one", expected: 1);

        return passed;
    }

    // THE HOST MUST SURVIVE. Four structurally broken tapes and one absent name are fed to `replay.verify`: a file
    // that is not a recording, a two-byte stub, a retired shape (the right magic, a version this build does not
    // speak), and a well-headed file whose length prefixes are garbage — the shape that used to size an allocation
    // from a doctored count and take the process down with it. Each must be NAMED and refused, and the session must
    // still answer afterwards.
    static bool RunCorruptRound(ComposedShotKit.Ctx ctx) {
        File.WriteAllBytes(path: TapePath(name: JunkTape), bytes: Encoding.UTF8.GetBytes(s: new string(c: 'x', count: 512)));
        File.WriteAllBytes(path: TapePath(name: TruncatedTape), bytes: [0x50, 0x4B]);
        File.WriteAllBytes(path: TapePath(name: RetiredTape), bytes: [.. Header(version: 0u), .. new byte[64]]);
        File.WriteAllBytes(path: TapePath(name: OversizedTape), bytes: [.. Header(version: 1u), .. Enumerable.Repeat(element: (byte)0xFF, count: 80)]);

        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.verify {JunkTape}", expect: $"[replay.verify: '{JunkTape}' is unreadable/corrupt", name: "not-a-recording-is-refused");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.verify {TruncatedTape}", expect: $"[replay.verify: '{TruncatedTape}' is unreadable/corrupt", name: "truncated-tape-is-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.verify {RetiredTape}", expect: $"[replay.verify: '{RetiredTape}' is unreadable/corrupt", name: "retired-shape-is-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.verify {OversizedTape}", expect: $"[replay.verify: '{OversizedTape}' is unreadable/corrupt", name: "doctored-length-prefix-is-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "replay.verify nosuchtapehere", expect: "no replay named 'nosuchtapehere'", name: "absent-tape-is-refused");
        // LIVENESS, not narration: two further reads must come back. A host taken down by one of the four tapes above
        // answers nothing here, and the deadline turns the crash into a failing check instead of a silent green run.
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "replay.status", expect: "[replay.status: idle]", name: "host-survives-broken-tapes");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.status", expect: "schema puck.world.def.v1", name: "world-still-answers-after-broken-tapes");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "corrupt-round-refused-only-its-five", expected: 5);

        return passed;
    }

    // The tape lifecycle read off `replay.list` (state), not off the verbs' own echoes: a cancelled recording leaves
    // NOTHING behind, and the three persisted tapes are all listed. Plus the three misuse refusals — a second
    // record while one is running, a stop with nothing running, and a name that would escape the Replays directory.
    static bool RunTapeLifecycleRound(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.record {CancelTape}", expect: $"recording '{CancelTape}'", name: "cancel-round-arms");

        ComposedShotKit.Send(ctx: ctx, line: "world.wait 60");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"replay.record {IdleTape}", expect: "[replay.record: busy", name: "second-record-while-recording-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "replay.cancel", expect: $"dropped '{CancelTape}'", name: "cancel-drops-the-recording");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "replay.stop", expect: "[replay.stop: not recording]", name: "stop-while-idle-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "replay.verify ../escape", expect: "[replay.verify: name must be non-empty", name: "path-escaping-name-refused");

        var listing = SendAwait(ctx: ctx, line: "replay.list", pattern: new Regex(pattern: @"\[replay\.list: ", options: RegexOptions.Compiled));

        passed &= ComposedShotKit.Check(name: "list-holds-the-persisted-tapes-and-not-the-cancelled-one",
            ok: ((listing is not null) && listing.Contains(value: IdleTape) && listing.Contains(value: DrivenTape) && listing.Contains(value: MidTape) && !listing.Contains(value: CancelTape)),
            detail: (listing?.Trim() ?? "(no '[replay.list: …]' echo)"));
        passed &= ComposedShotKit.Check(name: "cancelled-tape-wrote-no-file", ok: !File.Exists(path: TapePath(name: CancelTape)),
            detail: TapePath(name: CancelTape));
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "lifecycle-round-refused-only-its-three", expected: 3);

        return passed;
    }

    // ----- reads -----------------------------------------------------------------------------

    static (bool Passed, ulong? Recorded, ulong? Replayed) ReadStop(ComposedShotKit.Ctx ctx, string name, bool wantMatch) {
        var line = SendAwait(ctx: ctx, line: "replay.stop", pattern: StopEcho, deadlineSeconds: 60.0);

        if (line is null) {
            return (Passed: ComposedShotKit.Check(name: name, ok: false, detail: "(no '[replay.stop: …]' echo)"), Recorded: null, Replayed: null);
        }

        var match = StopEcho.Match(input: line);
        var verdict = match.Groups[3].Value;
        var recorded = ulong.Parse(s: match.Groups[4].ValueSpan, style: NumberStyles.HexNumber, provider: ProofApp.Inv);
        var replayed = (match.Groups[5].Success ? ulong.Parse(s: match.Groups[5].ValueSpan, style: NumberStyles.HexNumber, provider: ProofApp.Inv) : recorded);
        var ok = (verdict == (wantMatch ? "MATCH" : "MISMATCH"));

        return (Passed: ComposedShotKit.Check(name: name, ok: ok, detail: $"{match.Groups[2].Value} ticks | {verdict} recorded 0x{recorded:X16} replayed 0x{replayed:X16}"), Recorded: recorded, Replayed: replayed);
    }

    static (bool Passed, ulong? Recorded, ulong? Replayed) ReadVerify(ComposedShotKit.Ctx ctx, string tape, string name, bool wantMatch) {
        var pattern = (wantMatch ? VerifyMatchEcho : VerifyMismatchEcho);
        var line = SendAwait(ctx: ctx, line: $"replay.verify {tape}", pattern: pattern, deadlineSeconds: 60.0);

        if (line is null) {
            return (Passed: ComposedShotKit.Check(name: name, ok: false, detail: $"(no '[replay.verify: {(wantMatch ? "MATCH" : "MISMATCH")} '{tape}' …]' echo)"), Recorded: null, Replayed: null);
        }

        var match = pattern.Match(input: line);
        var recorded = ulong.Parse(s: match.Groups[3].ValueSpan, style: NumberStyles.HexNumber, provider: ProofApp.Inv);
        var replayed = (wantMatch ? recorded : ulong.Parse(s: match.Groups[4].ValueSpan, style: NumberStyles.HexNumber, provider: ProofApp.Inv));

        return (Passed: ComposedShotKit.Check(name: name, ok: true, detail: line.Trim()), Recorded: recorded, Replayed: replayed);
    }

    static double? ReadWhereZ(ComposedShotKit.Ctx ctx) {
        var line = SendAwait(ctx: ctx, line: "player.where 1", pattern: ProofApp.WhereEcho);

        return ((line is null) ? null : double.Parse(s: ProofApp.WhereEcho.Match(input: line).Groups[4].ValueSpan, provider: ProofApp.Inv));
    }

    static string? SendAwait(ComposedShotKit.Ctx ctx, string line, Regex pattern, double deadlineSeconds = 20.0) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        return ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => pattern.IsMatch(input: candidate), deadlineSeconds: deadlineSeconds);
    }

    // ----- the store -------------------------------------------------------------------------

    // WorldReplayTape.Directory()'s twin — there is no CLI override for the replay store, so the proof resolves the
    // same real path the host writes to and cleans up exactly the names it owns.
    static string TapePath(string name) {
        var directory = Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), path2: "Puck", path3: "World", path4: "Replays");

        return Path.Combine(path1: directory, path2: (name + ".puckreplay"));
    }

    // The .puckreplay header: the "PKRP" magic plus a version word.
    static byte[] Header(uint version) {
        var header = new byte[8];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: header.AsSpan(start: 0, length: 4), value: 0x504B_5250u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: header.AsSpan(start: 4, length: 4), value: version);

        return header;
    }

    static string Hex(ulong? value) {
        return ((value is { } resolved) ? resolved.ToString(format: "X16", provider: ProofApp.Inv) : "????????????????");
    }

    static string Fmt(double? value) {
        return ((value is { } resolved) ? resolved.ToString(format: "0.00", provider: ProofApp.Inv) : "(?)");
    }
}

// ============================================================================================
// SCREEN-SOURCES — the rest of the `screen.*` family: the source-binding verbs (capture,
// desktop, camera), the magazine selector, the live device swap, and the cable-link group.
// `ScreensProof` covers insert/eject/peek/state around one booted cartridge; this suite covers
// what that one leaves untouched, and every claim is read back off STATE — an ejectable slot,
// the selector `screen.state` reports, the bound/unbound flag the selected source actually
// moves, the machine's own options read-back, the emulated counter still advancing across a
// device swap, and the link membership `screen.state` carries.
// ============================================================================================
static class ScreenSourcesProof {
    static readonly Regex StateEcho = new(options: RegexOptions.Compiled, pattern: @"\[screen\.state: (\d+) (.+?)\]");
    static readonly Regex PeekEcho = new(options: RegexOptions.Compiled, pattern: @"\[screen\.peek: (\d+) 0x([0-9A-Fa-f]+)=0x([0-9A-Fa-f]+)\]");
    static readonly Regex OptionsEcho = new(options: RegexOptions.Compiled, pattern: @"\[screen\.options: (\d+) '([^']*)'\]");
    static readonly Regex DirtyEcho = new(options: RegexOptions.Compiled, pattern: @"dirty (\d+) ");

    // The joypad-echo ROM's liveness counter (ScreensProof's cartridge, reused — the proof owns its own content; no
    // cartridge ships with the world).
    const int CounterAddr = 0xC001;
    // The screens the baked default declares: 0 a view (the machine bay), 1 a window capture, 2 the jumbotron view,
    // 3 the webcam, 4 the unconfigured native AGB.
    const int BayScreen = 0;
    const int CaptureScreen = 1;
    const int CameraScreen = 3;
    const int MagazineEntries = 3;

    // Screen 0's row, re-authored with a three-entry magazine: a bound view, a second bound view, and `none` (the
    // engine's procedural no-signal fallback — the entry whose selection is visible as an UNBOUND slot).
    // Screen 0's row, re-authored with a three-entry magazine whose middle entry is a CARTRIDGE: selecting it must
    // boot a real machine onto the slot (a state read no echo can fake), and selecting past it must clear it again.
    static string MagazineRow(string romPath) {
        var escaped = romPath.Replace(oldValue: "\\", newValue: "\\\\", comparisonType: StringComparison.Ordinal);

        return "world.screen.set {\"index\":0,\"origin\":[-3,1.2,-3],\"right\":[1,0,0],\"up\":[0,1,0],\"halfWidth\":1.3,\"halfHeight\":1,\"halfDepth\":0.12,\"round\":0.08," +
            "\"source\":{\"$type\":\"view\",\"cameraName\":\"overhead\"},\"route\":{\"engageable\":true,\"engageRadius\":2.5}," +
            "\"magazine\":{\"entries\":[{\"$type\":\"view\",\"cameraName\":\"overhead\"}," +
            $"{{\"$type\":\"machine\",\"engine\":\"gaming-brick\",\"contentPath\":\"{escaped}\",\"options\":\"dmg\"}}," +
            "{\"$type\":\"none\"}],\"selected\":0,\"wrap\":true}}";
    }
    const string ConsoleOnCamera = "world.screen.set {\"index\":3,\"origin\":[-4.4,3.6,-6.5],\"right\":[1,0,0],\"up\":[0,1,0],\"halfWidth\":1.6,\"halfHeight\":1.2,\"halfDepth\":0.14,\"round\":0.1,\"source\":{\"$type\":\"console\",\"rows\":24,\"columns\":64,\"procedural\":false},\"route\":{\"engageable\":false,\"engageRadius\":0}}";
    const string ConsoleOnCapture = "world.screen.set {\"index\":1,\"origin\":[3,1.2,-3],\"right\":[1,0,0],\"up\":[0,1,0],\"halfWidth\":1.3,\"halfHeight\":1,\"halfDepth\":0.12,\"round\":0.08,\"source\":{\"$type\":\"console\",\"rows\":24,\"columns\":64,\"procedural\":false},\"route\":{\"engageable\":false,\"engageRadius\":0}}";

    public static int RunScreenSources(ArgMap opts) {
        var noBuild = opts.Flag(name: "--no-build");
        var width = opts.GetInt(fallback: 1280, name: "--width");
        var height = opts.GetInt(fallback: 800, name: "--height");
        var exitAfterSeconds = opts.GetInt(fallback: 300, name: "--exit-after-seconds");
        var repoRoot = ProofApp.RepoRoot();
        var exe = ComposedShotKit.BuildAndFindExe(repoRoot: repoRoot, noBuild: noBuild);

        if (exe is null) {
            return 1;
        }

        var romPath = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-screen-sources-{Environment.ProcessId}.gb");

        File.WriteAllBytes(bytes: ScreensProof.BuildJoypadEchoRom(), path: romPath);
        Console.WriteLine(value: $"[proof] joypad-echo ROM written: {romPath}");

        bool passed;

        try {
            passed = RunSession(exe: exe, repoRoot: repoRoot, width: width, height: height, exitAfterSeconds: exitAfterSeconds, romPath: romPath);
        }
        finally {
            ComposedShotKit.TryDelete(path: romPath);
        }

        Console.WriteLine();
        Console.WriteLine(value: $"[proof] screen-sources proof {(passed ? "PASS" : "FAIL")}");

        return (passed ? 0 : 1);
    }

    static bool RunSession(string exe, string repoRoot, int width, int height, int exitAfterSeconds, string romPath) {
        var stopwatch = new Stopwatch();
        var ctx = ComposedShotKit.Launch(exe: exe, repoRoot: repoRoot, backend: null, width: width, height: height, exitAfterSeconds: exitAfterSeconds, stopwatch: stopwatch, extraArgs: ["--world", "baked"]);
        var process = ctx.Process;
        var passed = true;

        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = false; ComposedShotKit.KillQuietly(process: process); };
        EventHandler exitHandler = (_, _) => ComposedShotKit.KillQuietly(process: process);

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try {
            if (!ComposedShotKit.WaitForConsole(ctx: ctx)) {
                return false;
            }

            passed &= PinStage(ctx: ctx);
            passed &= RunSourceRound(ctx: ctx);
            passed &= RunMagazineRound(ctx: ctx, romPath: romPath);
            passed &= RunConsoleCeilingRound(ctx: ctx);
            passed &= RunDeviceSwapRound(ctx: ctx, romPath: romPath);
            passed &= RunLinkRound(ctx: ctx, romPath: romPath);
            passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "no-silent-rejections", expected: 0);
            passed &= ComposedShotKit.FaultSweep(ctx: ctx);
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            ComposedShotKit.KillQuietly(process: process);
        }

        return passed;
    }

    // PIN the stage: seat 1 alone (a dev-machine pad auto-seats extra players and an engaged seat shows up in
    // screen.state) and an empty census (nothing autonomous behind the slabs). Nothing below reads a boot value.
    static bool PinStage(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: "world.console off", expect: "[world.console: off]", name: "console-off");

        for (var seat = 2; (seat <= 4); seat++) {
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"player.leave {seat}", expect: "[player.leave:", name: $"pin-leave-{seat}");
        }

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "world.population 0 idle", expect: "[world.population: 0 network-human stand-ins active", name: "pin-population-zero");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "screen.links", expect: "[screen.links: none]", name: "pin-no-links");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "pin-round-refused-nothing", expected: 0);

        return passed;
    }

    // SOURCE BINDING, measured on the slot rather than on the setter's own echo: `screen.eject` refuses a slot with no
    // live producer and succeeds on one that has it, so a bind that stopped binding is caught by the eject that
    // follows it — the verbs' success lines cannot cover for each other.
    static bool RunSourceRound(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.eject {BayScreen}", expect: $"[screen.eject: screen {BayScreen} has no source to eject]", name: "declared-view-is-not-a-live-producer");

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.desktop {BayScreen} 0", expect: $"screen {BayScreen} capturing monitor 0", name: "desktop-binds-the-primary-monitor");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.eject {BayScreen}", expect: $"[screen.eject: screen {BayScreen} ejected]", name: "desktop-capture-was-live-and-ejects");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.capture {BayScreen} Puck: World", expect: $"screen {BayScreen} capturing 'Puck: World'", name: "capture-binds-a-window-title-with-spaces");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.eject {BayScreen}", expect: $"[screen.eject: screen {BayScreen} ejected]", name: "window-capture-was-live-and-ejects");

        // The webcam is the one source kind the environment can withhold. The verb is driven either way and its
        // outcome is NAMED: a bind must then be ejectable (the same state read as above), and a machine with no camera
        // must produce the loud device fault. The settle count follows the branch actually taken — never a count that
        // absorbs both.
        var cameraLine = SendAwait(ctx: ctx, line: $"screen.camera {CameraScreen}", pattern: new Regex(pattern: @"\[screen\.camera: ", options: RegexOptions.Compiled));
        var cameraBound = ((cameraLine is not null) && cameraLine.Contains(value: "showing the webcam"));

        passed &= ComposedShotKit.Check(name: "camera-binds-or-names-its-device-fault", ok: (cameraLine is not null), detail: (cameraLine?.Trim() ?? "(no '[screen.camera: …]' echo)"));

        if (cameraBound) {
            passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.eject {CameraScreen}", expect: $"[screen.eject: screen {CameraScreen} ejected]", name: "webcam-feed-was-live-and-ejects");
        } else {
            Console.WriteLine(value: "[proof]   note: no camera device on this machine — the webcam bind is reported, not asserted.");
        }

        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "source-round-refused-only-its-own", expected: (cameraBound ? 1 : 2));

        return passed;
    }

    // THE MAGAZINE: authored as data (a whole-row `world.screen.set` carries it), then driven. The selector's movement
    // is read off `screen.state`'s entry=N/M — and the SELECTED SOURCE IS APPLIED, witnessed by the slot's bound flag
    // dropping to unbound on the `none` entry and coming back on the wrapped-to view. A selector that moved a pointer
    // and applied nothing passes the entry read and fails the bound read.
    static bool RunMagazineRound(ComposedShotKit.Ctx ctx, string romPath) {
        var dirtyBefore = ReadDirty(ctx: ctx);
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: MagazineRow(romPath: romPath), expect: "[world.mutation: UpsertScreen", name: "magazine-row-applies");

        ComposedShotKit.Send(ctx: ctx, line: "world.wait 60");

        var dirtyAfter = ReadDirty(ctx: ctx);

        passed &= ComposedShotKit.Check(name: "magazine-row-is-exactly-one-journal-entry",
            ok: ((dirtyBefore is { } before) && (dirtyAfter is { } after) && (after == (before + 1))),
            detail: $"dirty {Fmt(value: dirtyBefore)} -> {Fmt(value: dirtyAfter)}");
        passed &= CheckState(ctx: ctx, name: "magazine-selector-starts-at-entry-zero", index: BayScreen, want: $"entry=0/{MagazineEntries}");
        passed &= CheckState(ctx: ctx, name: "magazine-screen-starts-machineless", index: BayScreen, want: "empty");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen}", expect: $"entry 0/{MagazineEntries} (unchanged)", name: "bare-select-echoes-without-moving");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen} next", expect: $"{BayScreen} entry 1/{MagazineEntries}", name: "select-next-applies-the-cartridge-entry");
        passed &= CheckState(ctx: ctx, name: "selector-moved-to-entry-one", index: BayScreen, want: $"entry=1/{MagazineEntries}");

        // THE SELECTED SOURCE IS REALLY APPLIED, not merely pointed at: the cartridge entry BOOTS A MACHINE onto the
        // slot (screen.state flips empty -> assigned, bound, stepping frames) and moving past it clears the machine
        // again. A selector that moved its pointer and applied nothing never reaches either state.
        passed &= PollState(ctx: ctx, name: "cartridge-entry-boots-a-machine", index: BayScreen,
            predicate: body => (body.Contains(value: "assigned gaming-brick") && body.Contains(value: "bound")));
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen} next", expect: $"{BayScreen} entry 2/{MagazineEntries}", name: "select-next-applies-the-none-entry");
        passed &= PollState(ctx: ctx, name: "none-entry-clears-the-machine", index: BayScreen,
            predicate: body => (body.Contains(value: "empty") && body.Contains(value: $"entry=2/{MagazineEntries}")));
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen} next", expect: "showing camera 'overhead'", name: "select-next-wraps-past-the-last-entry");
        passed &= CheckState(ctx: ctx, name: "wrapped-selector-is-back-at-entry-zero", index: BayScreen, want: $"entry=0/{MagazineEntries}");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen} prev", expect: $"{BayScreen} entry 2/{MagazineEntries}", name: "select-prev-wraps-backwards");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen} 0", expect: "showing camera 'overhead'", name: "select-takes-an-absolute-entry");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {BayScreen} 7", expect: $"[screen.select: entry 7 is outside 0..{(MagazineEntries - 1)}]", name: "out-of-range-entry-refused");

        ComposedShotKit.Send(ctx: ctx, line: "world.wait 60");

        passed &= CheckState(ctx: ctx, name: "refused-entry-left-the-selector-alone", index: BayScreen, want: $"entry=0/{MagazineEntries}");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.select {CaptureScreen} next", expect: $"[screen.select: screen {CaptureScreen} has no magazine]", name: "magazineless-screen-refused");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "magazine-round-refused-only-its-two", expected: 2);

        return passed;
    }

    // THE ONE-LIVE-CONSOLE CEILING: the console feed owns a single upload surface, so a SECOND declared console source
    // is a validator error naming both indices — and the document must be left exactly as it was.
    static bool RunConsoleCeilingRound(ComposedShotKit.Ctx ctx) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: ConsoleOnCamera, expect: "[world.mutation: UpsertScreen", name: "first-console-source-accepted");

        ComposedShotKit.Send(ctx: ctx, line: "world.wait 60");

        var dirtyBefore = ReadDirty(ctx: ctx);

        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: ConsoleOnCapture, expect: "at most one screen may declare a console source, but screens 1 and 3 both do", name: "second-console-source-rejected-naming-both");

        ComposedShotKit.Send(ctx: ctx, line: "world.wait 60");

        var dirtyAfter = ReadDirty(ctx: ctx);

        passed &= ComposedShotKit.Check(name: "rejected-console-row-left-the-journal-alone",
            ok: ((dirtyBefore is { } before) && (dirtyAfter is { } after) && (after == before)),
            detail: $"dirty {Fmt(value: dirtyBefore)} -> {Fmt(value: dirtyAfter)}");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "console-ceiling-round-refused-only-its-one", expected: 1);

        return passed;
    }

    // THE LIVE DEVICE SWAP — `screen.options` retargets a RUNNING machine across the engine's dmg|cgb|agb vocabulary
    // with no reboot. Proven on the machine's own state, not on the swap's echo: the options read-back reports the new
    // model, and the cartridge's work-RAM liveness counter keeps ADVANCING across each swap (a swap that killed or
    // re-booted the machine freezes it). A rejected options string must leave the running model untouched.
    static bool RunDeviceSwapRound(ComposedShotKit.Ctx ctx, string romPath) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.insert {BayScreen} {romPath} gaming-brick", expect: "booted", name: "cartridge-boots-on-the-bay-screen", deadlineSeconds: 30.0);

        passed &= PollState(ctx: ctx, name: "machine-is-assigned-and-bound", index: BayScreen, predicate: body => (body.Contains(value: "assigned") && body.Contains(value: "bound")));
        passed &= CheckOptions(ctx: ctx, name: "machine-boots-on-dmg", want: "dmg");
        passed &= CheckCounterAdvances(ctx: ctx, name: "counter-advances-before-any-swap");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.options {BayScreen} cgb", expect: "'dmg' -> 'cgb' reconfigured", name: "dmg-to-cgb-swap-applies");
        passed &= CheckOptions(ctx: ctx, name: "machine-reads-back-cgb", want: "cgb");
        passed &= CheckCounterAdvances(ctx: ctx, name: "counter-advances-across-the-cgb-swap");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.options {BayScreen} agb", expect: "'cgb' -> 'agb' reconfigured", name: "cgb-to-agb-swap-applies");
        passed &= CheckOptions(ctx: ctx, name: "machine-reads-back-agb", want: "agb");
        passed &= CheckCounterAdvances(ctx: ctx, name: "counter-advances-across-the-agb-swap");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.options {BayScreen} kinescope", expect: "unknown gaming-brick option 'kinescope'", name: "unknown-model-token-refused");
        passed &= CheckOptions(ctx: ctx, name: "refused-swap-left-the-machine-on-agb", want: "agb");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.options {CaptureScreen}", expect: $"screen {CaptureScreen} has no reconfigurable machine", name: "machineless-screen-has-no-options");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "device-swap-round-refused-only-its-two", expected: 2);

        return passed;
    }

    // THE CABLE GROUP. Today's honest ceiling is pinned by name: the queued gaming-brick host has no live-link path, so
    // a group over two RUNNING machines is recorded DORMANT with that exact reason. Membership is still real — both
    // members carry link=<name> in screen.state — and unlink severs it. The dormant reason is asserted verbatim on
    // purpose: the day live linking lands, this check fails and demands the live half be proven here.
    static bool RunLinkRound(ComposedShotKit.Ctx ctx, string romPath) {
        var passed = ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.insert {CaptureScreen} {romPath} gaming-brick", expect: "booted", name: "second-cartridge-boots", deadlineSeconds: 30.0);

        passed &= PollState(ctx: ctx, name: "second-machine-is-assigned", index: CaptureScreen, predicate: body => body.Contains(value: "assigned"));
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.link pair {BayScreen} {CaptureScreen}",
            expect: "pair 0+1 dormant (live cable linking of running gaming-brick machines is not yet wired for the queued host)", name: "link-over-two-machines-is-recorded-dormant");
        passed &= CheckState(ctx: ctx, name: "first-member-carries-the-link", index: BayScreen, want: "link=pair");
        passed &= CheckState(ctx: ctx, name: "second-member-carries-the-link", index: CaptureScreen, want: "link=pair");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "screen.links", expect: "pair 0+1 dormant", name: "links-query-lists-the-group");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.link other {CaptureScreen} {CameraScreen}", expect: $"screen {CaptureScreen} is already in link 'pair'", name: "member-cannot-join-a-second-link");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.link twice {CameraScreen} {CameraScreen}", expect: $"screen {CameraScreen} is named twice in link 'twice'", name: "duplicate-member-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: $"screen.link solo {BayScreen}", expect: "[screen.link: expected <name> <index> <index>", name: "one-member-link-refused");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "screen.unlink pair", expect: "link 'pair' severed", name: "unlink-severs-the-group");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "screen.links", expect: "[screen.links: none]", name: "severed-group-leaves-no-link");
        passed &= CheckStateLacks(ctx: ctx, name: "severed-member-drops-its-link", index: BayScreen, unwanted: "link=");
        passed &= ComposedShotKit.SendAwait(ctx: ctx, line: "screen.unlink pair", expect: "[screen.unlink: no link 'pair']", name: "unlink-of-an-absent-group-refused");
        passed &= ComposedShotKit.SettleWireErrors(ctx: ctx, name: "link-round-refused-only-its-four", expected: 4);

        return passed;
    }

    // ----- reads -----------------------------------------------------------------------------

    static bool CheckState(ComposedShotKit.Ctx ctx, string name, int index, string want) {
        var line = SendAwait(ctx: ctx, line: $"screen.state {index}", pattern: StateEcho);

        return ComposedShotKit.Check(name: name, ok: ((line is not null) && line.Contains(value: want)), detail: $"{(line?.Trim() ?? "(no '[screen.state: …]' echo)")} — want '{want}'");
    }

    static bool CheckStateLacks(ComposedShotKit.Ctx ctx, string name, int index, string unwanted) {
        var line = SendAwait(ctx: ctx, line: $"screen.state {index}", pattern: StateEcho);

        return ComposedShotKit.Check(name: name, ok: ((line is not null) && !line.Contains(value: unwanted)), detail: $"{(line?.Trim() ?? "(no '[screen.state: …]' echo)")} — want no '{unwanted}'");
    }

    static bool PollState(ComposedShotKit.Ctx ctx, string name, int index, Func<string, bool> predicate) {
        string? last = null;

        for (var attempt = 0; (attempt < 30); attempt++) {
            last = SendAwait(ctx: ctx, line: $"screen.state {index}", pattern: StateEcho, deadlineSeconds: 5.0);

            if ((last is not null) && predicate(arg: StateEcho.Match(input: last).Groups[2].Value)) {
                return ComposedShotKit.Check(name: name, ok: true, detail: last.Trim());
            }

            Thread.Sleep(millisecondsTimeout: 200);
        }

        return ComposedShotKit.Check(name: name, ok: false, detail: (last?.Trim() ?? "(no '[screen.state: …]' echo)"));
    }

    static bool CheckOptions(ComposedShotKit.Ctx ctx, string name, string want) {
        var line = SendAwait(ctx: ctx, line: $"screen.options {BayScreen}", pattern: OptionsEcho);
        var actual = ((line is null) ? null : OptionsEcho.Match(input: line).Groups[2].Value);

        return ComposedShotKit.Check(name: name, ok: (actual == want), detail: $"machine reads '{actual ?? "?"}' (want '{want}')");
    }

    // LIVENESS on the emulated machine itself: the cartridge bumps a work-RAM counter every loop, so a value that
    // moves proves the machine is still stepping. A frozen counter is what a device swap that dropped the machine
    // looks like.
    static bool CheckCounterAdvances(ComposedShotKit.Ctx ctx, string name) {
        var first = ReadPeek(ctx: ctx);

        for (var attempt = 0; (attempt < 40); attempt++) {
            var next = ReadPeek(ctx: ctx);

            if ((first is { } start) && (next is { } now) && (now != start)) {
                return ComposedShotKit.Check(name: name, ok: true, detail: $"0x{CounterAddr:X4} {start:X2} -> {now:X2}");
            }

            Thread.Sleep(millisecondsTimeout: 150);
        }

        return ComposedShotKit.Check(name: name, ok: false, detail: $"0x{CounterAddr:X4} held at {((first is { } held) ? held.ToString(format: "X2", provider: ProofApp.Inv) : "?")} — the machine is not stepping");
    }

    static int? ReadPeek(ComposedShotKit.Ctx ctx) {
        var line = SendAwait(ctx: ctx, line: $"screen.peek {BayScreen} 0x{CounterAddr:X4}", pattern: PeekEcho, deadlineSeconds: 5.0);

        return ((line is null) ? null : int.Parse(s: PeekEcho.Match(input: line).Groups[3].ValueSpan, style: NumberStyles.HexNumber, provider: ProofApp.Inv));
    }

    static int? ReadDirty(ComposedShotKit.Ctx ctx) {
        var line = SendAwait(ctx: ctx, line: "world.status", pattern: DirtyEcho);

        return ((line is null) ? null : int.Parse(s: DirtyEcho.Match(input: line).Groups[1].ValueSpan, provider: ProofApp.Inv));
    }

    static string? SendAwait(ComposedShotKit.Ctx ctx, string line, Regex pattern, double deadlineSeconds = 20.0) {
        var mark = ctx.Collector.Count;

        ComposedShotKit.Send(ctx: ctx, line: line);

        return ComposedShotKit.Await(collector: ctx.Collector, mark: mark, predicate: candidate => pattern.IsMatch(input: candidate), deadlineSeconds: deadlineSeconds);
    }

    static string Fmt(int? value) {
        return ((value is { } resolved) ? resolved.ToString(provider: ProofApp.Inv) : "(?)");
    }
}
