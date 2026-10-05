# Saved sky rows in the running artist surface

A real editor-grid seat uses a fixed camera aimed above its geometry. The
positive leg holds its red sky, edits the sky to blue and fog density to 0.03,
saves into the leg's run directory, loads that saved `.puck` and reloads its
current origin. The comparison measures the displayed frame before its graph
is disabled; the following capture observes the ordinary display. The
opposite retains red and 0.01 through precisely the same commands.

`world.lighting` reads both fog values. The sole `world.inspect` response
reads the reloaded fog before the next comparison response. Its wrapped lines
are checked in order; no later lighting echo can satisfy that inspector check. The inspector is queried without enabling its overlay or a continuous
pixel demand, so its fixed line reservation includes the shared sky and air
text and the captures contain no inspector panel. `world.cost sky` reads the
latest completed work; a missing completed sky sample is a refusal of this
fixture's evidence, never a zero count.

This source has not run in World. The actual seat, camera-only sky region,
comparison persistence across load, saved-source relocation and channel bounds
remain to be qualified on Vulkan and DirectX. The fixture does not automate an
external text editor or claim preservation of comments in a new target. Those
contracts stay with `WorldSkyEditLawTests` and `WorldSkyEditGpuLawTests`.
The atmosphere formatter's cache invalidation and steady allocation contracts stay with
`WorldSkyInspectorLawTests`; an image run cannot establish it.

Under a serial GPU grant, run the candidate's private CLI:

```text
dotnet <cli-copy>/Puck.Cli.dll canary sky-artist-surface --jobs 1 --gpu-jobs 1 --keep-transcripts --world-artifact <current-world-output>
```
