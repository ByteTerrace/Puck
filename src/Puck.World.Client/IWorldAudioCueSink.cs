using System.Numerics;

namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's audio director a scene/frame producer fires world-event cues
/// through. Declared here so a Client-side type can hold the write without naming the root's concrete audio-director
/// type — the same shape <see cref="IWorldAudioLever"/> already carries for the session-lever sink's own write.
/// </summary>
public interface IWorldAudioCueSink {
    /// <summary>Fires a world-event cue — see the root audio director's own <c>SubmitCue</c> remarks for the full
    /// producer/trigger contract this seam only narrows, never changes.</summary>
    /// <param name="eventToken">The published event token.</param>
    /// <param name="site">The event's world position, or <see langword="null"/> when none is derivable.</param>
    void SubmitCue(string eventToken, Vector3? site);
}
