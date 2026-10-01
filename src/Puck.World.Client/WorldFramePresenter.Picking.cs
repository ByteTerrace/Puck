namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    // A view keeps its producer name across dresses, including while a layout temporarily stops showing it.
    private readonly List<string> m_viewProducerNames = [];

    private string ViewProducerName(int view) {
        while (m_viewProducerNames.Count <= view) {
            m_viewProducerNames.Add(item: WorldRootGraph.ProducerOf(view: m_viewProducerNames.Count));
        }
        return m_viewProducerNames[view];
    }
}
