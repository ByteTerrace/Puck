namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // What the tick has installed and not yet delivered: a shape change carries the definition, a value change
    // carries state; the step delivers whichever is pending, once, through DeliverPending.
    private bool m_pendingDefinitionDelivery;
    private bool m_pendingStateDelivery;

    /// <summary>Delivers whatever the step installed and has not yet delivered: the definition after a shape change,
    /// or the state after a value change, stamped with the tick and the rows the export sweep found moved.</summary>
    internal void DeliverPending() {
        if (m_pendingDefinitionDelivery) {
            Host.ForgetMovedRows();
            Host.Output.DeliverDefinition(definition: m_definition);
        } else if (m_pendingStateDelivery) {
            var stamp = Host.TakeStateStamp();

            Host.Output.DeliverState(
                definition: m_definition,
                stamp: in stamp
            );
            Host.ForgetMovedRows();
        }

        m_pendingDefinitionDelivery = false;
        m_pendingStateDelivery = false;
    }
    /// <summary>Records that the live definition's state values have moved since the last delivery, so the step's
    /// <see cref="DeliverPending"/> carries them to every attached sink.</summary>
    /// <remarks>A shape change already pending outranks this: a definition delivery carries the values too.</remarks>
    internal void MarkStateDeliveryPending() => m_pendingStateDelivery = true;
}
