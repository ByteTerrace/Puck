using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private SdfFrame? m_screenClosureFrame;

    internal void FreezeScreenClosure() {
        if (m_frame is not { } frame) { return; }
        var lights = new SdfLights();
        lights.CopyFrom(frame.Lights);
        var sky = new SdfSky();
        sky.CopyFrom(frame.Sky);
        m_screenClosureFrame = frame with { Lights = lights, Sky = sky,
            DynamicTransforms = Array.AsReadOnly(frame.DynamicTransforms.ToArray()),
            Views = Array.AsReadOnly(frame.Views.ToArray()), MovedTransforms = null, ProgramChanged = false };
    }

    internal void ReleaseScreenClosure() => m_screenClosureFrame = null;

    internal void ResetScreenClosureLighting() {
        m_tables?.Indirect?.ResetLightingForCapture();
        Array.Clear(m_renderedSignatures);
    }
}
