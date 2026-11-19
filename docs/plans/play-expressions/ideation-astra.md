# Six ways to play an avatar

I’d build the **Reed Dancer**, **Splintmaker**, and **Reckoner** first. They give the bard, puzzle-healer, and spreadsheet-mage three different pleasures you can see from across a room. The bard leaves a rest for someone to answer. The healer turns a broken part in their hands until the pieces fit. The mage spots a pattern early enough to bet their own position on it. Each gives another player something to *do* with that skill.

## 1. Reed Dancer

*You play a wind instrument well enough to make a gap in the music feel like an invitation to move.*

A curved electric reed hooks over the left shoulder. Hold to bring the mouthpiece to the lips and sustain a breath. Tap the valves in time with the music to articulate notes. The left elbow opens on the long note, the right fingers work the short ones, and the player can keep steering throughout. Release the breath and the bell swings clear of the hips. That frees the dancer to duck, cross-step, or kick through the rest. The player chooses the step and its direction. The comedy is in delivering an immaculate little heel flick in the middle of a dangerous exchange.

**Five.** The bard ducks under a sweep and plays a rising phrase, then plants a heel facing the opening. A partner can cross on the rest and answer with a stomp. If nobody takes it, the bard can finish their own turn. **Forty.** Players close enough to answer hear the phrase and see the lifted heel. Across the room, the bell coming down and a cluster of bodies changing direction tell the larger story. Several bards can trade phrases without putting the entire raid on one clock.

**Combination and engine pressure.** An Understudy could learn the footwork while the bard keeps playing. The existing music clock and timed input give the phrase a starting point. The hard part is making the note, valve movement, planted foot, and authority’s judgment feel like one event to a moving player on a remote connection. A silent run should still be playable by watching the body. [Audio and rhythm windows](docs/reference/audio.md) · [Timed action input](docs/decisions/input-channels.md)

## 2. Splintmaker

*You mend a moving thing by finding the one shape that lets it move freely again.*

A fan of hinged ribs folds against the forearm. Face a damaged joint, spread the ribs with the opposite thumb, and take an impression of the break. Turn the wrist to reverse the impression, fold the ribs around the other side, then squeeze both palms together to seat the splint. The puzzle is physical. A piece can fit beautifully at rest and bind as soon as the elbow bends. The patient tries the movement and the maker sees which rib lifts.

**Five.** A teammate can keep a damaged shield arm low and offer it while backing toward cover. The maker walks beside them, fits the splint, and gets a second chance when the teammate raises the shield. **Forty.** The same interaction scales up to a buckling gate. Crews mend separate hinges while another group cycles the gate under load. Everyone can read how far the gate opens. Only the people at a hinge need to see its loose rib.

**Combination and engine pressure.** A climber could wear the fan on one arm and brace against the gate with the other. Puck’s signed distance fields would make the impression, the fitted piece, and the remaining clearance playable geometry. The demanding case is a splint that looks flush but catches under motion. The debug-world exhibit should let a player *feel* that catch through the shield or gate and correct it by hand. This takes the Alchemy table’s pleasure of discovering a fit into a problem whose answer changes as a partner moves. [Debug-world Alchemy](worlds/debug/alchemy.md) · [Authored surface holds](worlds/standard/abilities.puck)

## 3. Reckoner

*You can read where something will be a moment from now, and you’re willing to stand there first.*

The tool is a folding brass rule with sliding teeth. Each squeeze marks where a moving object was at that instant. Spread the arms and the marks stay spaced along the rule. With the free thumb, slide the last interval forward, repeat a pair of intervals, or widen the next one by the difference between the last two. Point the extended rule into the world to place your prediction. This is the pleasure of filling down a spreadsheet formula, performed at shoulder height while running.

**Five.** A gate skips along a row of arches. One player samples its passage, notices that it alternates a short hop and a long one, and marks the arch where it should next open. A partner chooses whether to commit to the run. The satisfying moment is already being in stride when the gate arrives. **Forty.** Reckoners on opposite balconies can follow different moving gates. The raid sees their chosen arches. A nearby teammate can inspect the marks on a rule, spot a missed sample, and offer a correction.

**Combination and engine pressure.** A bard can supply an even sampling rhythm. An uneven rhythm is useful too, provided the Reckoner accounts for it. The proposed prediction would be a small authored calculation over observations the player actually received. That stresses deterministic sampling, rule cost, and disclosure. If the gate changes course after the mark goes down, the prediction should be allowed to be gloriously wrong. [Authored calculations and state](docs/decisions/state-and-language.md) · [Rule work budgets](docs/reference/state/analysis.md)

## 4. Understudy

*You learn another player’s movement and perform their part after they have gone.*

A palm-sized jointed figure hangs from a finger ring. Hold it beside a willing partner as they demonstrate a short movement. The thumb steers its hips and two fingers set its feet. When its posture matches, close the fist to remember the sequence. Hook the figure onto the belt to perform the movement with your own body. The original performer can turn around and watch what you learned.

**Five.** A teammate demonstrates the shift of weight that keeps a rocking platform level, then crosses while the Understudy takes their place. A heavier avatar may need a wider stance. **Forty.** A lead dancer teaches a front row. Those players turn and teach the next while the first row gets back to the encounter. The handoff is visible in the instant each row turns around. A late arrival can learn from the nearest person who knows it.

**Combination and engine pressure.** A reed player can teach a sidestep while keeping their melody. An Understudy with a splint can borrow a patient’s test movement and feel where the repaired joint sticks. The proposed figure remembers a short sequence of intended movements. The receiving body performs them under its own reach, equipment, permissions, and current footing. That would test which parts of an authored movement transfer between very different rigs and whether a failed imitation still looks intelligible. [Authored rig movement](worlds/standard/rig.puck) · [Authored avatar abilities](worlds/standard/abilities.puck)

## 5. Flock Tender

*You steer a flock by understanding what its members will choose to do.*

A shoulder roost carries a handful of folding mechanical birds. Raise a forearm and one hops onto it. Cup the bird to shield its eyes, turn the shoulders to give it a sightline, then open the hand. Before it flies, its head tracks whatever has caught its attention. One bird follows motion, another seeks a perch beside a resting bird. Watching that head turn is how a player learns to handle a new bird.

**Five.** A partner carries a perch across a gap while the tender waits for a bird to look their way. Once it lands, a second bird will follow the first. Either player can recover a poor release by moving into view. **Forty.** Different groups tend roosts along a procession. A downstream group raises its perches to receive the flock before the upstream group folds theirs. From the back of the raid, the flock banking as one tells you the handoff worked.

**Combination and engine pressure.** A dancer can draw the eye of a motion-seeking bird. A Reckoner can call where a flock circling out of sight is likely to reappear. Authors would define what each bird notices and how it chooses. Seeded variation could give individuals recognizable habits. The engine question is how many birds can keep making meaningful local decisions around forty moving bodies, and whether players can still pick out the one bird whose choice matters to them. [Rule scheduling and work budgets](docs/reference/state/analysis.md)

## 6. Bell Diver

*You climb inside a machine small enough for a friend to hold and fix the part their hands cannot reach.*

The tool is a clear inspection bell with a knurled rim. The bearer cradles it against the ribs and twists the rim to turn its inner housing. A partner steps through the bell’s mouth. Inside, the same housing is room-sized. The diver climbs its ribs and reaches into the mechanism. From inside, the bearer’s thumb passing over the glass is an enormous, unmistakable pointing gesture.

**Five.** A lift governor jams. The diver climbs to the stuck pawl while the bearer turns the housing to put it within reach. The other three can operate the lift, take its weight off the mechanism, and feel when it runs freely again. **Forty.** Several crews could work on the governors of one large machine. Beside a bell you can look in and follow the diver. Across the encounter you can see which governor has started turning. Both views report a change the rest of the raid can use.

**Combination and engine pressure.** A Splintmaker could go inside while a Reckoner outside predicts the next unloaded part of the cycle. The gallery paintings already give Puck a place where an observer sees a destination and a body can cross into it. A carried, rotating entrance adds harder questions. Can the bearer walk without making the diver’s camera lurch? Can each world own its part of the repair and agree when the pawl catches, even if the bearer lets go? [Gallery painting portals](worlds/debug/paintings.puck) · [Worlds and authority](docs/architecture/worlds.md#the-idea-in-plain-terms)

## What makes a class

I’d let configuration name the player’s practice, and let **what the body is currently doing** determine which combinations work. A shoulder hook matters because the bard can let go of the reed. A belt hook matters because the Understudy can carry a learned step into a two-handed job. The instant a Splintmaker closes both palms, somebody else has to turn the gate. That gives equipment choices consequences another person can see and act on.

I’d put the examples and their rules in the authored standard package. A world could change what a good phrase, a sound repair, or a correct forecast accomplishes. The encounter authority would decide whether the action succeeded from the inputs and facts it owns. The character could bring its instrument and learned phrase into another zone, where the local author decides what answers that music. This leaves damage, healing, protection, and the eventual class names open. [Standard ability authoring](worlds/standard/abilities.puck) · [World authority and grants](docs/architecture/worlds.md#the-idea-in-plain-terms)

## First build in the debug world

I’d give the first three tools **one moving gate on the way from the gallery into the valley**. A newcomer can watch it from safety, cross it, then turn around and help the next person through. Each tool earns its place through a different observation.

- **Reed Dancer first.** Can a stranger see the planted heel, take the offered rest, and cross while the bard is still moving? This puts input, music, camera, and avatar weight under pressure together.
- **Splintmaker second.** Can one player feel the gate catch and deliberately reproduce the catch so another player can find the bad fit? This makes cooperation depend on real geometry and contact.
- **Reckoner third.** Can someone watch a few openings, stake out an arch, and persuade a stranger to run there before the mechanism moves? This tests whether doing maths in an action encounter feels like being perceptive.

