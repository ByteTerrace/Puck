namespace Puck.Bench;

/// <summary>
/// One benchmark scene's registration record — the CONTENT-BLIND face of a scored workload. The harness
/// (<see cref="BenchRunner"/>) sees only this descriptor and its <see cref="Controller"/>; the actual choreography
/// (which world, which camera, which cabinets boot) lives entirely CONTENT-side (the demo), reached through the
/// <see cref="IBenchSceneController"/> interface so <c>Puck.Bench</c> never names the demo, a backend, or the SDF VM.
/// The demo is merely the first content provider that registers scenes; any future game hosted on the launcher
/// registers its own against the same harness.
/// </summary>
/// <param name="Name">The scene's dotted identity, e.g. <c>room.flythrough</c>, <c>room.active</c>,
/// <c>sdf.shapes</c>.</param>
/// <param name="Category">The scene's family — <c>world</c> or <c>feature</c> — for the score table's grouping.</param>
/// <param name="Description">A human-readable one-line description shown by <c>bench.list</c>.</param>
/// <param name="WarmFrames">The per-scene settle+warm frames run (and driven through <see cref="IBenchSceneController.OnFrame"/>)
/// before any sample is recorded.</param>
/// <param name="SampleFrames">The number of frames sampled into the scene's statistics after warming.</param>
/// <param name="Weight">The scene's score weight in the geometric-mean overall; <c>0</c> means the scene is reported
/// but never scored (the warmup rung).</param>
/// <param name="Controller">The content-side driver: setup/teardown console scripts, the per-frame camera/pin hook,
/// and the readiness gate.</param>
public sealed record BenchSceneDescriptor(
    string Name,
    string Category,
    string Description,
    int WarmFrames,
    int SampleFrames,
    double Weight,
    IBenchSceneController Controller
);
