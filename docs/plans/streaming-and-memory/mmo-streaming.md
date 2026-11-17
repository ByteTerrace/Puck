# S1 research: MMO world streaming and bounded client memory

Brief: `s1-research-mmo`. Research lane (Codex) on `lead/authoring`.

Scope: research only. No source edits, commits, builds, Worlds, leases or hardware measurements. This report is being written as sources are opened; conclusions below distinguish evidence, inference and proposed work.

Step numbers in this report are the campaign outline's as it stood when the round ran: 2 one byte authority, 3 attribution, 4 duplicate-copy removal, 5 priority eviction, 6 mapped portal demand, 7 shared lighting demand, 8 areas, terrain and moons, 9 managed churn. [The plan](../streaming-and-memory.md#the-build-order) builds the same work as its steps 1 and 3, 2, 6, 5, 4, 9, 10 and 11, in that order.

## Puck baseline read before research

Read `AGENTS.md`, the `delegate` and `puck-world` skills, `docs/decisions/residency.md`, `docs/architecture/worlds.md` (including “The idea in plain terms”), `docs/plans/streaming-and-memory.md`, and the opening of the MMO foundations synthesis for its decision-record format.

The owner's purpose is an MMO at World of Warcraft's scale. Independent work means learning from multiple implementations rather than treating any library or game as the specification. The relevant settled invariant is in `docs/decisions/residency.md`: “Only presentation follows the observer”. Client asset demand, disclosure and cache policy may vary by observer; authoritative rules, physics, doors, admission, session opening and transfers may not.

The decision already provides one selector across presentation residencies, demand points from views/seats and mapped portal eyes, velocity lead by landing latency, projected-error detail, priority eviction, optional authored areas, terrain tiles and routed worlds. Areas and worlds are nodes; portals are edges. It rejects fixed metre rings as the universal detail rule and doors waiting on local I/O. The implementation plan reports device budget accounting, priority eviction, mapped portal demand, shared lighting demand and authored areas as unfinished. That status is the plan's report, not a completeness audit; the limited source check below identifies existing mechanisms to extend.

## Evidence convention

- **Opened primary**: developer documentation, developer-authored articles, or original reverse engineering opened during this lane.
- **Historical**: evidence is for the named version/date; no claim that an old algorithm still describes today's retail client.
- **Inference / proposal / hypothesis**: explicitly labelled; never attributed to a vendor without evidence.
- A quotation is short and exact, paired with the opened URL. Source identifiers in the analysis refer to the evidence register below.
- A download cache on disk, an address-space limit, recommended machine RAM and a byte-bounded resident cache are different things. Public evidence of one does not prove the others.

## Findings established so far

1. Seamless presentation, network interest and simulation ownership are separate decisions. Do not infer one from another.
2. A topological neighbour is a useful prefetch target even without a useful Euclidean distance. EVE's stargate prefetch is a particularly direct precedent for Puck's portal edges.
3. An MMO's crowd can exceed client resources even after terrain streaming is solved. Preserve knowledge and interaction while degrading expensive presentation; ArenaNet's removal of network culling is the cautionary example.
4. Published minimum RAM is not a resident byte budget. The researched games disclose useful resource controls but not a complete modern client eviction policy. Unknown limits will remain labelled unknown.

## Evidence register: opened sources

### E1 — EVE, resource demand and graph prefetch (developer, historical proposal, 2014-12-16)

Opened [CCP Snorlax, Download on Demand client for EVE Online](https://www.eveonline.com/news/view/download-on-demand-client-for-eve-online).

Exact sentence: “Ships usually enter the scene (what you see on your screen) in low detail any way – the higher detail resources are then downloaded on demand.”

The article describes downloading individual resources into a shared disk cache, prefetching neighbouring systems' stations and stargates, background fetching low-detail ship resources, and potentially longer first encounters on slow connections. This is evidence for dependency prefetch and a coarse-first strategy, not a RAM eviction algorithm. Its installer/cache sizes are historical deployment figures, not runtime budgets.

### G1 — Guild Wars 2, disclosure versus crowd presentation (developer, 2013 WvW change)

Opened [ArenaNet, World vs. World: The End of Culling](https://www.guildwars2.com/en/news/world-vs-worldthe-end-of-culling/).

Exact sentence: “Our changes are to the client/server culling and have no bearing on basic graphics operations in the GW2 client.”

ArenaNet reports that limiting which nearby characters the server disclosed made enemies invisible and extended apparent stealth. The replacement tells clients about characters within range while exposing separate controls over full models, lower-detail loading fallbacks and nameplates. This demonstrates a serious failure of conflating resource reduction with knowledge of combatants; it does not document freeing model bytes or a hard cache limit.

### W1 — WoW, original-client tile structure (original reverse engineering, explicitly 1.12.1)

Opened [Terrain — the ADT world surface](https://raw.githubusercontent.com/samwhosung/wow-1121-client-internals/main/docs/terrain.md).

Exact sentence: “The whole file is held resident at `area+0x678`.”

This archived record identifies WDT presence, ADT chunks, WDL far terrain and asynchronous loading. Its author points to the successor below; these are one research lineage, not two independent confirmations. No retail memory cap is established by this record.

### W2 — WoW's seamless design was an original goal (developer interview, 2009)

Opened [Jeff Kaplan interviewed by Phil Kollar, Blizzard's Jeff Kaplan on Warcraft's Past And Future](https://gameinformer.com/games/world_of_warcraft_cataclysm/b/pc/archive/2009/12/02/blizzard-s-jeff-kaplan-on-warcraft-s-past-and-future).

Exact sentence: “We kind of take that for granted in 2009, but being able to transition seamlessly between zones was a really big deal.”

The adjacent answer describes an already-working Westfall/Elwynn/Duskwood prototype with different lighting and tilesets. This supports a development transition from conventional zone-loading assumptions, not a claim that released WoW originally had loading screens between those zones.

### W3 — WoW tile selection and eviction (original reverse engineering, 1.12.1.5875)

Opened [benilla-re, terrain: world](https://raw.githubusercontent.com/samwhosung/benilla-re/main/systems/terrain/world.md), “Area of interest”. Sentence excerpt: “The inner window is the index ± r, r = 1 + trunc(far clip × 0.03)”. The definition centres inner and larger outer chunk windows on a position and sizes them from far clip; it does not describe forward-velocity prediction.

Opened [benilla-re, terrain: map update](https://raw.githubusercontent.com/samwhosung/benilla-re/main/systems/terrain/mapupdate.md).

Exact sentences: “Only the window decides: no age, no memory test.” And: “`0x66fc80(position)` reloads the map around a position and waits for it.”

The record reports inner-window blocking reads, time-limited outer-ring chunk work, destruction outside the outer window, WMO group demand from box intersection, and a synchronous forced-reload path. It marks the central findings `verified`, `Proof: one`, with no direct test for most of them. This lane has not checked the executable. Do not silently promote these to measured retail behavior.

### W4 — WoW WMO visibility and aging (same reverse-engineering lineage)

Opened [benilla-re, terrain: map object visibility](https://raw.githubusercontent.com/samwhosung/benilla-re/main/systems/terrain/portals.md).

Sentence excerpts: “the walk through the portals” and “ages every cached map object model: […] freeing what has expired.” The record distinguishes portal draw traversal from keep-alive timers freeing group data and models.

### W5 — WoW shared model lifetime (same reverse-engineering lineage)

Opened [benilla-re, models: the cache](https://raw.githubusercontent.com/samwhosung/benilla-re/main/systems/models/cache.md).

Exact sentence: “A shared model whose count drops to zero stays cached on the release list, stamped with the time.” The following sentences specify a 30-second grace period and removal from the release list if referenced again.

### W6 — Independent format implementation

Opened [warcraft-rs, Rendering ADT Terrain](https://raw.githubusercontent.com/wowemulation-dev/warcraft-rs/main/docs/src/guides/adt-rendering.md).

Exact sentence: “Each ADT file represents a 533.33x533.33 yard tile of the game world, divided into 16x16 chunks.”

Opened [warcraft-rs, WMO Rendering Guide](https://raw.githubusercontent.com/wowemulation-dev/warcraft-rs/main/docs/src/guides/wmo-rendering.md).

Exact sentence: “Unlike smaller decorative objects (M2 models), WMOs have complex interior/exterior structures, multiple groups, portals, and advanced lighting.”

This independently maintained implementation corroborates the format decomposition, not Blizzard's eviction policy. Its sample loader loads all group files; sample code must not be represented as the production WoW streamer.

### W7 — Historical WoW memory constraints

Opened [Blizzard, World of Warcraft online manual, PDF page 4](https://bnetcmsus-a.akamaihd.net/cms/template_resource/LO0VQ46XB1281555957773363.pdf).

Exact requirement: “Memory: 256MB RAM”. This is the original machine requirement, not a promise that world data alone occupies 256 MB.

Opened [Blizzard forum post preserved by Blue Tracker, Patch 3.3.5a Known Issues, graphics section](https://www.bluetracker.gg/wow/topic/eu-en/11824163843-patch-335a-known-issues/).

Exact sentence: “If ‘gxTextureCacheSize’ is set to 0, the client will try to pick a reasonable default value for your settings.” The post gives 128 MB as a configurable GPU texture-cache example for the D3D9Ex backend. This is a historical developer statement mirrored by a third party; not a total client budget, and not a modern recommendation.

### W8 — WoW interest management, independent measurement (2011)

Opened [John L. Miller, Distributed Virtual Environment Scalability and Security, §3.3.1 and §4.2](https://www.repository.cam.ac.uk/bitstream/1810/241109/1/jlm60-Thesis-v2.pdf), PDF pages 37 and 58.

Exact sentence: “Unfortunately, WoW clients only receive avatar movement data which is immediately relevant to them.”

The trace study separates relevance-filtered movement updates from world-wide simulation. Its reported distances concern the measured historical game/scenarios, not a universal modern AOI radius.

### G2 — GW2 map-download miss (developer, streaming-client beta)

Opened [ArenaNet, Coming Soon: Beta Version of Streaming Client](https://www.guildwars2.com/en/news/coming-soon-beta-version-of-streaming-client/).

Sentence excerpt, with an omission: “If you go to an area you haven’t downloaded yet, […] you can wait at the map loading screen.”

This is download/install streaming with an explicit map barrier, not proof that all of a map's assets remain resident in RAM.

### G3 — GW2 server maps and population copies (developer)

Opened [ArenaNet, Introducing the Megaserver System](https://www.guildwars2.com/en/news/introducing-the-megaserver-system/).

Exact sentence: “The megaserver system is a weighted load balancer for players.”

Opened [Stephen Clarke-Willson, Guild Wars 2: Scaling from one to millions, GDC 2013, slide 40](https://media.gdcvault.com/gdc2013/slides/824129Clarke-Willson_Stephen_Guild_Wars_2.pdf).

Exact sentence: “We load balance thousands of maps (public maps plus story instance and dungeon maps) across hundreds of hardware servers in each datacenter.”

These are separate dated developer accounts: 2013 server deployment and the later megaserver selection policy. Neither says the client's graphics cache controls a map's simulation.

### E2 — EVE shipped download-on-demand, including stalls (developer, 2015)

Opened [CCP, Patch Notes For Scylla, releases 1.0–1.3](https://www.eveonline.com/news/view/patch-notes-for-scylla).

Exact sentence: “Download-on-demand has been optimized, in particular the chance of stalls when looking at ships has been reduced.”

This confirms the feature reached a released client and required further stall fixes; the earlier announcement alone would only establish intent.

### E3 — EVE simulation and client recipients (developer, 2010)

Opened [CCP, Fixing Lag: Drakes of Destiny (Part 1)](https://www.eveonline.com/news/view/fixing-lag-drakes-of-destiny-part-1-1).

Exact sentences: “A ballpark is a collection of bubbles.” And: “Bubbles shrink and grow dynamically, and can never overlap.” The definitions place a ballpark at a solar system and a bubble at a physical-interaction volume. The tick builds and sends updates to relevant clients separately from evolving the physics.

### E4 — EVE simulation placement (developer, 2023)

Opened [CCP, Tranquility Tech IV](https://www.eveonline.com/news/view/tranquility-tech-iv).

Exact sentence: “But on the other end of the spectrum Jita is solo on one node and the market for The Forge is solo on another node.”

The article describes SOL/PROXY processes and solar-system simulation placement. Its server RAM figures must not be presented as client memory budgets.

### E5 — EVE client representation cost (developer, 2022)

Opened [CCP, Patch Notes – Version 20.09, Technical](https://www.eveonline.com/news/view/patch-notes-version-20-09).

Exact sentence: “We’ve also improved how long it takes for the client to load and unload ships, which gives a smoother gameplay experience.”

Ship-controller refactoring reduced RAM by approximately 1.5 GB in CCP's 1,000-ship scene. This is CCP's result, not a measurement made in this lane or a guaranteed saving in ordinary play.

### E6 — EVE RAM specification is not residency policy (developer, 2020)

Opened [CCP, 64-bit client transition complete](https://www.eveonline.com/news/view/64-bit-client-transition-complete).

Exact sentence: “We’ll be raising the minimum memory specification for the client to be 4GB as a result of the move to 64-bit.”

Again, this is a dated machine specification, not a 4 GB resident cache cap.

### C1 — Star Citizen approach streaming (developer, 2018)

Opened [CIG, Monthly Studio Report: January 2018, Engineering](https://robertsspaceindustries.com/en/comm-link/transmission/16399-Monthly-Studio-Report-January-2018).

Exact sentence: “This will help performance, decrease load times, and reduce the amount of memory required to run the game.”

The preceding sentence describes background loading of planets and stations on approach. This is a development report about OCS, not a measured memory ceiling.

### C2 — Star Citizen client/server streaming and authority (developer text, historical 2021 design, mirrored)

Opened the [official Q&A URL](https://robertsspaceindustries.com/en/comm-link/transmission/18397-Server-Meshing-And-Persistent-Streaming-Q-A); its extracted body contained only an iframe, so it cannot support a quotation here. Opened the [full mirror of CIG's Server Meshing and Persistent Streaming Q&A](https://api.star-citizen.wiki/comm-links/18397), attributed to Paul Reindell and Benoit Beausejour. **This is mirrored developer testimony, not an independent implementation study.**

Exact sentences from that mirror: “The client already only streams entities that are in visible range.” And: “With Server Meshing, we can decouple the client and server-streaming radius.”

The account separates client OCS (released 2018), server OCS (2019), entity authority transfer, replication and persistence. Its dynamic meshing, capacity and release-date predictions describe future work in that account; they are not evidence that every proposed capability shipped.

### C3 — Star Citizen deployment scope check (operator status, historical)

Opened [RSI incident history, 2024 Preview Deployment](https://status.robertsspaceindustries.com/issues/).

Exact sentence, excluding the decorative emoji: “Star Citizen Alpha 4.0.0-live.9470730 was deployed to the 4.0 Preview channel at 0455 UTC.”

The entry is dated 2024-12-20. It confirms a public alpha deployment, not MMO production maturity, an unlimited shard, or completion of dynamic meshing. Star Citizen is an additional live-alpha comparator here; WoW, GW2 and EVE provide the shipped-game comparisons.

### W9 — WoW server-zone boundary (search summary only; not confirmed from page body)

Attempted to open [Blizzard, Cross-Realm Zones Coming to Beta](https://worldofwarcraft.blizzard.com/en-us/news/5393667/cross-realm-zones-coming-to-beta), including the alternate slug URL; both returned 403. **Search summary only:** the search result reproduces Blizzard's FAQ, including: “Usually transitions will occur on area boundaries (think moving from Darkshore to Ashenvale).”

It also describes multiple population copies and movement between zone instances. This is provisionally evidence that server population topology can differ from the seamless terrain presentation. Confirmation needed: the original FAQ body through an accessible official copy or preserved developer text, plus a developer architecture account before inferring process ownership or handoff details. No ADT-to-server mapping is established.

### W10 — WoW far-terrain representation (original reverse engineering)

Opened [benilla-re, terrain: lod](https://raw.githubusercontent.com/samwhosung/benilla-re/main/systems/terrain/lod.md).

Sentence excerpt: “the WDL's low-detail mesh drawn for the tiles beyond the loaded areas.” This is evidence for a separate far representation, not a claim that every missing close tile gets an adequate placeholder.

## The games compared

Release cross-check for G1: opened [ArenaNet, Flame and Frost: The Razing, Build 3/26/13 release notes](https://www.guildwars2.com/en/news/the-razing/). Exact sentence: “Server-side culling has been removed from World vs. World.” The next bullet lists the three client representations. This establishes release of that change rather than relying only on its announcement.

The facts in this section are scoped to the evidence above. **Unknown** means this lane found no opened source establishing it, not that the feature does not exist. In particular no researched source establishes a complete, current, whole-process client byte budget for any of these games.

### World of Warcraft: spatial windows, a far representation and selective interiors

**Selection.** W1/W3/W6 describe tiles and chunks within a larger map: WDT existence information, ADTs and referenced objects. The 1.12.1 analysis uses a far-clip-sized inner window and an outer preload margin rather than loading one named quest zone at a time. WMO groups intersecting the far box are requested; close ones can be waited on. W4 adds indoor/outdoor portal traversal for drawing. **Do not turn this into “only portal-visible WMO groups are loaded”: load boxes and visibility are different mechanisms.** No opened source establishes movement-direction prediction in Blizzard's streamer.

**Eviction and memory.** W3 reports area/chunk destruction when outside the outer window. W4 reports expiring WMO group/model keep-alives; W3 reports refreshing a demanded group's timer to 30 seconds. W5 reports zero-reference shared M2 models retained for 30 seconds and reused if referenced again. This limits spatial and temporal working sets, but content density can still vary their byte cost (**inference**). W7 establishes a historical texture-cache control and small original hardware target, not a whole-client byte authority. **Unknown:** today's retail geometry/texture total budgets and pressure eviction order.

**Miss and continuity.** W10 supplies low-detail distant terrain. W3's inner-window reads and forced reloads can block the frame; its forced-reload path explicitly does not raise a loading screen. Thus seamless geography need not mean stall-free traversal. W2 places seamless zone transitions in the original development intent. There is no evidence here for a released “zone loads first, seamless later” migration. Different maps/instances must not be conflated with terrain tiles within one map; this lane does not claim all WoW travel lacks loading transitions.

**Server separation.** W8's measured client receives relevance-filtered avatar movement. It does not need every avatar on the continent. W9 provisionally documents a separate population-copy/zone mechanism. Neither terrain tiling nor WMO portal visibility establishes the server's simulation cell size. Exact present-day server ownership boundaries remain unconfirmed.

**Puck implication (inference).** Borrow decomposed spatial units, prefetch margins, reusable shared assets and a cheap far representation. Keep projected-error ranking in place of WoW's historical fixed spatial policy. A client with several views needs the union of their demands; a single camera window cannot be copied literally. Puck can derive a tile's products from authored SDF inputs rather than adopting ADT/WMO formats, and can use an area opening within one authority without making it a transfer between worlds.

### Guild Wars 2: map barriers plus crowd representation independent of disclosure

**Selection and misses.** G2's streaming client downloads needed map content; an absent destination can leave the player at a map loading screen. This is coarse map admission to content, not a documented interior-cell streamer. G1's WvW system reports nearby characters and chooses full models, fallback models or nameplates. Fallbacks also cover high-resolution models still loading. **Unknown:** terrain cell size, portal-driven asset fetching, velocity prediction and map-to-map prefetch internals.

**Eviction and memory.** Character quality/count controls reduce expensive crowd representation. They are workload controls, not proof of a byte cap or actual freeing of model data. **Unknown:** texture/mesh LRU policy, map-transition retained assets, cache timeout, runtime RAM/VRAM budgets and what memory pressure evicts first. Do not say nameplates alone prove memory is bounded.

**Server separation.** G3 places map simulations across hardware servers and separately describes the affinity-based selection of map copies. G1 distinguishes network reporting from rendering. The historical failure was making undisclosed combatants invisible while they could still matter. Its replacement preserves awareness within range while lowering presentation cost. A public map instance is a server population unit; a texture cache entry is not.

**Puck implication (inference).** A character being a world must not imply a full mesh, animation setup, nested renderer and lighting allocation per visible character. Select a compact presentation of that character while the recipient retains every authorized interaction fact. Standard-package authors can choose the readable proxy/nameplate/appearance policy. The mechanism must handle many distinct characters as well as many instances of one appearance. A map loading screen is an available product choice for entering a world, but local GPU readiness must not silently delay a shared simulation door.

### EVE Online: graph-neighbour prefetch and an explicit simulation topology

**Selection and misses.** E1 prefetches static resources across stargate adjacency, then uses coarse ship resources before fine ones. It caches downloads on disk. E2 confirms released download-on-demand behavior and later stall fixes. **Unknown:** the exact resident mesh/texture selection distance, velocity predictor, cache watermark or RAM eviction order. A first encounter can take longer to show; prefetch is not a readiness guarantee.

**Eviction and memory.** E5 documents client ship unloading and lower controller cost. It provides no lifetime rule or byte ceiling. E6's machine requirement cannot fill that gap. The disk download cache is intentionally a different layer from active client memory. Keep the measured thousand-ship improvement in E5 as evidence that per-entity costs remain important after spatial streaming, not as a budget for Puck.

**Server separation.** E3's ballpark/bubble arrangement handles simulation and relevant-client updates. E4 assigns solar systems and services to server processes; some busy services get a process of their own. A client prefetching a neighbouring system's station does not imply that the station's simulation moved to its machine or that its server was newly activated (**inference**). This evidence does not make a solar system an unbreakable architectural law for Puck.

**Puck implication (inference).** Stargate adjacency becomes portal-edge and resolved-destination adjacency; Euclidean separation is irrelevant across worlds. Prefetch the cheap representations and cache products needed at an opening before the detailed view, under the same selector reservation. Do not import EVE's universal low-detail ship catalogue literally: an open federation has no finite global catalogue of author content. Fetch a bounded relevant neighbourhood, not every character's lowest level forever.

### Star Citizen: useful hierarchical streaming evidence, with an alpha/design qualification

**Selection and eviction.** C1 describes approach-driven background loading of large areas. C2 describes range-based client streaming, dynamic bind/unbind, and a separate server streaming path. This supports dropping client entities outside demand; it does not specify exact spatial hysteresis, bytes, eviction timing or the current asset-cache policy. **Unknown:** explicit client RAM/VRAM cap, priority order and client handling of an ordinary late geometry miss. Do not fill that gap with anecdotal missing-floor behavior or claim doors always hold for streaming.

**Server separation.** C2 separates replication/persistence from single-entity authority and transfer between server nodes. It also warns that backend scale does not remove a single client's limits, and that client streaming range can include radar/missile requirements beyond simple visual need. Its dynamic-load distribution is a historical design ambition, not established shipped behavior in this report. C3 confirms an alpha preview deployment only.

**Puck implication (inference).** Hierarchical spatial ownership of content can help selection, but container membership must not become a second authority model. Puck already routes worlds and separates observing, acting and crossing. Borrow hierarchical query/dependency units; retain one authority per body, independent disclosure and independent client cache lifetimes. Do not adopt a central replication service as a requirement of a federation run by mutually independent hosts.

## Systems considered in the current checkout

This was a bounded read, not a source review or runtime validation. I loaded `content-search`, copied the existing Release CLI to a lane-named scratch directory, and ran these read-only searches without building it:

```text
dotnet .claude/scratch/s1-research-mmo-cli/Puck.Cli.dll search 'class MemoryAccount|ReserveArrivals|class SdfMeshDemand' src -g '*.cs' -M 0
dotnet .claude/scratch/s1-research-mmo-cli/Puck.Cli.dll search 'MemoryAccount|DemandWithinView|DemandHeight|Destination.*revision|Destination.*Revision' src -g '*.cs' -M 0 -l
```

The first returned `src/Puck.Hosting/Jobs/MemoryAccount.cs:27`, `src/Puck.SdfVm/SdfMeshDemand.cs:21`, and `WorldViewGraphHost.ReserveArrivals` at `src/Puck.World.Client/WorldViewGraphHost.cs:1614`. The second located existing memory participants, mesh library/store, shader budget, frame demand and window-frustum code. I opened `MemoryAccount.cs`, `SdfMeshDemand.cs` and the `ReserveArrivals` implementation. An initial host `rg` discovery was replaced by these Puck searches; two guessed host-search directories did not exist, so that initial result was not a complete search.

- `MemoryAccount` already has live/cached/retirement/metadata categories, owners, reservations and cache trim requests. Extend this authority; do not create a streaming budget singleton beside it. Its `TryReserve` refuses over-budget extra promises, while the owner's unconditional reserve (since made the process's one forced job, `MemoryAccount.Owner.TryReserveForced`) explicitly promises progress regardless of budget. **Integration issue for plan step 2:** reading the device budget alone will not make every existing admission path a hard cap. Audit and route mandatory progress through bounded work/fallback, accounting for the old and new representations together. Do not claim a process-wide enforced limit until every relevant allocator participates and the unconditional path is reconciled.
- `SdfMeshDemand.Observe`, `PixelAt` and `Priority` already combine multiple view eyes with pixel scale. `DemandWithinView`, `DemandHeight` and `WithinViewMargin` already carry aperture-related restrictions/margins. The portal recommendation below extends demand production and lifetime; it is not a request to write another frustum system.
- `ReserveArrivals` already walks a graph and retains the path to an arriving world's window. Preserve its understanding of pending landing, not just pending bake, while replacing hidden rendering as the pump. Do not replace it with a “bake completed” flag that can strand upload work.
- The source check does not establish all residency accounting consumers or destination pinning details. The world model's single destination resolution for display/transfer remains the design constraint; the next lane must inspect its implementation before changing it.

The delegate skill helped keep authority and evidence boundaries explicit; Puck.World helped locate the existing view graph; content-search found the shared mechanism. No skill/source changes were authorized or made. No claim that the implementation meets the memory or visual bars is made here.

Report check: `puck docs links` passed for this document when it was written. This validates repository references, not the truth of web claims or the external servers' availability. External evidence was opened and inspected as recorded above. No tests, builds or Worlds ran, and no Git mutation was made.

## Technique-by-technique translation into Puck

All proposed additions below are **design inferences** from the evidence, weighed against Puck's settled residency decision. They are recommendations for the lead's synthesis, not new owner rulings.

| Technique | Confirms | Contradicts or cannot be copied | Puck-native adaptation / addition |
|---|---|---|---|
| Spatial tiles/chunks and prefetch margins (W3/W6) | Terrain tiles as selector units; work near the occupied place first. | Fixed metre rings as the universal detail policy; a spatial window by itself is not a byte bound. | Use tiles as query/I/O/derivation units and rank their groups by existing projected error. Keep selection separate from authored spelling and authority boundaries. |
| Room/group portals and containment (W4/W6) | Areas are nodes and apertures/portals are edges. | A WMO's visibility graph is not a cross-authority transfer protocol; one world per room adds unnecessary authority machinery. | Gallery floors, lift shaft, stairs and outdoor openings contribute additive demand. Conservative traversal may overfetch; a wrong visibility rejection must not make a required floor disappear. |
| Topological neighbour prefetch (E1) | Portal-mapped demand and soon-to-open neighbours. | Fetching only Euclidean neighbours; globally prefetching every federation member's coarse asset catalogue. | Traverse already-authorized/resolved edges with aperture size, traversal likelihood and arrival deadline. Use the same destination generation/revision as display and crossing. Prefetch assets without silently opening sessions or reserving travel. |
| Coarse-first/fallback presentation (W10/G1/E1) | Coarsest meshes, portal fallbacks, held images and counted misses. | Invisible relevant combatants, holes, or letting a GPU miss determine collision/admission. | Keep an affordable authored representation per relevant object/world; prefer shared coarse products. Expensive appearance can arrive later while identity, targeting and authoritative contacts remain valid. A proxy must not fabricate hidden state. |
| Keep-alive and reuse (W4/W5) | Hysteresis and warm reuse across a turn-around or doorway. | A universal 30-second retention timeout as a hard memory policy. | Give recent use a retention bonus within one byte authority. Pressure overrides the bonus. Charge storage until actual retirement/fence completion, and reuse rather than allocate a second identical product. |
| Independent network interest and render quality (W8/G1/E3/C2) | Disclosure can follow the observer; authority cannot. | Treating render frustum or model presence as the authoritative interaction set. | Retain authorized interaction facts and cheap presentations across offscreen attacks, picking, sound and portal observations; send only what grants disclose. Presentation demand cannot mint an Observe grant. |
| Server zone/map copies or meshed ownership (G3/E4/C2; W9 provisional) | Worlds with their own authority; multiple clients observing one simulation. | Assuming client memory is fixed by adding servers, or silently turning cache eviction into world pause/transfer. | Route/admit through world relationships, with cache cost independent of host ownership. Author authority boundaries around interaction semantics; do not make an SDF tile an authority by default. |
| Loading barriers and synchronous demand (G2/W3) | A miss requires a deliberate user-visible policy and useful counters. | Holding a shared door until one client's disk/GPU is ready; blocking frame I/O as the normal residency mechanism. | Use deadline prefetch and presentation fallbacks. Any gameplay barrier is a separately ordered authoritative event; a local held image must still let cancellation/escape and controls behave coherently. |
| Per-entity representation cost (E5) | Plan steps 3, 4 and 9: attribution, duplicate-copy removal and managed churn. | Assuming spatial eviction solves a dense crowd occupying one small area. | Measure state/projection, appearance products, rig/animation data, lights, metadata and pending jobs per character-world. Share immutable identified products, keep unique state small and avoid a heavy renderer allocation merely because a session exists. |

### Weighing the options

**Option A: copy a fixed-ring streamer.** It is easy to reason about spatially and WoW's historical record shows a viable working-set pattern. It loses at Puck's range of scales, several simultaneous views and non-Euclidean edges, and cannot bound bytes under variable content density. **Ruling:** keep rings only as an implementation/query aid or prefetch margin; the decision's projected-error selector remains the policy.

**Option B: make every room a routed world.** It aligns memory with whole nodes but pays for authority, sessions and transfers at every doorway. It also encourages wholesale eviction and per-world fixed allocations. **Ruling:** optional areas within a world and routed worlds between actual authorities remain complementary. A room is a world only when its authored semantics warrant it.

**Option C: one budgeted selector over content dependencies and observation edges. Recommended.** Combine projected-error demand, existing frustum restrictions, optional area demand and bounded graph lookahead. Scheduler, store fetch, derivation, landing, lighting and eviction share the resulting priority. Content identity enables cross-world reuse; authoritative identity and revision still decide what that content means. **Strongest objection:** many portal branches and different representations create metadata/queue overhead even before meshes arrive. Close this with bounded speculative work and byte accounting for metadata and in-flight work, rather than another executor or one full allocation set per branch.

**What this closes.** Whole-continent assets cease to define RAM use; old routes cannot retain unlimited geometry; tiny remote views do not demand full-screen detail; one client's misses do not change another's simulation; dense crowds can reduce expensive representation without deleting awareness. **What it leaves open.** A malicious or simply too-dense required view can exceed any affordable presentation floor. The selector needs a fully specified overload behavior, and federation/disclosure/admission still owns what clients are permitted to observe. Streaming cannot solve simulation overload inside one interacting cluster.

### Puck-native mechanisms worth making explicit in the synthesis

1. **Budget the peak, not just the landed payload.** Reserve old resident data + replacement + decode/bake workspaces + staging + fence-delayed retirement. Count an aliased unified-memory allocation once in physical accounting; keep per-device and host constraints explicit within the one authority. Logical resource bytes, committed backing and device residency are different readings. Cancelling queued work must release its future promise, not orphan retained inputs.
2. **Give topology lookahead a deadline and a cost.** Existing velocity lead predicts ordinary travel; a known authored lift/flight path can offer a stronger hint to the same selector. Portal reachability and soon-open area edges cover destinations velocity cannot predict. **Hypothesis:** latency estimates by pipeline stage and tail percentile outperform a single average lead. This is presentation scheduling only; no rule fact reads the estimate.
3. **A mapped point needs the view's footprint.** Reuse the existing off-axis camera/frustum and demand-height data, including support for shadows and GI, when taking demand through a face. Use cycle-aware, budget-bounded traversal of already resolved views; do not use a depth alone as a cost guarantee. Multiple legitimate routes to one destination may require multiple eyes. Deduplicate products by derivation identity without collapsing different destination generations or disclosure scopes.
4. **A resident session is not a resident full scene.** Keep session/authority state and necessary compiled documents as their contracts require; let unused GPU tables, meshes, probe caches and render targets shrink under pressure. If minimum per-session overhead itself grows without bound, report it separately: presentation eviction cannot be claimed to solve session/document scaling. Changing document assembly to be view-dependent would contradict the decision and needs a separate design.
5. **Prewarm derived products, never canonize them.** Shipping can include cache products generated from inputs at build/release time. On a miss the same identity can be fetched or regenerated; do not record an inventory whose absence becomes refusal. Pick prewarm content from the debug world's travel paths and reachable portal neighbourhood, not a global federation list. **Hypothesis:** a compact shared coarse-product cache avoids much repeated bake work at portal seams; the implementation must measure it.

## Evidence limits and disagreements

- **Visibility versus loading:** the WMO guide's selective-visibility explanation and its all-groups example loader describe different layers. The original-client map-update analysis independently distinguishes load-box tests from portal drawing. There is no basis to call the example the retail loader or to claim a hidden group is immediately evicted.
- **Independent corroboration stops at the right boundary:** warcraft-rs and the original-client analyses agree on spatial format decomposition. The archived analysis and its benilla-re successor are the same author's work. They do not supply two independent confirmations of exact timer values or waits. Treat those as one original research record pending binary/hardware replication.
- **Memory budget versus address space:** WoW/EVE machine specifications and WoW's texture-cache knob constrain different resources. None proves a complete client memory bound. GW2's GDC paging incident is about servers; it must not be used to invent a client budget.
- **Seamlessness versus latency:** W2's design account and W3's blocking near-read account are compatible. No loading screen can still involve a stall. E2 likewise confirms that an asynchronous download design still required stall fixes after release.
- **Shipped versus intended:** E2 establishes release of EVE's download-on-demand; E1's prefetch details remain the developer's announced design, not independently traced here. C2's 2021 release predictions must not be used as dates; C3 confirms only the alpha deployment named in its entry. Dynamic meshing's modern status was not established in this lane.
- **Search-summary exception:** only W9's asserted FAQ behavior relies on a search summary. Its attempted opens and confirmation needed are recorded above. Other search hits, inaccessible format-wiki pages and community performance anecdotes were not used as factual support.
- **Hardware evidence:** none collected. Exact retail working sets, recent client eviction internals and shipping latency distributions remain unconfirmed. This report does not turn an absence of public detail into an absence of a system.

## Ranked five techniques to adopt or adapt

These are proposed amendments to `docs/plans/streaming-and-memory.md`; the plan itself was not edited. The checks are work for the implementation/measurement lanes, not runs performed here.

1. **Priority eviction with an affordable presentation floor and peak-aware admission.** This is the prerequisite that makes every later demand policy safe. The games provide partial precedents; Puck's single byte authority is the stronger native answer. **Change steps 2–5:** extend the existing `MemoryAccount` owners and trim mechanism to device/host/staging/retirement, audit unconditional progress reservations, reserve replacement overlap, and make reclaim finish before new bytes spend it. Retain compact coarse representations where affordable; under sustained pressure use the decision's fallback, not an exception or a resident-byte overshoot. **Check:** lower the budget while touring/turning back through the gallery; live + cached + retirement + metadata + outstanding promises reconcile, actual device usage is explained, and misses change only presentation. A mandatory minimum exceeding the usable budget must be reported, not hidden by “forward progress”.

2. **Topology-aware, deadline-led prefetch through mapped portal views.** EVE's graph-neighbour prefetch is the closest analogue; Puck adds frame mapping, aperture demand and federation identity. **Change steps 6 and 8:** include bounded lookahead for already-authorized portal/area edges, combine it with velocity lead, deduplicate shared products, and retire the hidden-render arrival pump only when independent bake/load/upload progress is established. Preserve generation/revision pinning and avoid observer-driven session admission. **Check:** the gallery's chained paintings, a same-world link, a fast approach and an abrupt reversal demand the correct destination before visibility; an unseen branch cannot grow queue/resident bytes without limit; misses are counted by late pipeline stage.

3. **Cheap but readable crowd representations independent of interest/disclosure.** ArenaNet's experience and EVE's controller result make dense crowds a first-class workload. **Change steps 3–5 and 9:** add a character-world cost breakdown, measure duplicate appearances and many unique ones, select cheaper authored presentation through the existing tier/budget machinery, and bound allocation bursts while those appearances arrive. No second crowd streamer. **Check:** a dense gathering in the debug world remains targetable and readable with fine appearance delayed; compare bytes per participant and per unique appearance, allocated bytes and collections, not only frame time. Keep network omission caused by grants distinct from graphics omission caused by budget.

4. **Hierarchical spatial units plus additive authored areas.** Borrow tile/chunk decomposition and portal-connected interior membership while keeping Puck's selector and authority model. **Change step 8, supported by step 7:** prove floors/openings first, then terrain tiles and routed moons; use the same demand support for GI blockers and shadow casters. Treat tile bounds as derivable from canonical authored content, not author spelling. **Check:** at an occupied gallery floor, distant visible outdoors and nearby hidden floors compete correctly; a lift/stair approach brings the next floor gradually; terrain demand scales with the observed neighbourhood. Include GI/support bytes and transition work in the reading, not just drawn meshes.

5. **Pressure-aware reuse of identified products and transient representations.** Borrow shared-model grace and warm disk caches; replace time-only retention with priority under the global account. **Change steps 4, 5 and 9:** remove rebuildable duplicate CPU copies after the consumer no longer needs them, share immutable products across worlds/characters, retain recent products only within the budget, cancel obsolete speculative jobs and pool arrival metadata. Use source-derived identities and optional release-time prewarming. **Check:** walk out and back, look through two portals showing the same content, then force pressure. Counts show reused fetches/bakes and actual reclaimed backing; a reference/fence still in use prevents reuse/freeing; the warm steady-state memory does not climb on each traversal.

## Open questions and owners

1. **What is the unavoidable minimum per active session/world on the client?** Step 3's attribution lane owns the measurement, split into document/projection, GPU tables, probes, targets and metadata. It decides whether step 5 alone bounds a portal-rich hub or a separate session/document-scale design is needed.
2. **What currently calls unconditional `MemoryAccount.Owner.Reserve`, and can its work be subdivided?** Step 2's accounting lane audits callers and defines bounded forward progress before claiming a hard budget. This is an identified design integration issue, not a source fix performed by this lane.
3. **Which progress currently requires a rendered frame, including fence/landing work?** Step 6's lane traces the `ReserveArrivals` replacement through the actual scheduler and uploads. It must show hidden rendering can stop without leaving pending meshes unbound.
4. **What should remain visible for a relevant character when fine appearance cannot fit?** The standard-package/authoring lane proposes proxies using existing authored mechanisms, and the lead judges them against visual/readability intent. The owner is needed only for a genuinely new product/taste choice, not whether to implement the existing miss policy.
5. **What are the landing-latency tails at walk, lift, flight and immediate portal reveal speeds?** Steps 3/6's measurement lane owns a quiet-window sweep on both backends. It decides deadline lead, speculative byte allowance and whether pipeline throughput is the real cause. No numeric prediction horizon is settled by this research.
6. **Can many authorized portal views require more than the allowed coarse floor?** Steps 5/6 jointly specify overload priority, aperture/cycle handling and fair service across views while preserving grants and all simulation. A tiny portal cannot consume unlimited bytes, but an arbitrary recursion cutoff is not yet justified.
7. **How is allowed remote content fetched and cached without leaking undisclosed bytes or duplicating state?** The federation/content lane checks resolution, generation pinning, cache namespaces and invalidation at existing doors. Content-hash equality may share public immutable bytes; it is not proof of admission or authority.
8. **Do shadow/GI support sets dominate visual demand in interiors or through portals?** Step 7's lighting lane measures support-region residency using the same observations. It decides cache bounds and trace levels; it does not silently drop blockers because their triangles are offscreen.
9. **Which exact WoW historical claims warrant independent replication?** The lead may assign a later read-only client-analysis lane to confirm window destruction, group timers and forced waits against a lawful local 1.12.1 build. W9 needs an accessible official FAQ copy; modern retail eviction policy needs fresh vendor evidence or a measured trace. None blocks the proposed native selector design.
10. **Which apparent memory savings actually release physical backing on the Deck?** Step 2/3's platform-budget lanes own device/host readings and unified-memory attribution. This report does not substitute hardware requirements, process RSS alone or separate logical GPU/CPU sums for that qualification.
