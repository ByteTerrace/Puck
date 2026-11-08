# MMO persistence and live operations

Lane A of [the MMO foundations round](../mmo-foundations.md). It reads how
World of Warcraft, Final Fantasy XIV, EVE Online, Guild Wars 2 and RuneScape
(Old School and RS3), with Path of Exile and Diablo III where their sources were
better, store characters, migrate them across patches, roll back after crashes
and exploits, and move characters between realms. It decides nothing. It owns
round question 2 (player state across author edits) and its link to
[durable images](../hub-and-saves.md#durable-images), the attack catalogue's
question 8 ([what a visited world gets to write](attacks.md#4-questions-for-the-research-lanes)),
and the round's first hypothesis.

Every claim carries a label. **[opened]** means this lane opened the page or file
and the quoted words are pasted from what it showed; a few pages were read
through a fetch tool that returns extracts, so a quote is exact for the words
inside the quotation marks and no more. **[summary]** means the claim comes from a
search result summary or a secondary account this lane did not open.
**[inference]** is this lane's reasoning. A Puck fact cites the file at this
tree, by symbol and by `file:line` as a lead; lane E owns the code reading, and a
Puck premise outside what is cited here is marked unverified.

## Summary

The five games agree more than they differ. Tuning lives in id-keyed templates
(EVE's `typeID` against a per-item `itemID`; WoW's item id plus a per-instance
bonus list), and a character's record holds references to those templates plus a
few per-instance values, so a patch retunes by editing templates and the stored
rows survive. The failures of that design are failures of reference stability: a
bad merge of Old School RuneScape's item definitions gave every player "the wrong
stuff" and forced a whole-game rollback, and an EVE cleanup script that reused
item ids lost up to 114,000 items. A deliberate continuity break is announced
well ahead and is almost always a conversion with refunds (EVE's removed learning
skills paid back skill points and paid for unused books; WoW's level squish
shipped a conversion table; FFXIV's value downscaling let weapon holders
reallocate), never a silent wipe; the true clean starts (Path of Exile's leagues,
Old School's launch) are separate, opt-in realms. Home anchors what an economy or
a realm owns: FFXIV's world visit and data-centre travel forbid market listings,
retainers, free companies, housing and mail away from home, and a WoW transfer is
refused while auctions or mail are open. Restoring is per account or per item from
a snapshot plus an independent log (Guild Wars 2 replayed a backup and the queued
messages; RS3 rebuilt locked-out accounts from February saves plus telemetry
captured at logout; EVE reimburses only what its server logs show; Diablo III
banned or rolled back each account by its activity). Whole-realm rollbacks are
rare, short and costly to players who did nothing wrong, and when two stores roll
back separately they duplicate items (Guild Wars 2's 2020 rollback, by press
report). The round's first hypothesis survives with three refinements: state
holds references plus per-instance values under ids that are never reused; the
deliberate break is an authored conversion with refunds, not only an epoch; and
per-row restore needs a log that someone other than the restorer's adversary
holds, which is exactly what a Puck player host lacks (A3). Puck today copies the
identity template into each character at seeding and quarantines a whole
identity document it can no longer read, which is the opposite of the practice
on both counts.

## 1. Storing characters

### Templates and instances

**EVE Online: a type id for tuning, an item id for the thing.** [opened] CCP's
API blog on the Tyrannis 1.2 change: "Most itemIDs are now 64-bit integers
instead of 32-bit" and "All item typeIDs are now 32-bit integers instead of
16-bit", and for the Static Data Export the change "only affects column
referencing dbo.invTypes.typeID". The reason given: "For years now we've been
teetering on the brink of running out of unique IDs." The asset flag widened too:
"The flagID of assets will similarly change from 8-bit tinyint to 16-bit
smallint."
(<https://www.eveonline.com/article/the-api-dev-blog-trilogy-volume-two-the-types-they-are-a-changing-tyrannis-1.2>)
[opened] A client generated from the ESI specification lists a character asset
as `item_id`, `type_id`, `location_id`, `location_flag`, `location_type`,
`quantity`, `is_singleton` and an optional `is_blueprint_copy`
(<https://github.com/evecommander/esi-client-php/blob/master/docs/Model/GetCharactersCharacterIdAssets200Ok.md>;
a generated model, not CCP's page). [inference] The record is a reference to a
template (`type_id`), a unique instance id, a location in a container tree, and a
count or singleton flag; tuning (the type's attributes) is published separately as
static data and changes under the instances.

**World of Warcraft: item id plus a bonus list.** [opened] A developer on
Blizzard's API forum: "it is currently impossible to determine the meaning of
bonus IDs for items" "(this affect both AH and character equipment endpoints)"
(<https://us.forums.blizzard.com/en/blizzard/t/our-vision-is-to-help-you-achieve-yours/10791/7>).
[summary] Community API documentation says the character equipment endpoint
reports each item with its own bonus list and an `item_id` that keys the item
endpoint, which reports default stats only. [inference] WoW's instance is a
template id plus a list of modifier ids, each a further template; the stats a
player sees are derived from both, not stored. That is why a squish can be done
by changing tables.

**FFXIV: a character record that travels, sized by what it carries.** [opened] A
press report of a 2026 Fan Festival panel: "Yoshida-san mentioned that adding
more would increase the load of data characters bring with them", about more
glamour plates and same-region matching
(<https://simulationdaily.com/news/final-fantasy-xiv-butt-slider/>; secondary).
[inference] The size of the record that moves between data centres is a design
constraint in a game that moves characters, which a Puck identity crossing
between hosts shares.

**Old School RuneScape: code and content separate from saves.** [opened] The
game is "a backup of the RuneScape source code" as it stood in 2007, and players begin "Old School RuneScape on Tutorial Island with a new
character" (<https://oldschool.runescape.wiki/w/Old_School_RuneScape>; community
wiki). [summary] Search summaries quote Jagex that the backup was of game code and
held no user data. [opened] Rollbacks are described as reverting "players' saved
games" (the 2018 notice, below). [inference] A Jagex save is a per-player document
loaded by whichever world the player enters; the world holds no character.

### What belongs to the character, the account and the realm

**WoW moves state from character to account over time.** [opened] Blizzard's
Warbands preview: "Any non-soulbound item can go into the Warband Bank";
"we opted to make it easier to transfer currency between characters in your
Warband instead"; "any character can collect any item appearance, regardless of
if they can equip the item or not"; and reputation merged by the best value,
"reputation progress will display as the furthest that any character in your
account has achieved." Exceptions stay per character: "Class-specific
appearances are an exception and will still be collectible only by that specific
class", and prestige achievements "whose title and progress will remain
character-specific." Warbands are one region, across realms and factions
(<https://worldofwarcraft.blizzard.com/news/24061008/>).

**GW2 converts item instances into account unlocks.** [opened] ArenaNet's
Legendary Armory post: account-bound legendary equipment "will be automatically
converted to your Legendary Armory"; "Unbound legendaries will remain in your
inventory"; owners of bound Sunrise or Twilight find "the greatswords will be
added to your Legendary Armory" and "you’ll receive tokens to use in crafting
Eternity", where "an Eternity produced using tokens will be account-bound". About
0.008% of players owned more copies than the armory holds, and ArenaNet would
"reach out to those players in the days following the release"
(<https://www.guildwars2.com/en/news/the-legendary-armory-arrives-july-13/>).

**FFXIV anchors realm-owned things to the home world.** [opened] A Home World
Transfer carries the friend list, blacklists, items, retainers and their items,
letters, gil up to the cap and mahjong rank, and is refused when "Your character
cannot have items for sale on the market", when the character "is currently away
from their Home World or has logged out on another World", for "characters
enlisted in a free company", and for housing: "Ownership must be relinquished
before a Home World Transfer will be possible." Linkshells drop:
"Transferred characters will be removed from linkshells automatically."
(<https://na.finalfantasyxiv.com/lodestone/playguide/option_service/world_transfer_service/>)

**[inference] The pattern.** Three scopes recur. The character holds its
progress, its carried items and references to templates. The account holds
collections and unlocks, merged monotonically (best value, union). The realm or
world holds what is shared with other players there: market listings, housing,
guilds, a realm's event state. Every game moves state from character to account
as it ages (Warbands, the armory), and none moves realm state onto the character.

### What failed

- **Reference corruption.** [opened] Old School's 4 June 2020 update: "due to a
  problem with players' save games we're turning the game off temporarily"; "We're
  going to rollback the game which will mean it remains offline for a few hours";
  Mod Ash: "the update corrupted some item definitions, meaning players had the
  wrong stuff after the update." The wiki attributes it to "a bad code merge
  resulting in incorrect item definitions"
  (<https://oldschool.runescape.wiki/w/Sins_of_the_Father_glitch>; community wiki
  quoting Jagex). [inference] When state holds references, a template edit that
  changes what an id means changes every holder at once; the template table is as
  load-bearing as the saves.
- **Id reuse.** [opened] EVE's May 2010 outage: deleted items "are moved to a
  specific location in the database that we refer to as the junkyard", cleanup
  scripts return ids to the pool, and a flawed script "went unnoticed on
  Singularity because the error had only a miniscule chance of occurring there";
  "up to 0.0071% of all itemIDs were affected, which is up to 114,000 itemIDs"
  (<https://www.eveonline.com/news/view/the-day-the-items-disappeared>). The
  incident notice: "The items in question are lost, not having been assigned an
  itemID during the normal process"
  (<https://www.eveonline.com/news/view/extended-downtime-friday-07-may-due-to-database-problems-some-ingame-items-affected>).
- **Id exhaustion.** [opened] The 64-bit change above was forced by running out
  of ids.

## 2. Migrating characters across patches

### Removing a thing players own

**EVE's learning skills (2010).** [opened] "All skillpoints invested in learning
skills will be reimbursed, including all the fiddly corner cases." "All
skillbooks not currently injected into people's heads will be reimbursed at the
old NPC sell price", paid to "whichever character or corporation owns the
container that the skillbooks are in." "Contracts containing learning skills will
have those skillbooks substituted for copies of the Pax Amarria." The change
involves "cancelling any and all market orders containing these skills." Every
character gains "an extra 12 base points (ie, non-remappable) in each attribute",
and "The 100% training speed bonus up to 1.6m SP will no longer be available."
Injected books were not refunded
(<https://www.eveonline.com/news/view/learning-skills-are-going-away-1>).
[summary] Search summaries report that a later destroyer and battlecruiser skill
split did not refund in the same way, to player complaint.

**WoW's tier bonuses.** [opened] The Dragonflight pre-expansion notes: "Eternity’s
End class set bonuses are now legacy and disabled in combat", and several talents
"removed" ("Torment the Weak has been removed.")
(<https://worldofwarcraft.blizzard.com/news/23814291/>). [inference] The item
stays in the player's bags and its template's effect is switched off: the
instance is kept, the tuning changes.

**GW2's armory** (above): instances converted on login, over-cap holders handled
one by one, a token refund for a recipe that changed.

### Squishes

**WoW, Shadowlands pre-patch.** [opened] "Characters at level 120 will become
level 50 after the content update", "With the pre-expansion patch, levels,
items, and stats will be adjusted", with a published level table, and boosts
remapped: "Level 110 Character Boosts will boost characters to level 48 after the
pre-expansion patch"
(<https://news.blizzard.com/en-us/article/23523206/explore-the-updates-arriving-with-the-shadowlands-pre-expansion-patch>).

**FFXIV, patch 6.0.** [opened] Community wiki: "Values were downscaled starting
at level 51, although the downscaling was not linear", to "approximately 20% of
their original values" at 80; "Holders of Anima Weapons were granted an
opportunity to reallocate their secondary attributes"; "This also had the effect
of resetting all EXP gained since earning a level to zero." The wiki cites
Yoshida's reasons as overflow errors, enmity caps, long damage text and very high
enemy health (<https://ffxiv.consolegameswiki.com/wiki/Value_downscaling>).
[inference] The squish was a template change everywhere except one stored value
(EXP into the current level), which the conversion zeroed: a real cost to every
player, paid because that one value was stored rather than derived.

### Clean starts and merges

**FFXIV 1.0 to A Realm Reborn.** [summary] A 2013 notice, reposted by a fan
forum this lane could not open (403), said all 1.0 character data would move to
the relaunch whatever the subscription state, saved up to a cutoff at the final
1.0 maintenance. [opened] Community wiki: "Items, job progression, and mounts
(such as the exclusive Goobbue) did persist on characters that were imported
over", and legacy players received a permanent subscription discount, a mount and
a mark (<https://consolegameswiki.com/wiki/Legacy>). [inference] Square Enix
replaced the game, the world and the engine, and still carried the characters.

**Path of Exile leagues.** [opened] A player guide that quotes GGG staff: "All
your characters are moved from the challenge league to the parent Standard
league"; "The contents of your stash in the challenge league get transferred to
your stash in the parent Standard league", each non-empty tab becoming "a new
remove-only stash tab"; in a later restart the hideout with more decorations wins
and the other is "lost, but all the decorations from it are returned to your
hideout stash"; masters take the higher level; completions merge
(<https://pathofexile.com/forum/view-thread/2091028>; a player post, not staff).
[inference] A league is an authored epoch: a realm that starts empty and ends by
merging into a permanent one under fixed rules (move, max, union, containerize).

**Old School.** [opened] A fresh start, but as a new game beside the old one.

**Destiny 2's content vault.** [summary] Bungie removed destinations yearly and
said the vault "does not apply to any weapons and armor in a player's inventory
or vault"; it later stopped vaulting expansions. Not opened (bungie.net returned
403 for the help pages this lane tried).

### What failed

- [opened] FFXIV's squish zeroed partial EXP, a loss the conversion chose.
- [opened] EVE's injected learning books were not refunded; CCP said players had
  had their value.
- [opened] GW2's armory could not convert over-cap holdings mechanically and fell
  back to contacting players.
- [opened] Path of Exile's merge loses the losing hideout's layout while it
  returns the decorations, which the guide's own "Nothing you own in a challenge
  league is ever lost by a league merge" does not reconcile.
- [inference] Every conversion above is a function from the old row to the new
  row with a residue (a refund, a placeholder item, a remove-only tab, a person
  who contacts you). None is "keep what fits, drop the rest silently".

## 3. Rolling back after crashes, dupes and exploits

### Whole-realm and whole-game rollbacks

**Old School RuneScape, July 2018.** [opened] Jagex: "the logic used for this
calculation was incorrect", converting stacks to 2.1b coins; the team chose to
"roll back players' saved games to the approximate time of the game update
(11.30am BST)"; they "were not able to restrict the effects solely to players
who'd generated money from the bug" because "coins are tradeable and the effects
were far more wide-reaching"; players "would have lost around 20 minutes of their
activity", with an apology to anyone who "received a rare loot-drop or something
similar during that time"
(<https://oldschool.runescape.wiki/w/Update:Old_School_RuneScape_Status_Update>;
a wiki copy of Jagex's post). [opened] The wiki's rollback list has ten entries
from February 2018 to October 2024, mostly reaching back to an update's start or
about 30 minutes; rollbacks "are used under very strict and rare circumstances",
and players worry that "items may be duplicated if a player is able to predict a
rollback" (<https://oldschool.runescape.wiki/w/Rollback>).

**Guild Wars 2, May 2020 (EU).** [opened] ArenaNet's own analysis: "we have two
servers, a primary and a secondary"; the replication queue grew faster than it
drained; early on the Monday "the “bad things” threshold was crossed, and the
databases swapped primary and mirror", so "all their progress since
Friday evening was wiped away." The recovery: they "found the most recent
automatic backup file and logs containing all the queued messages, up to 2:41
a.m.", loaded the backup and replayed the logged messages. "The root cause was the
drivers." (<https://guildwars2.com/en/news/inside-arenanet-live-game-outage-analysis>)
[opened] A press report: the guild bank and trading post "über andere Server
laufen" (run on other servers), and "So wurden viele Items verdoppelt" (many items
were duplicated); compensation was a campfire for everyone active in 30 days
(<https://mein-mmo.de/en/guild-wars-2-offers-compensation-for-rollback-and-is-being-criticized,504743>;
secondary, quoting players, not ArenaNet). The duplication is press-reported and
unverified against an ArenaNet statement.

**EVE, May 2010.** [opened] "We quickly decided that a rollback was not a
feasible option"; the team chose to "repair the damage, bring Tranquility back up
and resolve incoming petitions as quickly as possible"
(<https://www.eveonline.com/news/view/the-day-the-items-disappeared>).
[inference] A single shard with player markets has no small rollback: every
trade in the window touches someone who did nothing wrong.

**WoW.** [summary] This lane found no Blizzard post announcing a realm-wide
rollback for an exploit; searches turned up hotfixes, restarts and bans instead.
That is an absence of evidence, not evidence of absence.

### Per-account and per-item restore

**Diablo III, 2012.** [opened] Blizzard, via a trade press report: it "took the
Auction Houses in the Americas offline and suspended gold trading in order to
isolate the problem", reopened after an audit of the window's transactions, had
"already recaptured more than 85 per cent of the excess gold", "locked accounts
that appeared to be exploiting the bug as well as collaborators", and "either
banned or rolled back the account depending on their activity"
(<https://www.mcvuk.com/diablo-iii-auction-house-fixed-illicit-profits-heading-to-charity/>;
secondary).

**RS3, the 2021 login lockout.** [opened] Jagex: character progress before a
February save point "is intact"; "Our telemetry system separately captures all
game and account session information when you logout" and it "was not impacted
and is fully intact"; "more than 99% of RuneScape accounts remain unaffected",
with "some data gaps for that 1%" between the save point and the outage, about
four weeks; the fix merges the February account saves with telemetry
(<https://secure.runescape.com/m=news/server-login-issues---a-further-update-from-mod-warden>).
[opened] The wiki records the cost: "As many as 1% of RuneScape players were unable
to login for at least 4 weeks"; banks came back "in no particular order"; and it
lists items possibly duplicated or lost and invention perks reverted
(<https://runescape.wiki/w/Login_lockout>).

**WoW item restoration.** [opened] Community wiki: the service restores "any
items that have been vendored, disenchanted, obliterated, scrapped, or
destroyed", and "This service can only be used to restore up to fifty items once
every seven days for each character"; it launched in 2012 with "an initial 30 day
cooldown" (<https://warcraft.wiki.gg/wiki/Item_Restoration>). [summary] Blizzard's
own article (login-gated to this lane) is summarized as saying items the service
cannot restore are permanently lost and support will not restore items by hand.

**WoW Classic Hardcore.** [opened] After DDoS attacks, "we're taking steps to
resurrect player characters that were lost as a result of these attacks"; in
future "Blizzard may elect - at our sole discretion - to revive Hardcore
characters"; and "we do not intend to revive characters which have died due to
server disconnects, lag spikes, gameplay bugs"
(<https://www.pcgamesn.com/world-of-warcraft/wow-classic-hardcore-characters-revive>;
secondary quoting a Blizzard community manager).

**EVE reimbursement.** [opened] The 2011 policy: reimbursement only when "a game
master (GM) is able to verify" the cause from server logs; "we unfortunately
cannot accept player statements, chat logs or screenshots as definitive
evidence"; "Reimbursement will only be granted if a loss is attributable to a bug
or server error"; "All reimbursement requests must be sent within a week (7 days)
from when the loss occurred"; a node drop during a trade can be reimbursed
"following an investigation by a GM"; "Any losses of any kind resulting from a
large-scale player engagement are not covered"
(<https://www.eveonline.com/news/view/reimbursement-policy-updated>).

**FFXIV housing lottery, 2022.** [opened] Yoshida: "we have no intention of
performing a data rollback to forcibly rescind any refunded gil", because "these
housing lottery issues have been significant and caused great stress and
frustration in many players"; instead "In patch 6.2 we plan to implement an NPC by
which you may voluntarily return the deposit you were accidentally refunded"
(<https://eu.finalfantasyxiv.com/lodestone/topics/detail/8a8712688d43aaac9280998dddc710abdbfc928a>).

### Freezing the doors that move value

[opened] Diablo III took its auction houses offline and suspended gold trading
until an audit (above). [opened] Amazon's New World, 2021: "We've identified an
exploit and will be banning individuals who were using it"; "We will have trades
off for tonight while we work through and test a fix for the problem"; and the
press found that with transfers disabled "starting a town upgrade can lead to the
cost being added to your company wallet rather than removed"
(<https://www.pcgamesn.com/new-world/gold-exploit>; secondary). [opened] EVE
closed Tranquility once "the logs started filling up with errors and exceptions"
(the 2010 post).

### What a rollback costs the innocent, and how far back

- [opened] Old School: about 20 minutes and any rare drop in them; Jagex's
  post apologized and offered no compensation.
- [opened] Guild Wars 2: a weekend, from Friday evening to 2:41 Monday.
- [opened] RS3: four weeks locked out for about 1% of accounts, then a restored
  account with a reshuffled bank.
- [opened] EVE: loss without a rollback, repaid by petition where logs show it,
  within seven days.
- [inference] How far back a service can restore is set by its independent
  record, not by its backups: Guild Wars 2 reached the failover instant because
  it held the queued messages; RS3 reached logout because telemetry captured
  sessions separately; EVE reaches what server logs show; WoW item restoration
  reaches what destruction logs keep. The backup alone reaches only its own
  timestamp.

## 4. Character transfer between realms

**WoW paid transfer.** [opened] Community wiki (Blizzard's own article was
login-gated): "Active auctions or mail will prevent a transfer"; "Characters
holding WoW Tokens cannot transfer"; "Characters with caged pets in their
inventory cannot transfer"; gold caps by level; "A character cannot be
transferred to a realm where the character list is already full"; "Raid lockouts
transfer with your character"; "Mythic raids started prior to the transfer cannot
be continued after the transfer"; "Item Restoration cannot restore items deleted,
vendored, or disenchanted prior to the transfer"; "Characters are ineligible for
realm-first achievements for the first 60 days after transferring"; "Logging in
before the transfer is complete may cause the transfer to fail and result in the
character being locked"; and cooldowns cut from six months to three days over the
years (<https://warcraft.wiki.gg/wiki/Character_transfer>). [summary] Blizzard's
current article, as summarized: transfers between Battle.net accounts are not
possible, nor between retail and the Classic flavours, and "Customer Support
cannot bypass any of the below restrictions."

**FFXIV: visit, travel and transfer are three things.** [opened] World visit
moves play within a logical data centre "while retaining residence on your Home
World"; away from home "Items cannot be sold on the market, but purchasing is
available", "Retainers cannot be summoned, hired, or released from service",
"Estates and apartments cannot be purchased", "Free companies cannot be created,
joined, or left", "You must return to your Home World to receive moogle letters",
and after logout "you will automatically be returned to your Home World if you do
not log in again within 30 days"
(<https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/worldvisit/>).
[opened] Data-centre travel adds "Company credits cannot be earned", "Deep Dungeon
results will not be saved to leaderboards", "Belts cannot be retrieved from the
Calamity salvager", "You cannot apply for a Home World Transfer while visiting
another World", "You cannot delete your character when it is visiting another
World", and "You cannot cancel this process while it is underway"
(<https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/datacentertravel/>).
[opened] A Home World Transfer is refused while away and has the conditions in
section 1.

**GW2: home world and guesting.** [opened] "Guesting allows you to play on a
world other than your home world"; "You may not participate in World vs. World on
your guest world"; "If you choose to enter World vs. World on the world you are
guesting on, you’ll be moved back to your home world"; "Transferring to a new home
world will now cost a gem fee"; "Each account is still limited to one world
transfer every 7 days" (<https://guildwars2.com/en/news/guesting-is-coming>).
[summary] ArenaNet later replaced worlds as World vs. World teams with guild-based
teams reassembled each season.

**EVE.** [inference] One shard, so no realm transfer; the analogous checks sit
at the market and contract doors (the learning-skill change cancelled orders and
substituted contract contents).

**[inference] What is checked, across all four.** A transfer or a visit is
refused while the character has an open obligation to the realm it leaves (an
auction, a listing, mail, a guild or house, a petition, a pending rename), while
it is mid-flight (logged in, visiting, travelling), or when the move would carry
value past a cap. What moves is what the character holds; what stays is what the
realm holds on the character's behalf; instance progress that belongs to a
realm's server (a Mythic raid in progress) does not continue elsewhere, while the
character's lockout record does move. A visit never lets the visited realm touch
the home realm's economy.

## Options for Puck

### Puck as it stands, for these questions

Lane E owns the code; these are the facts this lane read.

- **An identity copies its template.** `WorldOwnedWorlds.Seed` builds each new
  identity as `template with { ... }`, folding the template's world rows into the
  identity's own state (`src/Puck.World.Server/WorldOwnedWorlds.cs:691-704`)
  [opened]. A later edit of the template does not reach a character already
  seeded [inference: nothing re-derives a stored identity from its template on
  load; the load path reads the stored document, `WorldOwnedWorlds.cs:120-176`].
  The hub plan's H1 shrinks the template, it does not make the identity reference
  it ([gap 1](../hub-and-saves.md#1-a-minimal-identity-template)).
- **An unreadable identity is set aside whole.** The catalog narrates "a document
  shape this catalog no longer reads is disposed of, never migrated" and moves it
  into `unloadable/`, then re-seeds the freed name
  (`WorldOwnedWorlds.cs:570`, `QuarantineDirectoryName` at `:27`,
  `IsTerminalDocumentShape` at `:628`) [opened]. There is no row-by-row admission
  for an identity, as there is planned for a world image (tier 2 of
  [the restore ladder](../hub-and-saves.md#the-restore-ladder)).
- **Home adopts what a colocated visited world wrote.** `WorldIdentity.TryAdopt`
  sets every carried fact and every owned record field through the local write
  door (`src/Puck.World.Schema/WorldIdentity.Records.cs:121-152`) [opened]. The
  architecture page narrows this: "Remote round trips do not adopt: a remote
  incarnation claim is unauthenticated ... Remote home adoption remains deferred"
  (`docs/architecture/worlds.md:819-821`) [opened]. So A2 is live today for
  crossings within one process and deferred, not closed, across federation.
- **No authored reset epoch exists.** A search of `src/` for reset-epoch names
  finds only session epochs restored with grants
  (`WorldPersistence.RestoreCheckpointCore`'s `resetSessionEpochs`,
  `src/Puck.World.Server/WorldPersistence.Checkpoint.cs:800`) [opened].

### Round question 2: player state across author edits

The question has three parts: what survives an edit, where tuning lives, and how
an author breaks continuity on purpose.

**Option 2a. Copy-at-seed with whole-document set-aside (today).** The identity
is a copy of its template plus writes; an edit of the template does not reach it;
a shape the catalog cannot read is quarantined whole.
- Evidence against: no studied game copies tuning into the character. WoW's and
  FFXIV's squishes, EVE's attribute grant and WoW's disabled tier bonus all work
  because tuning is in templates the record references [opened, sections 1-2].
- Cost: none to build. Fails at the first retune: a character keeps the old
  values forever, or loses everything when its shape stops loading. Whole-document
  loss is worse than any studied game's worst conversion.
- Attacks: none closed. A1 and A2 are untouched.

**Option 2b. References and per-row admission.** The identity holds references
(template ids, record keys) and its own per-instance values; tuning is read from
the template at the world that uses it; a stored row the current template no
longer declares is dropped by name through the same admission the world image's
tier 2 uses.
- Evidence for: the template-id and instance-id split (EVE, WoW) [opened]; the
  durable-images memo already measured the arena's own door partitioning the
  debug world's 469 rows into 15 kept and 454 dropped by name
  ([what an image holds](../hub-and-saves.md#what-an-image-holds)) [opened].
- Cost: an identity becomes a document whose meaning depends on the template it
  is read against, so the template becomes load-bearing (Old School's item
  definition corruption is the failure: one bad template merge changed every
  player at once) [opened]. Ids must be stable and never reused (EVE's junkyard
  incident) [opened].
- Fails at: values whose meaning changes rather than disappears (a squish, a
  removed skill). Dropping is not converting.
- Attacks: none directly; it is the substrate the others need.

**Option 2c. 2b plus an authored conversion program.** A template edit that
changes what a stored value means ships an authored `.puck` conversion that runs
once per stored document at restore, keyed by the template version it was written
under: map old rows to new rows, refund what is removed (a currency fact, a
placeholder record such as EVE's Pax Amarria), and leave a residue row naming what
it did.
- Evidence for: every deliberate break studied is such a function (EVE's refund
  rules, WoW's conversion table, FFXIV's reallocation, GW2's tokens, Path of
  Exile's move, max and union) [opened]. "Puck code over engine code" and "authors
  rule" (AGENTS.md) put the conversion in the author's program, not engine C#.
- Cost: a version on every stored document, a conversion language surface (the
  state-and-language plan's territory), and a test that a conversion is total over
  the rows it claims. A conversion that is wrong is a whole-population defect, as
  the Old School merge was.
- Fails at: conversions needing judgement (GW2 contacted over-cap holders by hand)
  [opened]; Puck has no support desk, so the residue must be something a player
  or author can act on.
- Determinism: a conversion is a pure function of (stored document, template
  version, conversion program), so it replays bit-identically [inference].
- Attacks: none directly. It narrows A1's gain only if the conversion clamps
  values to template bounds on the way in [inference].

**Option 2d. Epoch realms.** The deliberate clean break is a new world (a league,
a season, a relaunch) beside the permanent one, with an authored merge back at its
end; the permanent world's characters are never reset.
- Evidence for: Path of Exile's leagues, Old School's separate launch, FFXIV
  carrying characters through a full relaunch [opened].
- Cost: the merge rules are a conversion program (2c) between two worlds rather
  than two versions.
- Puck fit: "characters as worlds" makes a league a world the identity enters and
  leaves through a portal; the merge is the write-back at the end [inference].

**The durable-images owner calls.**

- *Saved state over an edited source.* The memo carries a saved row over an edit
  that keeps the row's declaration, and the alternative is a clean start on any
  change ([owner calls](../hub-and-saves.md#owner-calls)). The evidence favors
  carrying: every studied game keeps state across patches by default, and a clean
  start is reserved for a new realm (2d) or a relaunch that still carries
  characters [opened]. The evidence also says the memo's carry is incomplete: when
  the meaning of a declared row changes (a re-tuned default that was a score's
  scale, not its starting value), the games convert rather than carry (FFXIV's
  EXP reset, WoW's level table) [opened]. A memo that carries unchanged
  declarations and drops changed ones has no third path for "changed and
  convertible"; 2c is that path. The author's `world.image.discard` matches no
  studied practice for a shared world, where a reset is announced to the players
  who lose the state; for a world only its author plays, it is fine [inference].
- *Custody an inexact image holds.* The memo starts the row from its durable rows
  and narrates lost custody, leaving recovery to recall; the alternative refuses
  activation. The evidence favors starting: no studied service keeps a realm down
  until every item is resolved; they reopen and resolve per item from logs
  (EVE's petitions, WoW's restoration, RS3's telemetry) [opened]. It also favors a
  middle course the memo does not list: reopen the world but freeze the doors that
  move the affected value until it is resolved, as Diablo III froze its auction
  house and New World froze transfers [opened]. New World also shows the cost: the
  freeze itself opened a new duplication path [opened], so a frozen door is
  another door to review. The set-aside `image.refused` matches EVE's junkyard and
  WoW's restoration window; the evidence says set-aside data is kept for a bounded
  window and resolved, not kept forever [opened, inference].

### Attack question 8: what a visited world gets to write

The games partition state into character, account and realm scopes (section 1),
and a visit never lets the visited realm write the home realm's economy (section
4). Puck has no operator to enforce that partition, so each option says who does.

**Option 8a. Adopt everything (today, colocated).** Fails A2 outright, and it is
the one partition no studied game uses [opened, section 4].

**Option 8b. Namespaced write-back.** A visited world writes only facts and
records in its own package namespace; home adoption refuses others by name. This
is H9 extended from `WorldOwnedWorlds.Decide` to the crossing adoption
([invariants, I4](../hub-and-saves.md#invariants)).
- Evidence: FFXIV's deep-dungeon leaderboard is not written while travelling, and
  company credits are not earned; a visited world's own records are its own
  [opened]. The closest analogue of a namespace is a realm's own leaderboard.
- Closes: A2's griefing of other namespaces and of the studio dungeon's namespace.
- Leaves open: Bob's world writing Bob's namespace with anything (laundering is
  now visible as Bob's claim, not hidden in the studio's), A1, A3.

**Option 8c. Away restrictions on value classes.** Value-bearing classes
(currency, tradeable records, stakes, housing-like holdings) are home-scoped: a
visited world can read them and cannot change them; it can only issue a claim or
a pending grant that home applies by its own rule. This is FFXIV's travel
restriction list and GW2's home-only World vs. World as engine policy [opened].
- Closes: A2 for value (a visitor's gold cannot be zeroed or inflated abroad).
- Leaves open: A1 (the home is the player's own host), progress classes.
- Cost: a classification of every fact and record pool, authored on the identity
  template (the standard package chooses; the engine supplies the mechanism).

**Option 8d. Signed write-back.** A visited world signs what it writes; home
stores the claim with its issuer; a third world reading the identity applies its
own policy to each issuer. This is the plain-terms page's worked example
([the idea in plain terms](../../architecture/worlds.md#the-idea-in-plain-terms)).
- Evidence: no studied game needs it, because one operator is the only issuer.
  The nearest practice is provenance in logs (EVE's server logs as the only
  accepted evidence) [opened].
- Closes: A1 for claims issued by others (the owner cannot forge a studio's
  signature), and A2's laundering as trust (a reader can discount Bob).
- Leaves open: A1 for anything only the player issues; A9 (a banned issuer's key
  stays valid until expiry).

**Option 8e. Issuer-kept records.** Value that must stay unique stays at its
issuer (a studio silo), and the identity holds a reference to it. This is every
studied game's model, where the operator's database is the issuer [opened].
- Closes: A1 and A2 for that class.
- Cost: the issuer is reachable at spend time; offline play cannot spend it.

**Option 8f. Write-through as earned.** Each award a visited world grants is
submitted home when it is earned (a pending claim home decides at its own tick
boundary), rather than riding the projection until return (attack question 2,
lane B's).
- Evidence: RS3's telemetry captured at logout and Guild Wars 2's queued messages
  are records held apart from the session, and they are what made restore
  possible [opened].
- Closes: A3's crash case for progress earned abroad.
- Leaves open: A3's dishonest case (the host can withhold), unless 8d makes the
  award a claim the visitor holds as soon as it is issued [inference].

**When the realm rolls back after the character left.** The evidence is that
services roll realm state and character state back independently and chose, per
class, which side wins.
- [opened] WoW's Anniversary realms: "Due to a bug, some Anniversary realms saw
  the AQ war effort advance prematurely to a completed state. We've returned that
  back to the intended state on all affected realms." The post does not say what
  happened to rewards characters earned in the window
  (<https://eu.forums.blizzard.com/en/wow/t/580805/1>). Realm state was corrected;
  character records were not touched.
- [opened] FFXIV kept the refunded gil on the characters and offered a voluntary
  return.
- [opened] Diablo III recaptured gold from the accounts that took it and banned or
  rolled back by account, leaving everyone else.
- [opened, secondary] Guild Wars 2's character data rolled back while the trading
  post and guild bank, on other servers, did not; items held in both were
  duplicated.
- [inference] The rule the practice implies: a character keeps what it earned in a
  realm that later rolls back, unless the earning was itself the exploit, and the
  removal is targeted. That matches Puck's I14 ("restoring a world's image ...
  never writes or rolls back a character") for progress. For anything that moved
  between the realm's state and the character (a deposit, a withdrawal, a stake),
  independent rollback duplicates or loses it, which is Guild Wars 2's failure.
  Such a move needs one record both sides settle against: the crossing log where
  it exists, or the silo's refusal to rewind past outstanding obligations, which
  the attack catalogue lists as unattackable on the silo
  ([unattackable](attacks.md#3-unattackable-by-construction)).
- Relation to A3: a player host that restarts is a realm rollback the realm's own
  operator chose. The games' answer (character keeps its gains) is available to
  Puck only for gains that already left the realm (8f, 8d); gains still on the
  travelling projection die with the host today.

### The round's first hypothesis

"Player state carries across an edit by default; content templates hold tuning
and state holds references; an authored reset epoch is the deliberate break;
dropped player data is set aside where it can be recovered; per-row restore is the
common path, since every engine build invalidates an exact continuation."

- *Carries by default.* **Confirmed.** Every studied game; the clean starts are
  new realms [opened].
- *Templates hold tuning, state holds references.* **Confirmed, refined.** State
  holds references plus per-instance values (WoW's bonus list, EVE's quantity,
  flag and singleton), under ids that are never reused and never change meaning
  [opened]. Puck's identity does the opposite today (copy-at-seed) [opened].
  Account-scope state merges monotonically (best value, union) [opened].
- *An authored reset epoch is the deliberate break.* **Refined.** The break is an
  authored conversion with refunds, announced ahead; a reset in the literal sense
  is a separate epoch realm with a merge back (2c, 2d) [opened].
- *Dropped data set aside where it can be recovered.* **Confirmed, refined.**
  EVE's junkyard, WoW's restoration window, Path of Exile's remove-only tabs and
  GW2's individual contact are all set-asides; each is bounded (seven days, fifty
  items, until the tab is empty) and each resolves to a refund, a placeholder or a
  person [opened]. Puck's identity quarantine sets aside the whole character, not
  the dropped rows [opened].
- *Per-row restore is the common path.* **Confirmed, refined.** Restore is per
  account or per item; whole-realm rollbacks are reserved for contamination of
  tradeable value ("coins are tradeable") and are short [opened]. Per-row restore
  works only from an independent record the restorer trusts (logs, telemetry,
  queued messages) [opened]. A Puck player host holds no such record for its
  visitors (A3), and the visitor's own host holds no record of what it earned
  abroad until return.
- *"Every engine build invalidates an exact continuation."* Not a games question:
  no studied service resumes a simulation exactly across builds; all persist rows,
  which is the image ladder's tier 2 [inference].

### Combinations Puck is placed to make

- **Characters as worlds, under the world image's ladder.** An identity is a world
  document; giving it the same row-by-row admission and conversion the world
  image gets (2b, 2c) is one mechanism for both, rather than a second save path
  [inference; the memo rejects "A separate save-game format" for worlds,
  [rejected](../hub-and-saves.md#rejected)].
- **Derived products as caches.** Stats derived from template plus per-instance
  values are never stored, so a squish is a template edit; the only stored values
  a squish must convert are the ones that were not derived (FFXIV's EXP)
  [inference from section 2].
- **Behaviour as authored programs.** The conversion (2c), the merge back from an
  epoch realm (2d) and the value-class table (8c) are authored data and programs
  the standard package chooses.
- **Deterministic replay as the independent log.** A tape reproduces an
  authority's run, so a visitor that holds its own inputs and the destination's
  signed snapshots holds something like RS3's telemetry for its own session. It
  proves something only if the visitor, not the destination, holds it (attack
  critique 1.3) [inference]. Whether a visitor can hold enough to reconstruct its
  gains without the destination's hidden state is open.
- **Portals and hidden seams.** FFXIV's visit is invisible at the seam and strict
  at the economy; a hidden seam does not need loose write rules [inference].

## How the debug world would prove it

Each check is headless on a fresh `--state-dir`, reads state through existing
verbs, and names what decides it.

1. **References survive a retune (2b).** A standard template row holds a tunable
   value an identity record references. Create a character, set its record, quit,
   edit the template's value, relaunch: `identity.show` reads the new derived
   value and the identity document's bytes are unchanged. Decides that tuning is
   no longer copied into the character.
2. **A conversion runs once (2c).** The garden's source renames a row and ships a
   conversion that maps it and refunds a removed row into a currency fact. Set
   both rows, quit, edit, relaunch, travel to the garden: the new row holds the
   mapped value, the currency fact holds the refund, the narration names the
   conversion, and a second relaunch applies nothing. `puck test --reproduce` over
   the run agrees.
3. **Per-row set-aside for an identity (2b).** An identity document gains a row the
   template no longer declares: the catalog loads the identity, drops that row by
   name and keeps the rest; `unloadable/` stays empty. Decides that one bad row no
   longer costs the character.
4. **Namespaced write-back (8b).** A night-field rule writes a garden-namespace
   fact onto a visitor; on return, adoption refuses it by name and the night
   field's own fact is adopted. This extends H9's check from `Decide` to the
   crossing.
5. **Away restriction (8c).** With a currency fact marked home-scoped in the
   standard template, a garden rule that decrements it on a visitor is refused at
   the destination's write door; the fact reads unchanged on return.
6. **Gains survive the host (8f).** Travel to the garden, let a garden rule award
   a fact, kill the process before return, relaunch: today the fact is absent (A3
   shown); with write-through it is present. The fact read is the decision.

## Open questions

- **Which value classes take which form** (8b to 8f). Lane B (custody) and lane D
  (economy) weigh the failure history; the synthesis and the owner decide.
- **Whether an identity references its template or copies it.** A design change to
  `WorldOwnedWorlds.Seed` and the identity load path; lane E confirms the current
  behaviour (this lane's reading is above) and the synthesis decides.
- **The conversion surface** (2c): where a conversion is authored, how a stored
  document records the template version it was written under, and how a
  conversion is shown total. The state-and-language plan's owner; a bounded
  prototype over the garden settles the shape.
- **The retention window for set-aside data.** A product call for the owner: how
  long an `image.refused`, a quarantined identity or a dropped row is kept, and
  who can restore it.
- **The two durable-image owner calls** remain the owner's; this page gives the
  evidence, not a ruling.
- **Whether WoW and FFXIV save characters continuously during instance play**, so
  an instance server crash loses minutes and not the session. Not established from
  a primary source by this lane; it bears on how much write-through (8f) the
  practice implies. A primary engineering source would settle it.
- **The Guild Wars 2 2020 duplication** is press-reported; an ArenaNet statement
  would confirm whether separate trading-post and guild-bank stores caused it.

## Sources

Opened by this lane:

- <https://www.eveonline.com/article/the-api-dev-blog-trilogy-volume-two-the-types-they-are-a-changing-tyrannis-1.2>
- <https://github.com/evecommander/esi-client-php/blob/master/docs/Model/GetCharactersCharacterIdAssets200Ok.md>
- <https://www.eveonline.com/news/view/the-day-the-items-disappeared>
- <https://www.eveonline.com/news/view/extended-downtime-friday-07-may-due-to-database-problems-some-ingame-items-affected>
- <https://www.eveonline.com/news/view/learning-skills-are-going-away-1>
- <https://www.eveonline.com/news/view/reimbursement-policy-updated>
- <https://www.eveonline.com/news/view/tranquility-tech-3> (database clusters, mirroring and replication; no backup detail)
- <https://us.forums.blizzard.com/en/blizzard/t/our-vision-is-to-help-you-achieve-yours/10791/7>
- <https://worldofwarcraft.blizzard.com/news/24061008/>
- <https://news.blizzard.com/en-us/article/23523206/explore-the-updates-arriving-with-the-shadowlands-pre-expansion-patch>
- <https://worldofwarcraft.blizzard.com/news/23814291/>
- <https://warcraft.wiki.gg/wiki/Item_Restoration>
- <https://warcraft.wiki.gg/wiki/Character_transfer>
- <https://eu.forums.blizzard.com/en/wow/t/580805/1>
- <https://www.pcgamesn.com/world-of-warcraft/wow-classic-hardcore-characters-revive>
- <https://na.finalfantasyxiv.com/lodestone/playguide/option_service/world_transfer_service/>
- <https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/worldvisit/>
- <https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/datacentertravel/>
- <https://ffxiv.consolegameswiki.com/wiki/Data_Center_Travel>
- <https://ffxiv.consolegameswiki.com/wiki/Value_downscaling>
- <https://consolegameswiki.com/wiki/Legacy>
- <https://eu.finalfantasyxiv.com/lodestone/topics/detail/8a8712688d43aaac9280998dddc710abdbfc928a>
- <https://www.gameinformer.com/2022/05/17/the-final-fantasy-14-housing-lottery-is-finally-fixed>
- <https://simulationdaily.com/news/final-fantasy-xiv-butt-slider/>
- <https://guildwars2.com/en/news/inside-arenanet-live-game-outage-analysis>
- <https://guildwars2.com/en/news/guesting-is-coming>
- <https://www.guildwars2.com/en/news/the-legendary-armory-arrives-july-13/>
- <https://mein-mmo.de/en/guild-wars-2-offers-compensation-for-rollback-and-is-being-criticized,504743>
- <https://oldschool.runescape.wiki/w/Rollback>
- <https://oldschool.runescape.wiki/w/Update:Old_School_RuneScape_Status_Update>
- <https://oldschool.runescape.wiki/w/Sins_of_the_Father_glitch>
- <https://oldschool.runescape.wiki/w/Old_School_RuneScape>
- <https://runescape.wiki/w/Login_lockout>
- <https://secure.runescape.com/m=news/server-login-issues---a-further-update-from-mod-warden>
- <https://secure.runescape.com/m=news/login-lockout---a-message-from-mod-warden-march-12> (recovery phases; no technical detail)
- <https://pathofexile.com/forum/view-thread/2091028>
- <https://www.mcvuk.com/diablo-iii-auction-house-fixed-illicit-profits-heading-to-charity/>
- <https://www.pcgamesn.com/new-world/gold-exploit>

Tried and refused (login wall, 403 or not found), so cited only as [summary] or
not at all: Blizzard support articles 16572 and 83440, the bluegartr repost of the
2013 FFXIV data-transfer notice, massivelyop.com, reddit, help.guildwars2.com,
help.bungie.net, icy-veins.com, and a warcraft.wiki.gg character-undelete page.

Puck files read: `docs/architecture/worlds.md` (the plain-terms section and the
identity projection and adoption section), `docs/plans/mmo-foundations.md`,
`docs/plans/mmo-foundations/attacks.md`, `docs/plans/hub-and-saves.md` (the shape,
invariants, gap 1, durable images, the ownership table and owner calls),
`src/Puck.World.Server/WorldOwnedWorlds.cs`,
`src/Puck.World.Schema/WorldIdentity.Records.cs`,
`src/Puck.World.Server/WorldPersistence.Checkpoint.cs`.

---

[Plans](../README.md) · [MMO foundations](../mmo-foundations.md) · [Attacks](attacks.md) · [Durable images](../hub-and-saves.md#durable-images)
