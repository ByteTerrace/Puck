# Gallery syllable bank

The map sign's `sampleSet` names `gallery.sample-set.json`. It contains A, E,
I (also Y), O, U and a consonant/digit syllable, all created by Puck's own
procedural synth. No external recording is used. These are intentionally
committed demonstration assets requested by the owner, authored from the synth;
the procedural guide remains a separate live voice.

Each WAV is the `audio.render` export of one character (`a`, `e`, `i`, `o`,
`u`, `b`) with the `gallery-guide` patch in `../patches/guide-voice.patch.json`,
`cadenceTicks: 24` and neutral pitch/formant/pace (1000). They are 4,800-frame,
48 kHz stereo signed 16-bit PCM files, including each syllable's quiet tail.

To recreate one, add a HUD Text element to a scratch world based on the debug
world, with `text: "a"` and
`voice { patchId: "gallery-guide", cadenceTicks: 24 }`. Boot that scratch world
headlessly with `--devices none`, then run
`audio.render <panel> <element> worlds/debug/voices/gallery/a.wav`. Repeat for
the other five characters. The exporter drives `WorldSpeechRenderer` over the
same source the live mixer pulls.

The set plays at 1500 thousandths before the speaker pitch scale. The sign's
800 pitch scale makes its effective sample rate 1.2 times the recording, while
its pace independently controls the deterministic character spacing.
