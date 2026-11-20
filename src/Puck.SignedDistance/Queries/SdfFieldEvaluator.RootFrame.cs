namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    // Point and interval walks share the same frame lifetime. The first executed chain runs the original
    // instruction sequence, so fixed-point rounding and interval outward rounding are preserved.
    private struct PointFrame<TPoint, TScale> where TPoint : struct where TScale : struct {
        private int m_key;
        private int m_pending;
        private TPoint m_point;
        private TScale m_scale;

        public void Capture(int index, TPoint point, TScale scale) {
            if ((m_pending == 0) || (index != m_pending)) { return; }

            m_point = point;
            m_scale = scale;
            m_pending = 0;
        }

        public void Reset(SdfProgram.RootFrame frame, ref int index, ref TPoint point, ref TScale scale) {
            if (frame.Length == 0) { return; }

            if ((frame.Key == m_key) && (m_pending == 0)) {
                point = m_point;
                scale = m_scale;
                index += frame.Length;
            } else {
                m_key = frame.Key;
                m_pending = (index + frame.Length + 1);
            }
        }
    }
}
