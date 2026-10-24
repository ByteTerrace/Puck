using System.Globalization;
using System.Numerics;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>One seat's deliberate view choice, optional clearance hysteresis and continuous first-person move.
/// Only presentation owns this state; a world requests a view through its camera program's state binding.</summary>
public sealed class WorldCameraViewTransition {
    private bool? m_override;
    private bool? m_autoOverride;
    private bool m_automaticFirst;
    private float m_dwell;
    private float m_progress;

    /// <summary>The current requested view, including an explicit choice before the next presented frame.</summary>
    public bool FirstPerson => (m_override ?? RequestedFirst);
    /// <summary>The last frame's resolved request.</summary>
    public bool RequestedFirst { get; private set; }
    /// <summary>The eased fraction of the path from third to first person.</summary>
    public float Blend => (m_progress * m_progress * (3f - (2f * m_progress)));
    /// <summary>Whether this frame uses first-person look semantics.</summary>
    public bool LookFirstPerson => (FirstPerson || (m_progress > 0f));
    /// <summary>The independent room behind the authored eye, in full-size metres.</summary>
    public float Room { get; private set; }

    /// <summary>Sets first or third person; null restores the program and automatic preference.</summary>
    public void SetView(bool? first) => m_override = first;
    /// <summary>Sets automatic pull-in; null restores the portable/author preference.</summary>
    public void SetAutomatic(bool? enabled) => m_autoOverride = enabled;
    /// <summary>Resolves the session, portable and author automatic preference, in that order.</summary>
    public bool Automatic(WorldSeatViewControl control, WorldSeatCameraFeel feel) =>
        (m_autoOverride ?? feel.AutoFirstPerson ?? control.AutoFirstPerson?.Enabled ?? false);

    /// <summary>Drops transient presentation state when this seat no longer frames its body.</summary>
    public void Reset() {
        m_automaticFirst = false;
        m_dwell = 0f;
        m_progress = 0f;
        RequestedFirst = false;
    }

    /// <summary>Steps the request and transition. Clearance is measured at the head independently of the
    /// current camera, so being in first person cannot manufacture its own reason to remain there.</summary>
    public void Advance(in SdfCameraFirstPerson authored, WorldCameraAutoFirstPerson automatic, bool enabled,
        float room, float deltaSeconds) {
        Room = room;
        var dt = (float.IsFinite(deltaSeconds) ? MathF.Max(0f, deltaSeconds) : 0f);
        if (!enabled || m_override.HasValue || authored.Active) {
            m_automaticFirst = false;
            m_dwell = 0f;
        } else {
            var qualifies = (m_automaticFirst ? (room > automatic.ExitDistance) : (room < automatic.EntryDistance));
            m_dwell = (qualifies ? (m_dwell + dt) : 0f);
            if (m_dwell >= (m_automaticFirst ? automatic.ExitSeconds : automatic.EntrySeconds)) {
                m_automaticFirst = !m_automaticFirst;
                m_dwell = 0f;
            }
        }
        RequestedFirst = (m_override ?? (authored.Active || m_automaticFirst));
        m_progress = Math.Clamp(m_progress + ((RequestedFirst ? dt : -dt) / authored.TransitionSeconds), 0f, 1f);
    }

    /// <summary>Moves the chase boom into the authored eye while retaining its heading. The pivot migrates to
    /// the eye as the boom retracts; the look target stays ahead, including when the remaining boom is zero.</summary>
    public void Apply(Vector3 head, Vector3 forward, float hideRadius, ref Vector3 eye, ref Vector3 target, ref float opacity) {
        var blend = Blend;
        if (blend <= 0f) {
            return;
        }
        var thirdOffset = (target - eye);
        var thirdForward = ((thirdOffset.LengthSquared() > 1e-8f) ? Vector3.Normalize(thirdOffset) : forward);
        var sightline = Vector3.Normalize(Vector3.Lerp(thirdForward, forward, blend));
        eye = Vector3.Lerp(eye, head, blend);
        target = (eye + sightline);
        var distance = Vector3.Distance(eye, head);
        var visible = Math.Clamp((distance - hideRadius) / hideRadius, 0f, 1f);
        opacity = MathF.Min(opacity, visible * visible * (3f - (2f * visible)));
    }

    /// <summary>The view request, transition and automatic hysteresis read-back.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"view={(FirstPerson ? "first" : "third")} viewSource={(m_override.HasValue ? "player" : (m_automaticFirst ? "clearance" : "program"))} transition={Blend:0.###} room={Room:0.###} dwell={m_dwell:0.###}");
}
