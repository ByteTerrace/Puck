# MMO foundations lane C: world seams

## 1. Summary

Every major MMO and multi-server architecture splits world seams along two axes: **spatial boundaries** (how adjacent regions share sight, contact and authority handoff) and **population/state overlays** (how multiple copies or filtered views of the same coordinates coexist). Across World of Warcraft, Guild Wars 2, Elder Scrolls Online, Final Fantasy XIV, Star Citizen, SpatialOS, EVE Online, Dual Universe and New World, hiding population seams behind dynamic server reassignment creates two recurring failure modes whenever a seam moves under a live player: **mid-action world disappearance** (nodes, targets or party mates vanishing mid-cast) and **layer-hopping exploits** (using party invites to jump copies for rare spawns or to escape PvP combat). Conversely, fine-grained reactive authority splitting across continuous space (SpatialOS worker boundaries, Dual Universe dynamic octrees, EVE system-jump bottlenecks under Time Dilation) fails or degrades wherever a crowd concentrates at a single contested junction. (Citation check: of these three, only EVE's is confirmed by an opened operator source; the SpatialOS root cause is contradicted by Bossa's own statement and Dual Universe's is unconfirmed, see Topic 3.) Puck's world model already separates continuous spatial topology (`WorldAdjacency`, which compiles a fixed-point overlap band and renders neighbour solids through `WorldAdjacencySceneEmitter` without any frame furniture) from diegetic doorways (`WorldPlacementFace` + `WorldPlacementPortal`, where frame geometry and `window` sessions are optional facets over a swept crossing plane) and non-spatial cohort handoffs (`WorldDestination` resolution). However, three gaps remain at MMO scale: (1) population layer transitions lack an unobservability predicate and hop-cooldown fence; (2) body interest management (`WorldObserverDisclosure` and `WorldOutputHub.Redact`) is an $O(N \times M)$ post-tick Euclidean scan disconnected from the planned `WorldRecipientView` (specified in `docs/plans/disclosure.md`, not yet in the tree), defaulting unembodied observers to `All` (Attack A16) and detaching sinks at `PendingDeliveryLimit = 8` rather than decimating low-priority updates (Attack A12); and (3) contested places between two player-hosted authorities have no honest settlement path under co-location (Attack A6) or mirror-pose seam contact (`WorldCrossAuthoritySettlement.LocalResponds`, Attack A10), whereas Puck's fixed-point determinism (`FixedQ4816`, Rule 4) makes mutual deterministic simulation with per-tick state-hash comparison and a lightweight stall/desync referee viable where no float-based MMO can use it.

---

## 2. Findings by topic

### Topic 1: World of Warcraft sharding, CRZ, layering and phasing

#### What each mechanism hides and how it works

1. **Phasing (first used in *The Burning Crusade*, used at scale from *Wrath of the Lich King*, evolved into smooth object phasing in *Warlords of Draenor*):**
   - **Citation check (opened):** Warcraft Wiki *Phasing* says "Phasing was invented as a bug fix for the climax of the Ogri'la introduction quest chain" (a *Burning Crusade* zone) and that *Wrath* is where Blizzard "has made full use of this system"; this corrects the original "introduced in *Wrath*". It also says "Players in different phases cannot see each other unless they are in the same party and party sync is enabled."
   - **Mechanism:** Hides or replaces NPCs, props and terrain states within the same zone based on a character's quest or narrative progression **[summary]** (drawn from Blizzard's engineering and design discussions of phasing and smooth phasing, summarized via Wowpedia *Phasing* `https://warcraft.wiki.gg/wiki/Phasing` and MMO-Champion coverage of *Warlords of Draenor* phasing changes).
   - **What players notice:** Under early all-or-nothing terrain phasing (*Wrath* / *Cataclysm*), two grouped friends standing at the same coordinates on different quest steps could not see, heal or assist each other, and chat/combat split across invisible narrative walls **[summary]** (Wowpedia *Phasing* `https://warcraft.wiki.gg/wiki/Phasing`). Later smooth phasing kept players in the same spatial instance while hiding or swapping individual quest NPCs and harvestable props per client **[summary]** (Wowpedia *Phasing* `https://warcraft.wiki.gg/wiki/Phasing`).

2. **Cross-Realm Zones (CRZ, introduced in Patch 5.0.4):**
   - **Mechanism:** Merges underpopulated outdoor zones across multiple realm databases into shared zone instances so low-level areas stay populated **[summary]** (Zarhym, Blizzard Entertainment official blog and forum FAQ *Cross-Realm Zones Coming to Beta*, summarized on Wowhead `https://www.wowhead.com/news/cross-realm-zones-coming-to-beta-203382` and Wowpedia *Cross-realm zones* `https://warcraft.wiki.gg/wiki/Cross-realm_zones`).
   - **What it hides:** The server boundary between realms inside outdoor zones, coalescing players automatically as they cross a zone border (for example, riding from Orgrimmar into Durotar) **[summary]** (Wowpedia *Cross-realm_zones* `https://warcraft.wiki.gg/wiki/Cross-realm_zones`).

3. **Sharding (introduced in *Warlords of Draenor* / *Legion*):**
   - **Mechanism:** Dynamically splits a single high-density sub-zone (or a small geographic patch around a world boss or city hub) into multiple parallel copies ("shards") when population exceeds a server threshold, and merges shards back together when players disperse **[summary]** (Ion Hazzikostas and Blizzard developer interviews on sharding, summarized on Wowpedia *Sharding (term)* `https://warcraft.wiki.gg/wiki/Sharding_(term)` and Wowhead sharding analyses).
   - **What it hides:** Sub-zone server capacity caps, aiming to keep frame rates and server tick times stable without login queues **[summary]** (Wowpedia *Sharding (term)* `https://warcraft.wiki.gg/wiki/Sharding_(term)`).

4. **Layering (introduced for *WoW Classic* launch):**
   - **Mechanism:** Unlike micro-zone sharding, a "layer" duplicates an entire continent (all of Eastern Kingdoms or Kalimdor) for roughly 3,000 concurrent players so that a player riding from Southshore to Tarren Mill sees one continuous population without crossing sub-zone shard seams **[summary]** (Blizzard *WoW Classic* developer panels and blue posts by Kaivax and Ion Hazzikostas, summarized on Wowhead *Layering in Classic WoW* `https://www.wowhead.com/classic/news/layering-in-classic-wow-291729` and Wowpedia *Layering* `https://warcraft.wiki.gg/wiki/Layering`).

#### What failed in practice: player-visible seams and exploits

- **Mid-cast resource node and mob disappearance:**
  - Two distinct causes produced the infamous "herb/ore node vanishes while my cast bar is at 90%" defect **[summary]** (Blizzard forums and Reddit `/r/wow` bug reports, summarized on Wowpedia *Sharding (term)* `https://warcraft.wiki.gg/wiki/Sharding_(term)`):
    1. *Multi-tap despawn window:* After *Legion* made gathering nodes multi-tap, the first player to harvest a node starts a hidden despawn timer (roughly 30 seconds). A second player arriving at second 28 begins a 3-second gather cast and watches the node despawn mid-cast without clear feedback **[summary]**. Citation check: the cited Warcraft Wiki *Sharding (term)* page does not mention multi-tap nodes or a despawn timer; this cause is unconfirmed.
    2. *Dynamic shard/CRZ reassignment:* When a player accepts a party invite, walks across an invisible sub-zone CRZ border, or triggers dynamic shard balancing while approaching a node or rare mob, the server reassigns the player's phase/shard ID mid-approach or mid-combat, causing every surrounding mob, corpse and node from the prior shard to vanish instantly **[summary]** (Wowpedia *Sharding (term)* `https://warcraft.wiki.gg/wiki/Sharding_(term)`). Citation check (opened): the page confirms the parts this rests on, in its words: "Each shard has its own set of objects such as herbs and mining nodes", "players may notice NPCs momentarily disappear", and on joining a party "all party members are automatically moved to the leader's shard" when in the leader's zone. It dates sharding to patch 6.0.2 (*Warlords of Draenor*).
- **Layer-hopping and shard-hopping exploits:**
  - Because grouping rules prioritized putting party members together, accepting an invite from a player (or an automated guild/addon bot sitting in a capital city on another layer) transferred the invitee to the leader's layer **[opened]** (Warcraft Wiki *Layering* `https://warcraft.wiki.gg/wiki/Layering`: "Players can move to a different layer by joining a party with a party leader on a different layer"). Citation check: the Wowhead URL this page first cited (`https://www.wowhead.com/classic/news/blizzard-clarifies-layer-hopping-cooldowns-and-mechanics-in-classic-295154`) opens an unrelated Wowhead news post about a Brazilian Classic realm, so it supports none of the claims attributed to it here; the Blizzard posts below replace it. The transfer was not always immediate: Blizzard's lead software engineer Brian Birmingham said in the Classic AMA, reposted on the Blizzard forums (`https://us.forums.blizzard.com/en/wow/t/layering-in-classic-blizzard-responds-on-reddit/260714`), "we don’t want to transfer you in the middle of combat, or before you get a chance to loot" **[opened]**.
  - **Economy abuse:** Players farmed high-value rare spawns (Black Lotus, Devilsaur leather, Arcane Crystals, rare chests) in one location, requested an addon-automated invite to hop to another layer at the exact same coordinates, and harvested the node again **[summary]**. Citation check: Blizzard Watch's report of the hotfix (`https://blizzardwatch.com/2019/09/09/wow-classic-layer-hopping-abuse-hotfix/`) **[opened]** says it was meant to "impede the abuse of layering to gather resources or tag quest mobs", which confirms resource-gathering abuse, but the same article calls claims of stockpiled Arcane Crystals or Black Lotus false; the named-item detail is unconfirmed.
  - **Infinite AOE farming and economy inflation:** Mages AOE-farmed dense packs, hopped layers immediately upon clearing to bypass respawn timers, and generated massive gold inflation **[summary]** (US Blizzard Classic General Discussion forum threads and Wowhead Classic layering reports). Citation check: no specific page is named; unconfirmed.
  - **PvP combat escape:** Players losing a world-PvP fight accepted a pre-staged party invite or addon auto-invite and vanished mid-duel onto another layer **[summary]**. **Refuted as stated** by the design statement this should rest on: Birmingham's AMA answer (forum repost above) **[opened]** says "there’s actually a separate, longer transfer delay following any PvP combat", so an invite did not move a player out of an ongoing PvP fight; the cited Wowhead URL is the unrelated page noted above.
  - **Blizzard's mitigation (hotfix):** Blizzard patched in an increasing cooldown between layer hops **[opened]** (Kaivax, Blizzard forums, *New Increasing Cooldown for Changing Layers* `https://us.forums.blizzard.com/en/wow/t/290703`: "We recently developed a hotfix that restricts frequent layer-hopping", "each time a player moves to a new layer, there will be an increasing amount of time", "the cooldown can increase to a duration of several minutes (or longer)"). Citation check: the post does not say the cooldown grows exponentially, and it does not mention aggro or PvP; the combat, loot and PvP delays were part of the design before the hotfix, per Birmingham's answer above, not part of it. Even then, addons such as *Nova World Buffs* tracked layer IDs via NPC GUIDs (`[Creature-0-XXXX-...]`), turning hidden layers into player-named channels ("Layer 1", "Layer 2") that guilds coordinated to overload or hop between **[summary]** (CurseForge *Nova World Buffs* documentation and Classic community guides).

---

### Topic 2: Guild Wars 2 megaserver, ESO megaserver and campaigns, FFXIV instances and World Visit

#### Guild Wars 2 megaserver and map instancing

- **How it works:**
  - Every zone in *Guild Wars 2* is a bounded map connected by visible portal arches (or waypoint fast-travel). At launch, each "Home World" (server) owned one static copy of each map, spilling excess players into temporary "Overflow" shards where world-versus-world bonuses and queue progress behaved inconsistently **[summary]** (ArenaNet official blog post *Introducing the Megaserver System*, summarized on Guild Wars 2 Wiki `https://wiki.guildwars2.com/wiki/Megaserver_system`).
  - The Megaserver update removed static home-world map ownership in PvE: the matchmaker opens just enough map copies to hold the active population across the entire region (NA or EU) and routes arriving players into a copy weighted by party, guild, language, home world and map fullness **[opened]** (Guild Wars 2 Wiki *Megaserver* `https://wiki.guildwars2.com/wiki/Megaserver`: placement uses "a set of heuristics which includes your party, guild, language, and home world").
  - When a map copy drains below a threshold, remaining players receive a non-intrusive HUD prompt to move to a populated map for a buff rather than being silently yanked mid-action **[opened]** (Guild Wars 2 Wiki *Volunteer's Blessing* `https://wiki.guildwars2.com/wiki/Volunteer%27s_Blessing`). Citation check: the earlier quotation was not the game's text, and the cited `Megaserver_system` URL returns 404 (the page is now `https://wiki.guildwars2.com/wiki/Megaserver`, which describes no prompt). The *Volunteer's Blessing* page gives the prompt as "There are few players remaining on this map" with a countdown, "You have 59 m, 59 s to volunteer before the map closes", and a 10-minute buff (15% movement speed, 5% karma, 10% kill experience).
- **What failed in practice:**
  - **LFG "Taxi" map-hopping and hard map caps:** Because profitable meta-events (such as *Octovine* in Auric Basin or *Chak Gerent* in Tangled Depths) run on staggered or multi-copy schedules, squad commanders list squads in the LFG tool to "taxi" 50+ players into one winning map shard via "Join in [Map]". During multi-loot exploits (such as the Auric Basin *Tarir multiloot* before ArenaNet patched chest lockouts per account), players hopped across five or six megaserver copies after a single boss kill to open grand chests repeatedly **[summary]** (Guild Wars 2 Wiki *Auric Basin* and Reddit `/r/Guildwars2` postmortems on Tarir multiloot).
  - **Party-split on full maps:** When a map hits its hard player cap (roughly 70–150 players depending on the zone), a friend or squad member crossing the portal lands in a newly spun-up empty copy and sees "Map is full" on every manual join attempt, forcing spam-clicking to enter **[summary]** (Guild Wars 2 forums and Wiki `https://wiki.guildwars2.com/wiki/Megaserver_system`). Citation check: the current *Megaserver* page gives no player cap and says a player whose party's copy is full gets a pop-up offering to join a queue for it (the Megaserver Map Queue), which supersedes the spam-click description.

#### Elder Scrolls Online megaserver and Cyrodiil campaigns

- **How it works:**
  - ESO runs one regional megaserver per platform/region (PC-NA, PC-EU) for overland zones, dynamically spinning up zone instances behind loading-screen doorways, wayshrines and region borders, while isolating large-scale three-faction PvP inside named "Campaigns" (instances of the Cyrodiil zone with faction-locked population caps and alliance-lock timers) **[summary]** (UESP *Online:Megaserver* `https://en.uesp.net/wiki/Online:Megaserver` and *Online:Campaigns* `https://en.uesp.net/wiki/Online:Campaigns`).
- **What failed in practice:**
  - **Launch quest-phasing party splits:** At launch, ESO tied overland sub-zone instances heavily to per-character quest state. Friends who grouped together but had made different choices in a prior quest chain saw each other as floating chevrons on the compass with invisible bodies; ZeniMax Online Studios had to spend months consolidating quest phases and adding "Travel to Player" phase-sync rules **[summary]** (Matt Firor, ZeniMax Online Studios *Road Ahead* developer letters, summarized on UESP `https://en.uesp.net/wiki/Online:Phasing`).
  - **Cyrodiil campaign population locks and queue freezes:** Because Cyrodiil concentrates hundreds of players casting multi-target area calculations on one zone instance, ZeniMax lowered per-faction population caps repeatedly to combat severe skill input delay; during prime-time faction locks, players experienced multi-hour queues or a recurring queue bug where the "Confirm Entry" prompt looped without admitting the character **[summary]** (ESO official forums Cyrodiil performance tests and UESP `https://en.uesp.net/wiki/Online:Campaigns`).

#### Final Fantasy XIV instances, World Visit and Data Center Travel

- **How it works:**
  - FFXIV keeps traditional named Home Worlds grouped into logical Data Centers. Major expansion zones split into explicitly numbered instances (`Zone ①`, `Zone ②`, `Zone ③`) during expansion launches; the seam is never hidden—players pick an instance number at the zone border or Aetheryte crystal, and each instance has its own independent hunt-mark spawn timers **[summary]** (Final Fantasy XIV Lodestone patch notes and ConsoleGamesWiki *Instances* `https://ffxiv.consolegameswiki.com/wiki/Instance`). Citation check: the ConsoleGamesWiki page is a disambiguation entry that says only "Multiple copies of an overworld zone or city existing in the same World", "Usually temporarily implemented for certain areas after an expansion release or major patch to relieve congestion"; the numbered instances and the "Travel to Instanced Area" aetheryte option are supported only by search summaries (a PC Gamer how-to), and the per-instance hunt timers are unconfirmed.
  - **World Visit (Patch 4.57) and Data Center Travel (Patch 6.18):** Players interact with a main city Aetheryte (or select DC Travel from the character-select menu) to move their character record temporarily to another World or Data Center **[summary]** (FFXIV Official Guide *World Visit System* `https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/worldvisit/` and *Data Center Travel System* `https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/datacentertravel/`).
- **What failed in practice:**
  - **Cultural raid centralization and Congested lockouts:** Data Center Travel allowed players across all North American data centers (Crystal, Primal, Dynamis) to travel to Aether to use its cross-world Party Finder for Savage/Ultimate raids. Because Party Finder is scoped per logical Data Center, Aether became the self-reinforcing default raid hub; during raid tiers, Aether worlds lock as "Congested" to travellers while Party Finder on other data centers empties out **[summary]** (Naoki Yoshida / Square Enix Lodestone notices on Data Center Travel congestion and community raid-population reports).
  - **Database-isolation restrictions as anti-dupe fences:** Because a character's retainers, free-company chest, housing estate and market-board seller listings remain authoritative on their Home World's database shard, Square Enix hard-blocks visiting travellers from summoning retainers, selling items on the Market Board, creating, joining or leaving a Free Company, or buying housing while abroad **[opened]** (FFXIV Official Guide `https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/worldvisit/`: "Retainers cannot be summoned, hired, or released from service", "Items cannot be sold on the market, but purchasing is available", "Free companies cannot be created, joined, or left", "Estates and apartments cannot be purchased"). Citation check: the guide lists no Free Company storage restriction; "accessing Free Company storage" was corrected to the guide's Free Company rule. Rather than attempting distributed two-phase commits across Home World databases during high-frequency item storage, FFXIV disables those writes outside the Home World **[inference]**.

---

### Topic 3: Star Citizen server meshing, SpatialOS, EVE Online Time Dilation, Dual Universe and New World

#### Star Citizen server meshing and the Replication Layer

- **How it works:**
  - Cloud Imperium Games (CIG) decouples world state from simulation workers through a central **Replication Layer** (backed by an in-memory/persisted **EntityGraph** database) **[summary]** (Paul Reindell and Benoit Beausejour, CitizenCon *Server Meshing & StarEngine* presentation, and CIG *Letter from the Chairman* / *Server Meshing Q&A* `https://robertsspaceindustries.com/comm-link/transmission/18397-Server-Meshing-And-Persistent-Streaming-Q-A`).
  - Clients connect exclusively to the Replication Layer, never directly to a Dedicated Game Server (DGS). Multiple DGS nodes attach above the Replication Layer, each assigned authority over a spatial zone (" territory") of the EntityGraph **[summary]** (`https://robertsspaceindustries.com/comm-link/transmission/18397-Server-Meshing-And-Persistent-Streaming-Q-A`).
  - Under **Static Server Meshing**, zone boundaries (for example, the jump point between Stanton and Pyro, or fixed planetary/station volumes) are assigned to fixed DGS instances at boot. When a ship crosses a boundary, the Replication Layer switches the entity's authority pointer from DGS A to DGS B without disconnecting the client; DGS A and DGS B both subscribe to entities near the boundary via the Replication Layer so players see and shoot across the seam **[summary]** (CitizenCon Server Meshing demonstration).
  - Under **Dynamic Server Meshing** (planned/in-test), a coordinator splits and merges authority zones at runtime—down to assigning one ship interior or one landing zone to its own DGS when player density spikes **[summary]** (`https://robertsspaceindustries.com/comm-link/transmission/18397-Server-Meshing-And-Persistent-Streaming-Q-A`).
  - Citation check: the comm-link page exists (its title fetches) but its body renders client-side and could not be read, so every Star Citizen claim here stays unconfirmed.
- **What failed in practice:**
  - **The "Error 30000 (30k)" crash and authority-recovery desyncs:** Before the Replication Layer split landed, a DGS crash dropped every player to menu with lost cargo. After separating the Replication Layer, a DGS crash triggers a "Server Error — Please Wait" banner while a replacement DGS spins up and hydrates from the Replication Layer; in live testing, authority handoffs across dense boundaries and post-crash hydration repeatedly surfaced stuck interaction prompts, delayed inventory commits, and missiles/projectiles desyncing across authority borders **[summary]** (Star Citizen Alpha 3.23 / 4.0 Tech Preview Evocati and PTU patch notes and player telemetry reports).

#### SpatialOS (Improbable) and why games built on it shut down or left it

- **How it works:**
  - Improbable's SpatialOS offered a cloud runtime that stored a world as a single distributed entity-component database and dynamically load-balanced simulation across many server-worker processes ("workers") along a spatial grid, with overlapping authority/read regions **[summary]** (Improbable SpatialOS documentation and architecture whitepapers).
- **What failed in practice (cancelled/shuttered titles and developer postmortems):**
  - ***Worlds Adrift* (Bossa Studios — shut down):** In its closure announcement and developer postmortems, Bossa Studios explained that building a physics-heavy persistent sky-island MMO on SpatialOS meant that *"all our work went into making the game work, rather than making the game the experience we wanted it to be"* and cited unsustainable per-player cloud hosting costs alongside severe physics/grappling-hook rubber-banding whenever ships and players crossed worker boundaries **[summary]** (Bossa Studios *The End of Worlds Adrift* official postmortem `https://www.worldsadrift.com/` and Eurogamer / GamesIndustry.biz coverage `https://www.gamesindustry.biz/articles/2019-05-29-worlds-adrift-shutting-down-in-july`).
    - Citation check: GamesIndustry.biz refused fetching. Game Developer's report of Bossa's statement (`https://gamedeveloper.com/business/bossa-shutting-down-i-worlds-adrift-i-says-game-is-no-longer-commercially-viable-`) **[opened]** confirms only part of the quotation: "The challenges that came with our ambitious project meant that all our work went into making the game work"; the words "rather than making the game the experience we wanted it to be" are not in it. Bossa's stated reasons are that the game "just hasn't reached the level of popularity it needs to continue" and "is just no longer commercially viable"; physics, rubber-banding, worker boundaries and hosting costs are not mentioned. **Refuted as a technical root cause:** Bossa's general manager later said "I don't think we would pin the success or failure of the game on that technology" **[opened]** (PC Games Insider, `https://www.pcgamesinsider.biz/news/69671/bossa-doesnt-blame-improbable-for-worlds-adrifts-cancellation/`).
  - ***Mavericks: Proving Grounds* (Automaton Games — studio entered administration):** Aimed for a 1,000-player battle royale / 400-player tactical shooter on SpatialOS; after catastrophic technical delays and high infrastructure burn during closed testing, Automaton entered insolvency **[summary]** (GamesIndustry.biz *Automaton Games enters administration* `https://www.gamesindustry.biz/articles/2019-07-30-automaton-games-enters-administration`).
    - Citation check: GamesIndustry.biz refused fetching. Search extracts of the studio's own notice give its reason as "due to insufficient funding, the development of the Mavericks: Proving Grounds game has now ceased" **[summary]**; the technical-delay and infrastructure-burn causes are unconfirmed.
  - ***Lazarus* (Spilt Milk Studios — shut down) and *Scavengers* (Midwinter Entertainment — shut down):** Both studios cited the operational complexity and cost of SpatialOS; Midwinter ultimately scaled *Scavengers* down to smaller match-based sessions before shutting down **[summary]** (PC Gamer and GamesIndustry.biz reports on SpatialOS game closures).
  - **Technical root cause:** Every time an entity or physics interaction crossed a worker boundary, state had to be serialized through the central SpatialOS runtime fabric to the neighbouring worker process. When 100+ players converged on a single town or battle, either (a) one worker owned the whole crowd and choked on CPU just like a monolithic server, or (b) a worker boundary cut right through the fight, multiplying cross-worker serialization latency and producing severe rubber-banding and ghost hits **[summary]** **[inference]**. Citation check: no opened source attributes any of these closures to worker-boundary behaviour (see the Worlds Adrift check above), so this paragraph is inference only.

#### EVE Online single-shard model and Time Dilation (TiDi)

- **How it works:**
  - EVE Online runs one primary global shard (*Tranquility*). Each solar system is a discrete spatial room connected by stargates; groups of solar systems run on blade servers ("nodes"), and fleet commanders can give CCP 24–48 hours' notice to reinforce a target system onto a dedicated high-spec " Everest" node before a major battle **[summary]** (CCP Veritas / Brian Knox, EVE Online dev blog *Introducing Time Dilation (TiDi)* `https://www.eveonline.com/news/view/introducing-time-dilation-tidi`).
  - When a solar system node's simulation loop falls behind its 1 Hz tick budget under heavy fleet load (thousands of pilots, drones and missile calculations), **Time Dilation (TiDi)** stretches the simulation clock down to a minimum of 10% speed (one 1-second simulation tick takes 10 wall-clock seconds) so every client in that system experiences the same deterministic slowdown rather than arbitrary packet drops **[summary]** (`https://www.eveonline.com/news/view/introducing-time-dilation-tidi`).
  - Citation check (opened): the TiDi blog is by CCP Veritas (no real name on the page). It describes the goal as to "slow down the game clock enough to maintain a very small queue of waiting tasklets", but gives no 10% minimum ("I don't know where that threshold will be"), states no 1 Hz rate, and says nothing about notice or node reinforcement. The reinforcement claim is supported instead by CCP's *Fleet Fight Notification* page cited in [custody.md](custody.md#mmo-transfer-and-failure-evidence), which describes nodes that "can play host to one or more in-game solar systems"; the 24–48 hour figure and the "Everest" node name are unconfirmed.
- **What failed in practice:**
  - **The stargate / jump-tunnel seam under TiDi (B-R5RB and M2-XFE):** While TiDi keeps an *already-loaded* system running down to 10% speed, the seam *into* that system (jumping through a stargate or cynosural field from an adjacent solar system) requires an authority handoff and session change into the overloaded node **[summary]** (CCP Games dev blog *The Bloodbath of B-R5RB* `https://www.eveonline.com/news/view/the-bloodbath-of-b-r5rb` and PC Gamer / Imperium News postmortems on the *Battle of M2-XFE*).
  - At the **Second Battle of M2-XFE**, over 5,000 players were already inside system M2-XFE at 10% TiDi when an attacking coalition attempted to cyno-jump thousands more capital ships across the seam into the same system. The session-transfer queue overwhelmed the node: ships loaded on the server grid without their pilots' clients finishing the jump tunnel ("black screens"), duplicate ghost ship hulls appeared in both the origin and destination systems, weapons failed to cycle, and hundreds of Titans were destroyed before their pilots could load the grid **[summary]** (PC Gamer *EVE Online's biggest ever battle ended in disaster* `https://www.pcgamer.com/eve-onlines-biggest-ever-battle-ended-in-disaster/`).
  - Citation check: the PC Gamer URL returns 404. CCP's own report, *The Second Timer in M2-XFE* (`https://www.eveonline.com/news/view/the-second-timer-in-m2-xfe`) **[opened]**, confirms the seam failure in its own words: "players did have trouble entering the system", "ships not appearing in the right systems – even after going through the jump tunnel", and "As pilots attempted to jump from one heavily loaded to another, they experienced odd and unresponsive game behavior", with the T5ZI-S and M2-XFE nodes "under heavy load". It also lists "duplicated ships/mods where a wreck with loot was in M2-XFE but the fitted ship was in T5ZI-S", which supports the duplicate-hull claim. CCP's *Massacre at M2-XFE* page gives "Titans Destroyed: 257" across the battles. The 5,000-player and 10% TiDi figures and the weapons-not-cycling detail are unconfirmed; CCP's report says M2-XFE had no population cap and does not mention time dilation there.

#### Dual Universe and New World server-per-region choices

- **Dual Universe (Novaquark):**
  - **Mechanism:** Built a "Continuous Single-Shard Cluster" (CSSC) using a dynamic 3D octree that split authority volumes down to small spatial cubes as player density rose, paired with player-editable voxel constructs **[summary]** (Jean-Christophe Baillie / Novaquark dev blogs on CSSC architecture).
  - **What failed in practice:** When hundreds of player-built voxel ships and market stalls gathered at the Alioth District 6 market hub, clients had to stream gigabytes of unbaked voxel mesh deltas while the dynamic octree thrashed across adjacent cells, producing multi-second client freezes and server timeouts. Ultimately, the cloud cost of running the continuous single-shard cluster could not be sustained by subscriptions; Novaquark shut down the official MMO server and released *myDU* (player-hosted dedicated servers) and *openDU* **[summary]** (Novaquark official announcement on *Dual Universe* server transition and *myDU*).
  - Citation check: no URL is given, and search finds no stated reason in Novaquark's shutdown notice; press accounts report only that the official server closed and play continues on player-hosted *myDU*. The cost cause, the Alioth freezes and *openDU* are unconfirmed.
- **New World (Amazon Games):**
  - **Mechanism:** Chose traditional separated worlds capped at 2,000–2,500 concurrent players per world, originally planning a multi-server mesh within each world and later adding cross-world transfer tokens **[summary]** (Amazon Games *New World* developer blogs and postmortems).
  - **What failed in practice (cross-boundary dupes):** Within weeks of launch, players discovered multiple gold and item duplication exploits tied directly to boundary and state-persistence seams: (1) initiating a server transfer or trade, pulling the network cable / blocking packets or triggering a client lag state ("window dragging" / unresponsive client state) so the trade completed on one path while the character rollback restored the items on the origin state, and (2) territory tax / trading post asynchronous settlement races. Amazon Games had to freeze **all** player-to-player trades, guild treasuries and Trading Post orders across all servers multiple times while patching transactional checks **[summary]** (Amazon Games official *New World* forum notices by Community Manager Luxendra and IGN / PC Gamer coverage *New World disables all forms of wealth transfer again due to new gold dupe exploit* `https://www.pcgamer.com/new-world-disables-all-forms-of-wealth-transfer-again-due-to-new-gold-dupe-exploit/`).
  - Citation check: the PC Gamer URL returns 404. NME's report (`https://www.nme.com/news/new-world-disables-all-forms-of-wealth-transfers-to-combat-gold-exploit-3085160`) **[opened]** confirms the freeze, quoting Amazon's list "sending currency, guild treasury, trading post, player to player trading", and explains no mechanism. Amazon's 1.0.5 notes, opened in [economy.md](economy.md#new-world), confirm storage-and-crafting item duplication and "gold duplicating on territory projects we introduced by disabling trading". The server-transfer, cable-pull and window-dragging mechanisms are unconfirmed.

---

### Topic 4: Interest management, area-of-interest grids, priority, bandwidth per player, and Puck's disclosure model

#### How production MMO and large-scale engines run interest management

1. **Spatial partitioning to avoid the $O(N^2)$ broadcast trap:**
   - Checking every client $M$ against every active entity $N$ each tick costs $O(N \times M)$ CPU work and $O(N^2)$ network bandwidth when all players stand in one region **[summary]** (Unreal Engine documentation *Replication Graph* `https://dev.epicgames.com/documentation/en-us/unreal-engine/replication-graph-in-unreal-engine`).
   - Production servers partition dynamic entities into a **2D spatial grid** (for example, Unreal's `UReplicationGraphNode_GridSpatialization2D` or traditional MMO 9-grid / 25-grid cell subscriptions) or an adaptive quadtree/octree **[summary]** (`https://dev.epicgames.com/documentation/en-us/unreal-engine/replication-graph-in-unreal-engine`). Static or rarely moving actors are separated from dynamic actors so the grid only re-bins moving bodies **[summary]**.
   - Citation check (opened): the Replication Graph page says it is "designed to scale well with large numbers of players and replicated Actors" (Fortnite: 100 players, about 50,000 actors) and that standard replication makes each actor "determine whether or not it should send an update to each connected client". It describes grid cells in general but does not name `UReplicationGraphNode_GridSpatialization2D`, and its static-actor counterpart is "dormant" placed actors kept in a separate list.
   - **Cell-border hysteresis:** To prevent an entity oscillating across a grid line from triggering rapid spawn/despawn packets (`EnterAoI` / `LeaveAoI` thrashing), the subscribe radius $R_{\text{enter}}$ is strictly smaller than the unsubscribe radius $R_{\text{leave}}$ (or the observer subscribes to a $3 \times 3$ neighbourhood of cells while dropping entities only when they leave a padded outer ring) **[summary]**.

2. **Priority, relevance, and per-client bandwidth budgets:**
   - Even inside a single AoI radius (say 100 metres), 200 players in a city plaza or raid boss fight exceed a client's downstream packet budget if sent at full tick rate **[summary]**.
   - Engines therefore compute a per-connection **priority score** (combining distance, whether the entity is inside the observer's view frustum, whether the entity recently attacked or targeted the observer, and time since last update) and pack updates in priority order until the connection's **per-tick byte budget** is full **[summary]** (Unreal Engine *Actor Priority* and *Replication Graph* documentation `https://dev.epicgames.com/documentation/en-us/unreal-engine/actor-priority-in-unreal-engine`).
   - Typical downstream per-player bandwidth targets in large-scale multiplayer games sit around **10–30 KB/s** in normal play and burst to **50–100 KB/s** in dense combat, kept bounded by decimating update frequency (`NetUpdateFrequency`) with distance (for example: 20–30 Hz within 15 m, 10 Hz at 15–50 m, 2–5 Hz beyond 50 m) rather than dropping the connection when a queue fills **[summary]**.
   - Citation check (opened): the Actor Priority page confirms the priority inputs, "An actor's current network priority is calculated based on the time since the actor was last replicated", with distance and view-direction multipliers, and that a saturated connection gives each actor "a fair share of the available network bandwidth resources" in proportion to priority. The bandwidth figures and per-distance rates in the preceding bullet have no cited source and are unconfirmed.

3. **AoI as an anti-cheat boundary:**
   - Any entity position or hidden state sent over the wire to a client's memory—even if the client renderer hides it behind fog of war, stealth or a wall—can be read by a passive packet sniffer or memory-reading ESP/maphack **[summary]** (Riot Games engineering blog on *Valorant* Server-Side Fog of War `https://technology.riotgames.com/news/demolishing-wallhacks-valorants-fog-war`). Withholding the bytes at the server's outbound sink is the only defence against passive observation cheats **[summary]**.
   - Citation check (opened): the URL now redirects to `https://www.riotgames.com/en/news/demolishing-wallhacks-valorants-fog-war` (Paul "Arkem" Chamberlain, VALORANT anti-cheat lead). It confirms the withholding: "we wouldn't send their location to enemy players, keeping them hidden until they decided to peek the angle", aimed at "cheats that take advantage of a game client's access to information, like wallhacks". It does not discuss packet sniffing or memory reading.

#### How Puck's codebase implements disclosure and interest management today

In this worktree, Puck divides outbound filtering across three mechanisms:

1. **Admission-level document disclosure (`WorldDisclosureTier`):**
   - `src/Puck.World.Schema/WorldAdmission.cs:147-164` **[opened]**:
     ```csharp
     /// <param name="Disclosure">How much of this world's document a peer verified under this entry receives (see
     /// <see cref="WorldDisclosureTier"/>). Absent resolves to <see cref="WorldDisclosureTier.Presentation"/>, so a
     /// projection-only wire is the default and nothing has to be edited to stay closed.
     /// <see cref="WorldDisclosureTier.Replica"/> is reachable only by authoring it.</param>
     ```
   - `docs/architecture/worlds.md:770-775` **[opened]** defines the three tiers: `Frames` (rendered pixels only), `Presentation` (`puck.world.projection.v1`, carrying only rendered and embodied-from state with no rules or authority sections), and `Replica` (the full document with withheld state cells stripped).

2. **State-cell disclosure (`WorldRecipientView` and `WorldDeliveryCursor` in `docs/plans/disclosure.md`):**
   - `docs/plans/disclosure.md:31-36` **[opened]**:
     ```text
     **The guarantee.** To a recipient the policy excludes from a cell, the world is
     indistinguishable from the same world without that cell, in everything the
     engine answers at tick granularity: content, structure (slots, offsets, order,
     reservations), counts, identity (ids, keys), and the timing of change, including
     every document version, revision and sequence the recipient is shown, the size
     and number of deliveries it receives per tick, and the lifetime of its streams.
     ```
   - [Disclosure, decision 5](../disclosure.md#5-a-delta-is-a-difference-of-views-owned-by-the-stream) avoids $O(N \times M)$ state composition by caching row images per audience class:
     ```text
     Composition of one row's image for a
     tick is cached by (row, audience class), where the audience class is the set
     of policies that admit the recipient, so a thousand spectators with the same
     rights compose once.
     ```

3. **Snapshot body disclosure (`WorldObserverDisclosure` and `WorldOutputHub.Redact`):**
   - `src/Puck.World.Schema/WorldObserverDisclosure.cs:8-18` **[opened]**:
     ```csharp
     public enum WorldObserverDisclosureMode : byte {
         /// <summary>Every active body. The default, and what every world authoring no policy delivers.</summary>
         All,

         /// <summary>Only bodies within <see cref="WorldObserverDisclosure.Radius"/> metres of the observer's own body,
         /// plus that body itself. An observer with no body of its own is delivered nothing.</summary>
         Radius,

         /// <summary>Only the observer's own body. An observer with no body of its own is delivered nothing.</summary>
         SelfOnly,
     }
     ```
   - `src/Puck.World.Schema/WorldObserverDisclosure.cs:27-34` **[opened]** sets a flat global snapshot cadence per world:
     ```csharp
     /// <param name="UpdateSeconds">The minimum interval between remote projection snapshots. Zero requests every
     /// authority tick; the default is 0.03 seconds (30 Hz on the standard 240 Hz authority after whole-step
     /// quantization). Local in-process sinks are unaffected.</param>
     ```
   - `src/Puck.World.Server/WorldOutputHub.cs:372-406` **[opened]** runs `Redact` per non-full subscriber by scanning `entries.Length` linearly to find `observerPosition` and then scanning `entries.Length` a second time to filter by `disclosure.Policy.Discloses(...)`.
   - **Four MMO-scale gaps in Puck's current body interest management (`[inference]` from the `[opened]` code above):**
     1. **$O(N \times M)$ scan in `WorldOutputHub.Redact`:** For $M$ remote observers under `WorldObserverDisclosureMode.Radius` and $N$ active bodies, `Redact` performs $2 \times N \times M$ linear array scans every delivery tick with no spatial grid.
     2. **No radius hysteresis:** A body stepping back and forth across `Radius` enters and leaves the observer's snapshot on consecutive ticks.
     3. **Unembodied observers default to `All` (Attack A16):** If a world leaves `population.disclosure` at its default (`Mode = WorldObserverDisclosureMode.All`), any admitted unembodied observer receives every body's position (`docs/plans/mmo-foundations/attacks.md:536-542` **[opened]**). Conversely, under `Radius`, `"An observer with no body of its own is delivered nothing"` (`src/Puck.World.Schema/WorldObserverDisclosure.cs:13` **[opened]**), which breaks legitimate unembodied portal `window` or spectator cameras unless they have a spatial anchor.
     4. **Hard disconnect on backpressure instead of priority decimation (Attack A12):** `WorldFederationProjectionSink` queues at most `PendingDeliveryLimit` (8) deliveries and detaches the sink with `world.observation.backpressure` when full (`docs/architecture/worlds.md:850-855` **[opened]**; `docs/plans/mmo-foundations/attacks.md:493-495` **[opened]**), rather than dropping or decimating distant low-priority entity updates to stay within the link's bandwidth.

---

### Topic 5: Contested places across an authority boundary — RTS lockstep, fighting-game rollback (GGPO), state-hash desync detection, and referee failure modes

#### Peer-to-peer deterministic lockstep in RTS games

- **How it works:**
  - Pioneered at scale by Ensemble Studios for *Age of Empires* and documented by Paul Bettner and Mark Terrano in their GDC paper *"1500 Archers on a 28.8: Network Programming in Age of Empires and Beyond"* **[summary]** (`https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond`).
  - Instead of replicating positions and health for 1,500 units, every peer runs an identical deterministic simulation step-for-step. Peers transmit only player **command turns** scheduled for turn $T + k$ (typically 100–250 ms in the future), plus a periodic cryptographic or CRC **state hash** of the simulation memory **[summary]** (`https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond`).
- **What failed in practice:**
  - **Input delay scales with the worst peer's RTT:** Because no peer may advance turn $T$ until every participant's command packet for turn $T$ arrives, command latency equals at least half the round-trip time of the slowest link **[summary]**.
  - **Stalls and the "Waiting for Player" freeze:** If any peer drops packets, hits a GC pause, or pulls their network cable, the entire simulation freezes globally for every player **[summary]** (`https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond`).
  - **Floating-point and compiler desyncs ("Out of Sync" errors):** A single bit difference in x87 vs SSE floating-point rounding, uninitialized memory, iteration order over a hash set, or a presentation read mutating simulation RNG causes state hashes to diverge at turn $T$, immediately aborting the match with an unrecoverable Out-of-Sync error **[summary]** (`https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond`).
  - Citation check (opened): the article confirms the lockstep shape: "commands issued during turn 1000 would be scheduled for execution during turn 1002", "Turns were typically 200 msec in length", "the game can really only run as fast as the slowest machine", and "Any simulation that ran differently was tagged as "out of sync" and the game stopped", with checksums over "the world, the objects, the pathfinding, targeting and every other system". Its named desync causes are compounding small differences and random-number call counts; x87/SSE rounding, uninitialized memory and hash-set order are not in it. The page bylines Paul Bettner alone.

#### Rollback netcode in fighting games (GGPO)

- **How it works:**
  - Created by Tony Cannon (*GGPO — Good Game Peace Out*, open-sourced at `https://github.com/pond3r/ggpo`) and detailed in Michael Stallone's GDC talk *"8 Frames in 16ms: Rollback Networking in Mortal Kombat and Injustice 2"* (`https://gdcvault.com/play/1025471/8-Frames-in-16ms-Rollback`) and Infil's technical analysis *"Explaining how fighting games use delay-based and rollback netcode"* (`https://words.infil.net/w02-netcode.html`) **[summary]**.
  - Citation check (opened): the GDC Vault URL first cited here (`/play/1024001/`) is Timothy Ford's *'Overwatch' Gameplay Architecture and Netcode*, not Stallone's talk; the corrected URL above is Stallone's (NetherRealm, GDC 2018), whose abstract confirms "rolling back and simulating up to 8 game frames in 16ms" and says nothing about snapshots or checksums. The Infil article (the old `ki.infil.net` URL redirects to `words.infil.net`) is bylined "Infil"; the name Ricky Pusch does not appear on it. Neither the GGPO README nor `ggpo.net` names Tony Cannon; `ggpo.net` dates the SDK to 2009 and describes prediction "based on previously seen inputs" and re-simulation "from the point of divergence to the current visible frame".
  - Rollback is **deterministic lockstep plus speculative input prediction and a fast ring-buffer state snapshot**:
    - At tick $T$, if the local machine has not yet received the remote peer's input for tick $T$, it predicts the remote peer repeated their input from tick $T - 1$ and steps the simulation immediately with near-zero local input delay **[opened]** (Infil, part 4, `https://words.infil.net/w02-netcode-p4.html`: rollback will "duplicate the last known input from the remote player for the current missing frame").
    - When the true remote input for tick $T - d$ arrives ($d$ ticks late, typically up to a rollback window of 7–8 frames), if the true input differs from the predicted input, the engine restores the saved state snapshot from tick $T - d$, replays ticks $T - d \dots T$ with both players' true inputs inside a single frame budget, and resumes **[summary]** (`https://gdcvault.com/play/1025471/8-Frames-in-16ms-Rollback`; the 7–8 frame window is not in the abstract, and Infil's pages give no rollback window).
    - Both peers exchange periodic checksums of the confirmed (non-speculative) frame $T_{\text{confirmed}}$ (the latest tick for which both peers have received each other's real inputs) to detect simulation desynchronization **[summary]** (`https://github.com/pond3r/ggpo`). Citation check: the GGPO README does not mention checksums; Infil part 5 (`https://words.infil.net/w02-netcode-p5.html`) **[opened]** says GGPO "can tell your game when two machines are out of sync, and by how much" and lists "desync detection" among a rollback game's duties, without naming the mechanism.

#### What failed in practice in P2P lockstep and rollback (and why it matters for Puck's Attack A6)

1. **One-sided rollback and lag-switching:**
   - When Peer A has an unstable connection or uses a lag-switch (withholding outbound packets for 100 ms and then bursting them in a single UDP datagram) while Peer B has a steady connection, Peer A receives Peer B's inputs smoothly on time, whereas Peer B receives Peer A's inputs late in bursts and suffers constant visual teleportation (rollback artifacts) without warning frames on Peer A's attack startups **[summary]** (`https://ki.infil.net/w02-netcode.html` and fighting-game clock-sync analyses on *Street Fighter V*'s early rollback implementation).
   - **Mitigation:** GGPO tracks the **frame-advantage differential** (how many ticks ahead Peer A's local clock is running relative to Peer B's confirmed inputs) and forces the ahead peer to stall for 1–2 ticks (or micro-adjust tick pacing) when the rift exceeds half the round-trip time, plus refuses inputs stamped outside the rollback window **[summary]** (`https://github.com/pond3r/ggpo` and `https://ki.infil.net/w02-netcode.html`).
   - Citation check (opened, Infil part 5): the one-sided rollback is confirmed but attributed to clock drift, not to an unstable link or a lag switch: "this rollback is entirely one-sided; only the player who is ahead of the other will experience the effects", because "different computers will run software at slightly different rates". The stall mitigation is confirmed: most rollback systems "briefly pause the player who is ahead for 1 or 2 frames so the player who is behind can catch up". Lag switches are not covered by any cited page, so the lag-switch threat above is inference.
2. **Intentional desync exploits to nullify a loss:**
   - In pure P2P fighting games and RTS matches (*Street Fighter*, *Tekken*, *Total War*, *Company of Heroes*), when two peers report a state-hash mismatch to the matchmaking service at tick $T$, the central service sees only `Hash(A, T) != Hash(B, T)`. Because the service did not run the simulation, it **cannot tell whether A or B modified their memory/rules** **[summary]**.
   - Cheaters exploited this by hooking memory or triggering a deliberate state mutation whenever their health approached zero, causing a "Desync Detected — Match Nullified" abort so they never lost ranked points **[summary]**. Citation check: no source is cited for this exploit or for the named games; it is unconfirmed, and the rage-quit attribution and relay-witness bullets below are likewise uncited.
3. **The P2P disconnect / rage-quit attribution dilemma:**
   - If Peer A pulls their Ethernet cable (or blocks UDP packets solely to Peer B while keeping their HTTPS heartbeat open to the matchmaking server), both A and B report to the server: *"My opponent stopped responding at tick $T$."* Without a relay or witness on the data path, a third party cannot prove who dropped the link **[summary]**.
   - **How modern games solved both (the relay + deterministic referee pattern):**
     - **Packet relay / witness (Steam Datagram Relay / UDP relay):** Routing P2P packets through a lightweight relay edge lets the infrastructure observe who stopped sending tick-stamped inputs and prevents IP/DDoS harvesting **[summary]**.
     - **Lazy deterministic referee replay:** Because a deterministic simulation needs only the initial seed/document and the compact input tape (a few kilobytes for an entire duel), a third-party referee world never needs to simulate every fight live. Only when `Hash(A, T) != Hash(B, T)` is reported, the referee replays the signed input tape up to tick $T$: whichever peer's signed state hash disagrees with the deterministic replay is provably lying and can be penalized or banned automatically **[inference]** (combining deterministic lockstep/GGPO checksum verification with Puck's Rule 4 replay verification).

---

## 3. Options for Puck

### Question 5a & the Seam Hypothesis: Which boundaries are visible portals and which are hidden?

**The hypothesis under test** (`docs/plans/mmo-foundations.md:76` **[opened]**):
> `"Seams: hidden for shards and layers, visible where the fiction is travel."`

#### Option S1: Strict fiction-aligned split — visible portals for travel and scope changes, `WorldAdjacency` for continuous spatial shards, and unobservability-gated cohort transfer for population layers

- **Mechanism:**
  1. **Visible portals (`WorldPlacementFace` + `WorldPlacementPortal` + `projection: window`):** Used whenever the player crosses between distinct places, enters an instance (`durability: ephemeral, scope: group`), visits a personal hub (`scope: user`), travels across realms/servers with different rules, or engages a nested screen/table (`docs/architecture/worlds.md:468-487`, `:564-585`, `:1101-1120` **[opened]**).
  2. **Hidden spatial seams (`WorldAdjacency`):** Used when one continuous landscape is partitioned across multiple global persisted authorities (`src/Puck.World.Schema/WorldAdjacencies.cs:144-151` **[opened]**). No placement or frame exists; `WorldAdjacencySceneEmitter` draws the neighbour's solid terrain and bodies across the boundary (`src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:10-35` **[opened]**), `WorldAdjacencyContactField` resolves ground and body contact across the overlap (`src/Puck.World.Server/WorldAdjacencyContactField.cs:6-24` **[opened]**), and ownership switches once the body crosses the derived deadband (`docs/architecture/worlds.md:503-517` **[opened]**).
  3. **Hidden population layers (non-spatial cohort transfer with an unobservability gate and hop cooldown):** When a zone's population drains or splits into parallel layers (`WorldDestination` pointing to the same `WorldReference`), the engine transfers a player or party (`WorldPortalTravel.Party`, `PartyAllOrNothing`) only when an **unobservability predicate** holds:
     - No active combat, aggro or incoming interaction;
     - No active interaction/gathering channel or cast in progress (preventing WoW's mid-cast node disappearance);
     - Either inside an interior/transition room where outdoor entities are occluded, or accepted via a GW2-style voluntary consolidation prompt;
     - Stamped with an **increasing layer-hop cooldown** on the traveler's mobility record so accepting party invites cannot be used to farm rare spawns or escape PvP (closing the *WoW Classic* layer-hop exploit).
- **What it costs:** Requires authoring transition corridors or an unobservability predicate for layer merges, and tracking a last-layer-transfer timestamp/epoch on the traveler's mobility credential.
- **Citation check:** the corrected WoW sources support this option more directly than first stated: Blizzard's own layering design already deferred a transfer during combat and before looting and added a longer delay after PvP combat, and its hotfix added an increasing hop cooldown (Topic 1).
- **What it fails at:** Cannot hide a layer switch in the middle of an open field while the player is actively looking at harvestable nodes or NPCs; in that state, the transfer must wait until the player enters a transition volume or clicks a GW2-style prompt.
- **Attacks closed or left open:** Preserves atomic crossings (`docs/architecture/worlds.md:1365-1367` **[opened]**); prevents layer-hop parallel farming alongside A7; leaves A10 (seam shoving across `WorldAdjacency`) to be closed by Option C3 below.

#### Option S2: Diegetic framed transitions everywhere (no silent population layers)

- **Mechanism:** Every copy of a high-traffic zone is either selected explicitly at an entry doorway/crystal (FFXIV `Zone ① / ② / ③` style) or separated by a physical threshold (a mist arch, lift, gatehouse or subway car) where the portal's `WorldDestination` resolves the target layer once for the party (`docs/architecture/worlds.md:485-486` **[opened]**).
- **What it costs:** Gives up silent background re-layering inside a single open field; if a layer empties out, remaining players must walk through a threshold or use a transport fixture to join a fuller copy.
- **What it fails at:** An unexpected crowd flash-mobbing an already-entered open field cannot be split into two layers without someone walking through a threshold.
- **Attacks closed or left open:** Eliminates mid-cast node despawns and party-invite in-place layer hopping by construction, because a layer change only happens by physically crossing a portal plane.

#### Option S3: Dynamic reactive spatial splitting (SpatialOS / Dual Universe octree style)

- **Mechanism:** Continuously split and merge `WorldAdjacency` boxes at runtime wherever player density rises.
- **What it costs and fails at:** Directly contradicts `docs/architecture/worlds.md:583-585` **[opened]** (`"Destinations are boot-authored document data"`) and `docs/architecture/worlds.md:1412-1414` **[opened]** (`"why the authoring guidance is not 'put a world wherever people fight', which is reactive topology, but 'do not run an authority boundary through a place designed to be contested'"`). In practice, this is the exact mechanism that caused worker-boundary thrashing in *Worlds Adrift* and *Dual Universe* (Topic 3). Citation check: that external evidence did not hold (Bossa declined to blame SpatialOS, and no Dual Universe cause was confirmed), so the case against S3 rests on Puck's own architecture rules and the EVE M2-XFE seam failure alone.

---

### Question 5b: How the portal mechanism runs without a frame

In Puck's current codebase, "running without a frame" is already supported at two levels, and can be exposed in two ways:

#### Option F1: Use the existing two-path split (`WorldAdjacency` for continuous terrain, frameless `WorldPlacementFace` for doorway planes)

- **Evidence in the codebase (`[opened]`):**
  1. **`WorldAdjacency` has no placement or frame at all:** `src/Puck.World.Schema/WorldAdjacencies.cs:144-146` **[opened]** states: `"It is topology, not visible furniture: no placement, screen, or portal is required. The compiler derives the overlap needed for observation, interaction, grounding, and handoff from the two delivered documents."` And `src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:26-27` **[opened]** notes: `"A placement with no solid facet (a portal frame, a decorative prop) never qualifies"`.
  2. **A `WorldPlacementFace` portal does not require a visual frame around its aperture:** In `worlds/debug/paintings.puck:105-153` **[opened]**, the visible frame (`lip`, `mat`, `border`, `plaque`) is authored SDF creation geometry around the `rim`/`opening` subtraction box. The engine's window glass simply fills the creation's aperture edge-to-edge with no bezel (`docs/architecture/worlds.md:1105-1107` **[opened]**: `"the glass shows it edge to edge, with no bezel. That glass lies inside the face's own shape, a centimetre short of each of its faces, so it fills whatever aperture the creation cuts through that shape"`), and the crossing trigger fires on the swept origin passing through that plane (`docs/decisions/engine-design.md:259-260` **[opened]**). Furthermore, `src/Puck.World.Schema/WorldPlacement.cs:306-307` **[opened]** notes that `"the door and the screen it shows are independent facts about the same face."`
- **How an author writes a frameless portal today:** Author a creation consisting solely of an invisible/non-solid aperture shape with a declared face, place it with `WorldPlacementFace(Face, Source, Portal: new WorldPlacementPortal(..., Arrival: WorldPortalArrival.Mapped, Counterpart: ...))`. If `Source` is a `window` session, the aperture renders the seamless parallax view of the destination (`projection: window`) with zero bezel; if `Source` is `none` (or the destination is resolved via script/region), it acts as an invisible swept crossing plane.
- **What it costs:** Zero C# architectural churn; only a standard-library template in `worlds/standard/` (an `aperture` or `threshold` template alongside `worlds/debug/painting-kit.puck:1-38` **[opened]**) so authors do not have to hand-roll an empty creation to get a frameless doorway.

#### Option F2: Allow `WorldAdjacency` to optionally attach an off-axis `window` fallback when direct geometry projection is unavailable or across non-persisted scopes

- **Mechanism:** Today `WorldAdjacency` requires a `global persisted` destination (`src/Puck.World.Schema/WorldAdjacencies.cs:149` **[opened]**) and projects solid SDF geometry directly into the local scene (`WorldAdjacencySceneEmitter`), whereas `WorldPlacementPortal` (`Arrival: Mapped`) works with any `WorldDestination` (including `ephemeral` group dungeons and `user` hubs) and renders the far side as an off-axis raster/raymarched `window` (`docs/architecture/worlds.md:1101-1105` **[opened]**). Allowing a frameless boundary to switch between direct SDF overlap (`WorldAdjacencySceneEmitter`, when the neighbour shares lighting/sky) and a frameless `window` plane (when the neighbour has its own sky clock, lighting or `group`/`user` scope, `docs/architecture/worlds.md:1121-1126` **[opened]**) lets an open archway into a personal hub or group dungeon have zero visible frame while keeping its own sky and scope.
- **What it costs:** Requires teaching `WorldAdjacency` or a frameless `threshold` template to host a `window` view without solid frame geometry.

---

### Question 5c: How interest management maps onto disclosure tiers

`docs/plans/disclosure.md:618` **[opened]** explicitly watches this seam:
> `"A separate body-disclosure view (WorldOutputHub.Redact, WorldSinkDisclosure) folded into the recipient view | Watch | Body redaction is the same shape (a per-sink policy applied at the hub) and the natural second occupant of the view"`

#### Option I1: Fold spatial AoI, hysteresis, spectator anchors and priority-tiered rate decimation into `WorldRecipientView` and `WorldDeliveryCursor`

- **How it maps onto the disclosure tiers:**
  1. **`WorldDisclosureTier.Frames`:** No document or entity snapshot crosses the wire; interest management is the camera frustum and `WorldPortalVisibility` (`docs/architecture/worlds.md:1092-1096` **[opened]**).
  2. **`WorldDisclosureTier.Presentation` (default for players and federated mirrors):**
     - **State rows:** Filtered by `WorldRecipientView` (`HiddenCells.Omit`, `Count`, `Placeholder`) and diffed per stream by `WorldDeliveryCursor` (`docs/plans/disclosure.md:419-459` **[opened]**). Citation check: both types are the disclosure plan's design and neither exists in `src/` yet (as `attacks.md:107-110` says); today state rows are filtered by `WorldStateDisclosure` (`src/Puck.World.Schema/WorldStateDisclosure.cs`) under the `HiddenCells` policy (`src/Puck.State/StateVisibility.cs:8`), so this option depends on that plan landing first.
     - **Dynamic bodies (`EntitySnapshot`s) and responsive placements:** Folded into `WorldRecipientView` + `WorldDeliveryCursor` with three MMO-scale upgrades over `WorldOutputHub.Redact`:
       - **Spatial grid index instead of $O(N \times M)$ linear scan:** Partition active bodies into fixed-point 2D grid cells once per tick on the authority, so each observer under `Radius` collects only overlapping cells in $O(k)$ where $k$ is nearby bodies.
       - **Enter/leave hysteresis (`Radius` + `Hysteresis`):** Track which body slots a `WorldDeliveryCursor` currently holds in its AoI set; admit at `Radius` and drop at `Radius + Hysteresis` so border-straddling bodies do not flicker.
       - **Explicit unembodied observer policy (closes Attack A16):** Replace the implicit fallback where `All` exposes every body to an unembodied connection (`docs/plans/mmo-foundations/attacks.md:537-542` **[opened]**) and `Radius` delivers nothing (`src/Puck.World.Schema/WorldObserverDisclosure.cs:13` **[opened]**). Require an unembodied observer under `Radius` to read only from an authorised camera/portal anchor (the `window` counterpart frame or spectator camera position), or receive zero bodies unless the admission row explicitly grants full-map observation.
       - **Distance/priority cadence tiers inside `WorldDeliveryCursor` (mitigates Attack A12):** Instead of one flat `UpdateSeconds = 0.03f` (`src/Puck.World.Schema/WorldObserverDisclosure.cs:33` **[opened]**) and detaching the sink when `PendingDeliveryLimit = 8` fills, let `WorldDeliveryCursor` decimate distant body pose updates (for example: full rate within near radius, half/quarter rate in mid/far rings, while keeping `WorldClockAnchorLedger` affine clock anchors intact) so a dense town hub degrades distant animation smoothness before disconnecting the peer.
  3. **`WorldDisclosureTier.Replica`:** Receives the full disclosed document and state cells the recipient's `WorldRecipientView` permits (`docs/plans/disclosure.md:237-241` **[opened]**), used for trusted shadows, co-simulating peers or full-map spectators.
- **What it costs:** Replaces `WorldOutputHub.Redact`'s stateless two-loop scan (`src/Puck.World.Server/WorldOutputHub.cs:372-406` **[opened]**) with a per-tick spatial grid bin and per-cursor AoI membership bitset.
- **Attacks closed:** Closes **A16** (unembodied scouting under `Presentation`), narrows **A12** (backpressure disconnects in crowds), and aligns body visibility with the fog-of-war skirmish fixture already planned in `docs/plans/disclosure.md:589-590` **[opened]** (`"a small fog-of-war skirmish (hidden positions, physics, a search over what a side can see)"`).

#### Option I2: Keep `WorldObserverDisclosure` as a separate post-tick filter in `WorldOutputHub`, adding only a spatial grid and an `Unembodied` mode field

- **What it costs:** Smaller diff now; leaves `WorldRecipientView` purely about `StateArena` rows.
- **What it fails at:** Fails the fog-of-war game in `docs/plans/disclosure.md:589-590` **[opened]** whenever a body's position is reflected in both `EntitySnapshot`s and state/placement rows, and cannot do per-stream hysteresis or priority rate decimation because `WorldOutputHub.Redact` is stateless across ticks.

---

### Attack Question 4 & Attacks A6, A10 and A16: Where a contested place runs

`docs/plans/mmo-foundations/attacks.md:628-636` **[opened]** frames the collision:
> `"Where does a contested place run? (A6.) Lane C. 'Do not run an authority boundary through a place designed to be contested' and 'players hosting their own authoritative worlds is a goal' collide at a duel between two player hosts. Lane C's seam research should weigh (a) co-location onto a platform silo for every fight, (b) mutual deterministic simulation with hash comparison between the two peers, which Puck's determinism makes available and no server-authoritative MMO can use, and (c) a transactional proposal protocol, against what fighting games and peer-to-peer RTS lockstep have learned about stalls and desync referees."`

#### Option C1 (Brief's (a)): Co-location onto a neutral third-party authority (platform silo or mutually trusted referee world)

- **Mechanism:** When two bodies owned by untrusted player hosts X and Y enter a contested region or accept a duel/combat engagement, both bodies transfer (`world.transfer`) into a third world $S$ whose authority neither combatant controls (a platform silo or a mutually vouched community server). $S$ runs the simulation tick, clamps inputs to kit envelopes (`src/Puck.World.Server/WorldBody.Step.cs:1189-1194` **[opened]**), withholds hidden state via `WorldRecipientView` (planned in `docs/plans/disclosure.md`, not in the tree; until it lands, hidden-state safety on $S$ rests on today's `WorldStateDisclosure`), and logs crossing/settlement records to a durable journal (`docs/architecture/worlds.md:1284-1295` **[opened]**).
- **What it costs:** Requires reachable third-party server capacity for every trustless fight and adds a network hop from both players to $S$, plus the preemptive transfer before combat starts (`docs/architecture/worlds.md:1462` **[opened]**: `"Co-locating on the first effect puts the migration on the first swing, the most latency-sensitive moment in the game; preemptive binding moves it to walking"`).
- **What it fails at:** Cannot run a trustless duel between two desktop player hosts when offline from a platform silo or when neither player wants to leave their hosted world.
- **Attacks closed or left open:** Closes **A3** (selective rollback by a combatant host), **A4** (host reading opponent's hidden state), **A5** (chosen seed by a combatant host), **A6** (host lying about hit outcomes or enjoying zero-latency advantage), **A8** (restart replay against a desktop host), and **A10** (seam shoving).

#### Option C2 (Brief's (b)): Mutual deterministic simulation (rollback or lockstep) with per-tick state-hash comparison between the two peers, backed by a lazy referee on dispute

- **Why Puck is uniquely placed to do this (`[opened]` + `[inference]`):**
  - Traditional MMOs use floating-point physics and non-deterministic server loops, making P2P co-simulation impossible. Puck enforces fixed-point deterministic simulation across every machine (`AGENTS.md` Rule 4; `Puck.Maths` `FixedQ4816`; `docs/plans/mmo-foundations/attacks.md:363-366` **[opened]**: `"Puck's determinism (rule 4) makes (c) unusual among engines: two authorities can each simulate the whole encounter and check the other's hash, which is how fighting-game rollback netcode works between two peers with no server"`).
- **Mechanism:**
  1. **Symmetric encounter slice:** X and Y agree on the compiled encounter definition, initial state snapshot, and a commit-reveal seed (closing A5).
  2. **Signed per-tick input exchange + GGPO-style rollback (within a bounded window, e.g., 8–16 ticks at 60/240 Hz):** Each peer signs its per-tick `CommandSnapshot` `(tick, input, confirmedTick, stateHashAtConfirmedTick)`. Each peer steps immediately using predicted remote input and rolls back within its ring buffer when the signed remote input arrives, eliminating the one-sided zero-latency host advantage noted in A6 (`docs/plans/mmo-foundations/attacks.md:344` **[opened]**).
  3. **Addressing the three P2P failure modes from Topic 5:**
     - *Lag-switching / one-sided rollback:* Enforce a hard input-window horizon $\Delta T_{\max}$ and GGPO rift balancing; an input arriving past $\Delta T_{\max}$ is refused and replaced by the held input.
     - *Stalls / rage-quit disconnect attribution:* Route the signed input stream through a lightweight packet witness/relay (or require periodic co-signed tick checkpoints). If X stops sending inputs at tick $T$, the witness attests that X went silent, so X forfeits rather than causing an unattributable draw.
     - *Desync referee (lazy replay on hash mismatch):* If `Hash(X, T) != Hash(Y, T)`, neither peer has to trust the other. Either peer submits the initial state plus the compact sequence of signed `CommandSnapshot`s for ticks $0 \dots T$ (a few kilobytes) to an arbiter/silo. The arbiter runs `replay.verify` over the fixed-point simulation once: whichever peer's signed `stateHashAtConfirmedTick` disagrees with the deterministic replay forged its simulation and loses its escrowed stake/reputation.
- **What it costs:**
  - Both peers must know every state cell that affects the physics/combat hash of the encounter. Any hidden state (such as a stealth position or a secret hand of cards) cannot be simulated in plain text by both peers without leaking it to the peer's host process (Attack A4).
  - Scales to 1v1 or small party skirmishes (2–8 peers), not a 100-player melee (where lockstep stalls on the slowest of 100 links).
- **Citation check:** the opened sources confirm the lockstep and rollback mechanics this option borrows (command scheduling, slowest-peer pacing, out-of-sync stops, last-input prediction, stalling the ahead peer), but the lag-switch, intentional-desync and disconnect-attribution failure modes it is designed against are uncited (Topic 5), so its witness and referee steps answer threats this page infers rather than documents.
- **Attacks closed or left open:** Closes **A6** (neither peer can lie about hit resolution or enjoy zero-latency host advantage) and **A3** (signed tick hashes and input tapes prevent silent rollback of a losing fight). Leaves **A4** open if the combat depends on hidden state inside the co-simulated slice (unless hidden choices use commit-reveal), and requires a witness/arbiter to settle stalls and hash disputes.

#### Option C3 (Brief's (c)): Transactional cross-authority proposal protocol and clamped/taped seam contact

Implementation status: `WorldPopulation.AdmitAdjacencyResponse` bounds what a
neighbour's bodies and geometry together move a local body per tick by its
collider reach and the overlap depth, and keeps the body and its outward speed
behind every ownership face it is still owned behind, so a correction never
crosses the ownership deadband. Contact settles by authority pair. The design
below is the round's proposal; tick-addressed input tapes remain in the
[neighbour replay packet](../open-items.md#transfers-federation-and-server).

- **Mechanism:**
  1. **Fix `WorldAdjacencyContactField` (Attack A10 and the Track 3 replay hole):**
     - Today, `src/Puck.World.Server/WorldAdjacencyContactField.cs:25-29` **[opened]** warns that neighbour poses arrive as presentation floats (`FixedVector3.FromVector3(value: neighbour.CurrentPosition(index: entity))`, line 119 **[opened]**) and applies whatever displacement `FixedDynamicBodyContacts.TryCorrection` returns when `WorldCrossAuthoritySettlement.LocalResponds` selects the local side (`lines 106-134` **[opened]**).
     - Replace raw float-snapshot contact with **tick-addressed, taped fixed-point neighbour records** and clamp any cross-seam contact correction to:
       1. A per-tick maximum displacement ceiling derived from the local world's accepted speed/impulse envelope (so a lying neighbour reporting a phantom solid collider moving at $1000\text{ m/s}$ cannot launch or shove a local body across the border); and
       2. Never allowing a neighbour-induced contact correction alone to push a local body across the ownership deadband unless the local body's own velocity/intent moved toward the border.
  2. **Cross-seam interaction proposals:** Instead of `WorldCrossAuthoritySettlement.LocalResponds` letting a hash-selected authority adjudicate every contact between two authorities unilaterally **[opened]**, an authority whose body initiates an action across an adjacency seam emits a **tick-stamped, signed interaction proposal** `(attackerAddress, targetAddress, interactionId, origin, direction, tick)` across the federation link. The target's own authority validates that:
     - The attacker's reported pose was within its kit speed envelope from its previous tick;
     - The interaction reach is within the compiled `WorldOverlapTerms.InteractionReach` (`src/Puck.World.Schema/WorldAdjacencies.cs:653-656` **[opened]**); and
     - Applies the effect to its own body under its own rules (preserving Invariant 1: only a body's authority writes to that body).
- **What it costs:** Adds one federation round-trip before a cross-seam hit takes effect on the target, and still trusts each authority not to make its own body invincible (`docs/plans/mmo-foundations/attacks.md:354-360` **[opened]**).
- **What it fails at:** If Player Host Y simply refuses to apply damage proposals to Y's own body, Y is immortal on Y's turf. Therefore, Option C3 works between **mutually trusted realm shards** (two silo zones bordering each other) and as a **defensive clamp against seam shoving (A10)** when bordering an untrusted neighbour, but cannot make raw PvP honest between two hostile player hosts without Option C1 or C2.

#### Summary comparison across the options for contested places

| Option | Honest between two untrusted player hosts? | Latency / feel | Hidden state safe (A4)? | Scales to 100-player melee? | Attacks closed |
|---|---|---|---|---|---|
| **C1: Co-location on neutral silo** | Yes | Normal client-server RTT to silo | Yes (the planned `WorldRecipientView` on silo) | Yes (up to single-authority budget) | A3, A4, A5, A6, A8, A10 |
| **C2: Mutual deterministic sim + hash check + lazy referee** | Yes (with witness/referee for stalls/desyncs) | Best for 1v1 / small group (GGPO rollback) | Only via commit-reveal (both peers run the sim) | No (lockstep stalls on slowest of $N$ peers) | A3, A5, A6 |
| **C3: Taped/clamped seam contact + transactional proposals** | Only if both authorities honour rules on their own bodies (silo-to-silo); defends borders against A10 | 1 RTT for cross-seam effects; 0 RTT for local motion | Yes (each authority keeps its own arena) | Bounded by `MaxEntitiesPerBand` at the border | A10, plus fixes `WorldAdjacencyContactField` float replay gap |

The evidence favours a **hybrid composition (C3 + C1/C2)**:
- Enforce **C3's per-tick impulse clamp and taped fixed-point poses** at every `WorldAdjacencyContactField` immediately so no neighbour can shove a body into a border (closing A10) and so seam contact is replay-deterministic.
- Keep the architecture rule (`"do not run an authority boundary through a place designed to be contested"`, `docs/architecture/worlds.md:1413-1414` **[opened]**): large contested zones run on a single neutral silo authority (**C1**), while 1v1 / small-party duels between player hosts can either step through a portal into an ephemeral neutral referee world (**C1**) or run **C2**'s mutual deterministic rollback over signed `CommandSnapshot`s with lazy referee verification on hash mismatch.

---

## 4. How the debug world would prove it

Each leading option can be proved in `worlds/debug` with a small, concrete scenario and existing console verbs/counters:

1. **Frameless seam vs framed portal (Options S1 / F1):**
   - **Scenario:** In `worlds/debug/`, place an invisible threshold doorway (a creation with an aperture face and no frame rings) beside the existing framed gallery paintings (`worlds/debug/paintings.puck:105-153` **[opened]**) and a new adjacency to be authored. Citation check: the debug world has no `garden.puck` adjacency; `worlds/debug/debug.puck:56-68` is a run of `import` lines, `garden` is a global persisted destination (`worlds/debug/debug.puck:111-117`) reached through the gallery's painting portal, and no `.puck` file under `worlds/` authors an `adjacencies` row. This step and step 4 therefore first need an adjacency authored in the debug world.
   - **Check:** Walk Player 1 through the frameless threshold and through the `garden` adjacency. Inspect `world.adjacencies`, `world.portals` and `world.screenshot <path.png> crossing` (`docs/architecture/worlds.md:539-540`, `:1154-1160` **[opened]**) to verify that both cross cleanly with zero bezel or frame geometry and preserve heading/velocity across the isometry.
2. **Unobservability-gated layer transfer & hop cooldown (Option S1):**
   - **Scenario:** Author two layer instances of a debug room containing an interactable prop/node and a party-invite transfer trigger.
   - **Check:** Start an interaction/channel on the prop and trigger a layer transfer: verify the transfer refuses or defers until the interaction completes (no mid-cast vanish), and verify that triggering a second layer hop within the cooldown window is refused by name in the transfer outcome.
3. **Spatial grid AoI, hysteresis and unembodied observer policy (Option I1, closing A16):**
   - **Scenario:** Boot a debug place with `population.disclosure` set to `mode: Radius, radius: 15` and spawn 64 autonomous bodies moving across the radius boundary, plus one unembodied spectator connection and one embodied player.
   - **Check:** Read `world.budget` / per-tick disclosure work and verify that: (a) an unembodied connection without a camera/window grant receives zero body snapshots (closing A16); (b) a body oscillating by $0.1\text{ m}$ at $15.0\text{ m}$ does not flicker in and out of the observer's snapshot stream while inside the hysteresis band; and (c) redaction cost scales with nearby bodies rather than scanning all entities per sink.
4. **Clamped/taped seam contact (Option C3, closing A10):**
   - **Scenario:** Across a `debug` $\leftrightarrow$ `garden` adjacency authored for this check (none exists today; see step 1), inject a synthetic neighbour entity snapshot reporting a huge overlap or high-velocity collider stepping into the border against a stationary local body standing inside the overlap strip.
   - **Check:** Verify that (a) the local body's single-tick displacement is clamped by the maximum contact correction ceiling and never pushed across the ownership deadband into `garden`, and (b) `replay.record` followed by `replay.verify` reproduces the exact fixed-point seam contact tick-for-tick (`src/Puck.World.Server/WorldAdjacencyContactField.cs:25-29` **[opened]**).
5. **Mutual deterministic duel with state-hash check and lazy referee (Option C2, closing A6):**
   - **Scenario:** Two in-process or loopback player-host instances step a shared 2-body duel arena over the same taped `CommandSnapshot` sequence for 300 ticks, exchanging per-tick state hashes; in a fault-injection leg, one host alters a body's position or health by 1 LSB of `FixedQ4816` at tick 150.
   - **Check:** Verify that normal execution produces bit-identical state hashes on all 300 ticks, and that the fault-injected leg flags the exact tick (150) where the hashes diverge and identifies the mutating host when re-driven through `replay.verify`.

---

## 5. Open questions

1. **Exact per-tick displacement bound in `FixedDynamicBodyContacts.TryCorrection` (Lane E):**
   - `docs/plans/mmo-foundations/attacks.md:453-456` **[opened]** notes that `WorldAdjacencyContactField.cs:121-134` applies `correction` directly to `neighbourPosition` and marks whether a per-tick bound exists inside `src/Puck.Physics/FixedDynamicBodyContacts.cs:282` as **[unverified]**. Lane E should confirm whether `TryCorrection` is unbounded for arbitrary reported penetration depths.
2. **Minimum acceptable layer-hop cooldown and party-merge rules (Owner product call):**
   - When a player joins a friend's party on another population layer, should the transfer wait until the player leaves combat/gathering and enters a transition region, or happen in place after a visible countdown (e.g. 10 seconds out of combat) with an increasing cooldown on subsequent hops?
3. **Whether player-hosted worlds may ever host raw PvP without a neutral witness/referee (Owner call + Lane B):**
   - Option C2 proves simulation honesty between two peers via determinism and state hashes, but still requires a witness (a relay or silo) to attribute a disconnect/stall or arbitrate a hash mismatch when real stakes or durable progress are on the line (Lane B's custody/write-through scope).
4. **Measured CPU and bandwidth crossover for spatial-grid AoI in `WorldOutputHub` (Measurement):**
   - At what exact body count $N$ and subscriber count $M$ does a 2D spatial grid inside `WorldRecipientView` / `WorldOutputHub` beat the current contiguous `Span<EntitySnapshot>` scan (`src/Puck.World.Server/WorldOutputHub.cs:372-406` **[opened]**) on cache locality? A `puck bench` measurement over $N \in \{32, 128, 512\}$ and $M \in \{16, 64, 256\}$ settles this.

---

## 6. Sources

### Opened files in this worktree (`[opened]`)

- `AGENTS.md:1-456`
- `.claude/skills/delegate/SKILL.md:1-145`
- `docs/architecture/worlds.md:1-1503` (specifically `#the-idea-in-plain-terms` at `:15-167`, `#portal-composition` at `:466-487`, `#adjacency-and-crossing` at `:488-555`, `#references-destinations-and-sessions` at `:557-600`, `#joining-admission-and-observation` at `:745-935`, `#nested-views` at `:1080-1169`, `#transfer-determinism-and-replay` at `:1170-1343`, `#federated-transfer` at `:1344-1391`, `#scale-and-authority` at `:1392-1415`, and `#decisions-and-exclusions` at `:1449-1470`)
- `docs/plans/mmo-foundations.md:1-119`
- `docs/plans/mmo-foundations/attacks.md:1-661` (specifically A6 at `:323-372`, A10 at `:443-465`, A12 at `:487-501`, A13 at `:503-515`, A16 at `:536-542`, Section 3 at `:544-600`, and Question 4 at `:628-636`)
- `docs/plans/disclosure.md:1-922` (specifically guarantee at `:29-55`, `WorldRecipientView` at `:221-265`, `WorldDeliveryCursor` at `:417-485`, compiler guard at `:486-565`, game fixtures at `:584-600`, and watched items at `:601-621`)
- `docs/decisions/engine-design.md:257-290` (`#a-portal-is-a-plane-a-body-passes-through`)
- `src/Puck.World.Schema/WorldAdjacencies.cs:143-190`, `:643-700`
- `src/Puck.World.Schema/WorldAdmission.cs:140-168`
- `src/Puck.World.Schema/WorldDestinations.cs:1-194`
- `src/Puck.World.Schema/WorldObserverDisclosure.cs:1-40`
- `src/Puck.World.Schema/WorldPlacement.cs:300-355`
- `src/Puck.World.Schema/WorldPortals.cs:1-221`
- `src/Puck.World.Server/WorldAdjacencyContactField.cs:1-353`
- `src/Puck.World.Server/WorldAdjacencyFields.cs:450-600`
- `src/Puck.World.Server/WorldCrossAuthoritySettlement.cs:1-54`
- `src/Puck.World.Server/WorldInstanceHost.cs:1-1820`
- `src/Puck.World.Server/WorldInstanceHost.Transfers.cs:1-400`
- `src/Puck.World.Server/WorldOutputHub.cs:1-450`
- `src/Puck.World.Server/WorldSessionSink.cs:1-300`
- `src/Puck.World.Client/WorldAdjacencySceneEmitter.cs:1-300`
- `worlds/debug/debug.puck:1-200`
- `worlds/debug/garden.puck:1-150`
- `worlds/debug/paintings.puck:1-582`
- `worlds/debug/painting-kit.puck:1-38`

### Web sources summarized (`[summary]` — pages for a reader lane to open if exact quotes are needed)

1. **World of Warcraft sharding, CRZ, layering and phasing:**
   - Wowhead (quoting Blizzard Community Manager Kaivax's official blue post): *Blizzard Clarifies Layer Hopping Cooldowns and Mechanics in Classic* — `https://www.wowhead.com/classic/news/blizzard-clarifies-layer-hopping-cooldowns-and-mechanics-in-classic-295154` (citation check: opens an unrelated news post; replaced by the next three)
   - Kaivax, Blizzard forums: *New Increasing Cooldown for Changing Layers* — `https://us.forums.blizzard.com/en/wow/t/290703` (opened in the citation check)
   - Brian Birmingham's Classic AMA answers, reposted on the Blizzard forums: *Layering in Classic - Blizzard Responds on Reddit* — `https://us.forums.blizzard.com/en/wow/t/layering-in-classic-blizzard-responds-on-reddit/260714` (opened in the citation check)
   - Blizzard Watch: *WoW Classic layer-hopping abuse hotfix* — `https://blizzardwatch.com/2019/09/09/wow-classic-layer-hopping-abuse-hotfix/` (opened in the citation check)
   - Wowhead: *Layering in Classic WoW* — `https://www.wowhead.com/classic/news/layering-in-classic-wow-291729` (citation check: 404)
   - Wowhead (quoting Blizzard's Zarhym FAQ): *Cross-Realm Zones Coming to Beta* — `https://www.wowhead.com/news/cross-realm-zones-coming-to-beta-203382`
   - Warcraft Wiki (Wowpedia): *Sharding (term)* (`https://warcraft.wiki.gg/wiki/Sharding_(term)`), *Layering* (`https://warcraft.wiki.gg/wiki/Layering`), *Phasing* (`https://warcraft.wiki.gg/wiki/Phasing`), and *Cross-realm zones* (`https://warcraft.wiki.gg/wiki/Cross-realm_zones`)
2. **Guild Wars 2, Elder Scrolls Online, and Final Fantasy XIV:**
   - Guild Wars 2 Official Wiki (summarizing ArenaNet's *Introducing the Megaserver System* blog post): *Megaserver system* — `https://wiki.guildwars2.com/wiki/Megaserver_system` (citation check: 404; the page is now `https://wiki.guildwars2.com/wiki/Megaserver`, and the closing prompt is on `https://wiki.guildwars2.com/wiki/Volunteer%27s_Blessing`, both opened)
   - Unofficial Elder Scrolls Pages (UESP): *Online:Megaserver* (`https://en.uesp.net/wiki/Online:Megaserver`), *Online:Phasing* (`https://en.uesp.net/wiki/Online:Phasing`), and *Online:Campaigns* (`https://en.uesp.net/wiki/Online:Campaigns`) (citation check: UESP refused fetching; the ESO claims stay unconfirmed)
   - Final Fantasy XIV Official Play Guide (Square Enix Lodestone): *World Visit System* (`https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/worldvisit/`) and *Data Center Travel System* (`https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/datacentertravel/`)
3. **Star Citizen, SpatialOS, EVE Online, Dual Universe, and New World:**
   - Cloud Imperium Games (RSI Comm-Link): *Server Meshing and Persistent Streaming Q&A* — `https://robertsspaceindustries.com/comm-link/transmission/18397-Server-Meshing-And-Persistent-Streaming-Q-A`
   - GamesIndustry.biz (quoting Bossa Studios' *Worlds Adrift* closure postmortem): *Worlds Adrift shutting down in July* — `https://www.gamesindustry.biz/articles/2019-05-29-worlds-adrift-shutting-down-in-july` (citation check: refused fetching)
   - Game Developer: Bossa's closure statement — `https://gamedeveloper.com/business/bossa-shutting-down-i-worlds-adrift-i-says-game-is-no-longer-commercially-viable-` (opened in the citation check)
   - PC Games Insider: *Bossa doesn't blame Improbable for Worlds Adrift's cancellation* — `https://www.pcgamesinsider.biz/news/69671/bossa-doesnt-blame-improbable-for-worlds-adrifts-cancellation/` (opened in the citation check)
   - GamesIndustry.biz: *Automaton Games enters administration* — `https://www.gamesindustry.biz/articles/2019-07-30-automaton-games-enters-administration` (citation check: refused fetching)
   - CCP Games (CCP Veritas, EVE Online dev blog): *Introducing Time Dilation (TiDi)* — `https://www.eveonline.com/news/view/introducing-time-dilation-tidi` (opened in the citation check; the page names no Brian Knox)
   - CCP Games (EVE Online dev blog): *The Bloodbath of B-R5RB* — `https://www.eveonline.com/news/view/the-bloodbath-of-b-r5rb`
   - PC Gamer (covering the M2-XFE jump-tunnel catastrophe): *EVE Online's biggest ever battle ended in disaster* — `https://www.pcgamer.com/eve-onlines-biggest-ever-battle-ended-in-disaster/` (citation check: 404)
   - CCP Games: *The Second Timer in M2-XFE* — `https://www.eveonline.com/news/view/the-second-timer-in-m2-xfe` and *The Massacre at M2-XFE* — `https://www.eveonline.com/news/view/the-massacre-of-m2-xfe` (opened in the citation check)
   - PC Gamer (covering Amazon Games' *New World* transfer/trade gold dupes): *New World disables all forms of wealth transfer again due to new gold dupe exploit* — `https://www.pcgamer.com/new-world-disables-all-forms-of-wealth-transfer-again-due-to-new-gold-dupe-exploit/` (citation check: 404)
   - NME: *New World disables all forms of wealth transfers to combat gold exploit* — `https://www.nme.com/news/new-world-disables-all-forms-of-wealth-transfers-to-combat-gold-exploit-3085160` (opened in the citation check)
4. **Interest management and replication graphs:**
   - Epic Games Unreal Engine Documentation: *Replication Graph in Unreal Engine* (`https://dev.epicgames.com/documentation/en-us/unreal-engine/replication-graph-in-unreal-engine`) and *Actor Priority in Unreal Engine* (`https://dev.epicgames.com/documentation/en-us/unreal-engine/actor-priority-in-unreal-engine`)
   - Riot Games Technology Blog: *Demolishing Wallhacks with VALORANT's Fog of War* — `https://www.riotgames.com/en/news/demolishing-wallhacks-valorants-fog-war` (the old `technology.riotgames.com` URL redirects here; opened in the citation check)
5. **Deterministic lockstep and rollback netcode (GGPO):**
   - Paul Bettner (GDC / Game Developer): *1500 Archers on a 28.8: Network Programming in Age of Empires and Beyond* — `https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond` (opened in the citation check)
   - *GGPO Rollback Networking SDK* — `https://github.com/pond3r/ggpo` and `https://www.ggpo.net/` (opened in the citation check; neither names its author)
   - Michael Stallone (NetherRealm Games, GDC 2018): *8 Frames in 16ms: Rollback Networking in Mortal Kombat and Injustice 2* — `https://gdcvault.com/play/1025471/8-Frames-in-16ms-Rollback` (citation check: the URL first given, `/play/1024001/`, is a different talk)
   - Infil: *Explaining how fighting games use delay-based and rollback netcode* — `https://words.infil.net/w02-netcode.html`, with parts 4 and 5 at `https://words.infil.net/w02-netcode-p4.html` and `https://words.infil.net/w02-netcode-p5.html` (opened in the citation check)

## 7. Citation check

A review lane from another family checked this page against its sources and the tree. Fetch dates and the inspected revision are in the commit that added this section. Corrections are made in place beside each claim, marked "Citation check".

- **Puck code and document claims:** 30 checked. 28 confirmed at the cited lines (quotes match the files). 2 corrected: the debug world has no `garden` adjacency (section 4; `worlds/debug/debug.puck:56-68` is imports, and `garden` is a portal destination at `worlds/debug/debug.puck:111-117`), and `WorldRecipientView` and `WorldDeliveryCursor` were written as present mechanisms in Options I1 and C1 and the comparison table although neither is in `src/` yet. None refuted.
- **Web claims:** 27 checked, chosen as every claim the section 3 options or the summary's findings rest on that names a source. 5 confirmed as written (party invites move players to the leader's shard or layer, sharding's patch, GW2 placement heuristics, Unreal actor priority, GGPO's prediction and rollback). 16 corrected to what the opened page says, including five wrong or dead URLs (the Wowhead layer-hop article, GW2 `Megaserver_system`, both PC Gamer articles, and the GDC Vault talk ID), the WoW layer-hop mitigation's source and content, Infil's authorship and the cause of one-sided rollback, and the TiDi and M2-XFE details. 2 refuted: layer-hopping out of PvP combat (Blizzard's design added a longer delay after PvP combat) and worker-boundary behaviour as Worlds Adrift's cause (Bossa declined to blame SpatialOS). 4 stay unconfirmed and keep **[summary]** with the reason (FFXIV numbered instances and hunt timers, Dual Universe's shutdown cause, the Star Citizen comm-link, and the uncited intentional-desync exploit). No web claim on this page was labelled **[opened]**, so none was relabelled.
- **Options affected:** S1 is strengthened (Blizzard's own design gated layer transfers on combat, loot and PvP). S3's external evidence fell away; its rejection now rests on Puck's architecture rules and EVE's M2-XFE. C2's mechanics are confirmed but the threats its witness and referee steps answer are uncited. I1 and C1 depend on the disclosure plan's `WorldRecipientView` landing. Debug-world proofs 1 and 4 need an adjacency authored first.
