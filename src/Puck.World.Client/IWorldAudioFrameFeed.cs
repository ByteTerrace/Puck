using System.Numerics;

using Puck.Abstractions.Machines;
using Puck.Audio.Mixing;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's audio director a frame source drives every produced frame —
/// reconcile against the delivered definition, resolve a speaker's live gizmo pose, bind the machine-audio source
/// resolver, and publish this frame's mixed snapshot. Declared here so a Client-side type can hold the director
/// without naming the root's concrete type, the same shape <see cref="IWorldScreenPresenter"/> and
/// <see cref="IWorldSimulationClock"/> already carry for the frame source's other root-held dependencies. Extends
/// <see cref="IWorldAudioCueSink"/> rather than re-declaring <c>SubmitCue</c> — a frame source's need is that seam's
/// need plus these four.</summary>
public interface IWorldAudioFrameFeed : IWorldAudioCueSink {
    /// <summary>Gets or sets the per-screen audio-machine source resolver a frame source binds once, at composition,
    /// so a booted/ejected/live-swapped machine self-heals the next published snapshot.</summary>
    Func<int, IAudioMachine?>? MachineSourceResolver { get; set; }

    /// <summary>Resolves this frame's listener and emitter poses and publishes one mixed snapshot.</summary>
    /// <param name="transforms">The frame's packed dynamic transforms.</param>
    /// <param name="seats">The per-slot resolved view-camera poses (the listener policy's candidates).</param>
    /// <param name="deltaSeconds">The clock advance since the previous publish.</param>
    AudioSnapshot Publish(ReadOnlySpan<DynamicTransform> transforms, ReadOnlySpan<WorldSeatCameraPose> seats, float deltaSeconds);
    /// <summary>Reconciles the derived emitter table against a delivered definition.</summary>
    /// <param name="definition">The delivered definition.</param>
    void ReconcileSpeakers(WorldDefinition definition);
    /// <summary>Resolves a speaker row's live gizmo pose.</summary>
    /// <param name="speaker">The (possibly drag-composed) speaker row.</param>
    /// <param name="transforms">The frame's packed dynamic transforms.</param>
    /// <param name="position">The resolved world position.</param>
    /// <returns><see langword="false"/> when the anchor is unresolvable this frame.</returns>
    bool TryResolveSpeakerPose(WorldSpeaker speaker, ReadOnlySpan<DynamicTransform> transforms, out Vector3 position);
}
