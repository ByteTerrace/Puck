namespace Puck.Bench;

/// <summary>
/// A benchmark scene's CONTENT-SIDE driver. Setup/teardown are console verb SCRIPTS first (the console is the
/// control plane; the choreography is readable, replayable, and identical to what an agent would type over stdin),
/// while <see cref="OnFrame"/> is the per-frame delegate hook for the things verbs cannot express at frame cadence —
/// a deterministic camera dolly, a pinned overview pose. Controllers are supplied by the content side (the demo), so
/// they may reference content internals freely; the harness sees only this interface.
/// </summary>
public interface IBenchSceneController {
    /// <summary>The console lines the harness submits, in order, before warming (may be empty). Idempotent verbs are
    /// preferred so a scene can be re-entered without a restart.</summary>
    IReadOnlyList<string> SetupScript { get; }
    /// <summary>The console lines the harness submits, in order, after the scene's samples are collected (may be
    /// empty) — e.g. eject the cabinets a setup script booted.</summary>
    IReadOnlyList<string> TeardownScript { get; }

    /// <summary>Called once per produced frame from warm-start through the last sampled frame —
    /// <paramref name="frameIndex"/> counts from <c>0</c> at warm-start; sampling begins at the scene's
    /// <see cref="BenchSceneDescriptor.WarmFrames"/>. This is where a scene drives a deterministic camera (the dolly)
    /// or asserts a pin. Must be cheap — it runs on the render thread inside the frame-timing publish.</summary>
    /// <param name="frameIndex">The frame counter, <c>0</c> at warm-start.</param>
    void OnFrame(int frameIndex);

    /// <summary>Polled once per frame after the setup script drains; the scene will not begin warming until it returns
    /// <see langword="true"/> (e.g. "all four cabinets report booted"). Return <see langword="true"/> immediately when
    /// readiness is trivial.</summary>
    /// <returns>Whether the scene is ready to warm.</returns>
    bool IsReady();
}
