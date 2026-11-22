using Puck.Hosting;

namespace Puck.SdfVm;

// What every recorder of an sdf.world pass shares: the view it records, held from its creation (the package's hold and
// the residency's retain, which its build took) until LeaveView gives them back, and followed on the frame thread to the
// view its instance resolves (SdfWorldPasses.PrepareRecord), so its recording, which may run on another thread, reads only
// its instance and the view's residency, its recording domain.
internal abstract class SdfViewRecorder(SdfWorldPasses owner, string instance, SdfWorldView view) {
    private protected SdfWorldView m_view = view;

    public object? RecordingDomain => m_view.Residency;

    public void PrepareRecord(in FrameContext context) {
        if (owner.PrepareRecord(instance: instance, view: ref m_view, context: in context)) {
            FollowedResidency();
        }
    }

    // Rebinds what the recorder declared for the residency its view left.
    private protected virtual void FollowedResidency() { }
    private protected void LeaveView() => owner.Leave(view: m_view);
}
