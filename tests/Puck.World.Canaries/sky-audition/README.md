# Sky row audition and clock previews

The running World's three named gradient rows make repeated-kind identity visible. Solo
selects red or blue, mute removes the selected blue row, and unmute restores it. A third
row reads the authored one-second clock through section keys. Scrubbing to zero and half
period changes the image; holding keeps it. At half-period, setting rate zero and running
releases the hold while preserving tick 25,200 and phase 0.5. The run response and a
readout after twelve ticks must report that unheld state and exact phase; its capture
must agree with the scrubbed half-period image. This compares the held and running
states at one known nonzero phase. It does not prescribe an endpoint reached by
positive-rate integration.

The separate run at rate one changes the image, while the opposite leg runs at rate
zero. The script holds before capturing that moving result so
capture completion cannot advance its comparison endpoint. Running again at rate zero
must keep the same image. Invalid row and negative-rate commands must refuse.

These controls are presentation previews; they do not save the authored clock or sky.
The source manifests await actual Vulkan and Direct3D 12 image qualification through the
private candidate CLI. No existing CPU or device proof is repeated by authoring them.
The World editor's interactive reload/save and atmosphere-only inspector invalidation
remain separate observations; these scripts do not claim those interactions.

The minimal selected run is:

```text
dotnet <private-current-cli>/Puck.Cli.dll canary sky-audition sky-artist-surface --jobs 1 --gpu-jobs 1 --debug-layers --keep-transcripts --world-artifact <current-world-artifact>
```

Reuse the root's successfully built current World artifact. Each manifest declares two
legs on both backends; the combined selection is eight authored legs, plus the runner's
pipeline warmups. This canary keeps its 60-second ceiling per leg. Run this selection
within the campaign's existing artist phase, not again after a combined selection has
already covered it. The separate at-cap companion is not added implicitly. A failed native
load or an unexpected image is a failure to diagnose, not evidence that the intended
opposite observation held.
