namespace Puck.World;

public sealed partial class WorldValueDomainGroup {
    /// <summary>Resolves a closed angular band as one coupled value. Reversed or out-of-domain ends hold the
    /// last valid pair; equal ends remain a valid degenerate band.</summary>
    /// <param name="property">The stable band name within this row.</param>
    /// <param name="low">The lower angle.</param>
    /// <param name="high">The upper angle.</param>
    /// <param name="values">The current shared resolver.</param>
    /// <param name="fallbackLow">The pinned lower angle.</param>
    /// <param name="fallbackHigh">The pinned upper angle.</param>
    /// <param name="domain">The admitted interval of both ends, in radians.</param>
    /// <returns>The valid or held angular band.</returns>
    public (float Low, float High) AngleBand(string property, BindableAngle low, BindableAngle high,
        WorldValueResolver values, float fallbackLow, float fallbackHigh, WorldValueDomain domain) {
        if (!m_fields.TryGetValue(property, out var field)) {
            field = new(path + "." + property,
                WorldValueDomain.SourceOf(low.Value) + "/" + WorldValueDomain.SourceOf(high.Value),
                (float)m_initial.Angle(low, fallbackLow), (float)m_initial.Angle(high, fallbackHigh));
            m_fields.Add(property, field);
        }
        var resolvedLow = (float)values.Angle(low, fallbackLow);
        var resolvedHigh = (float)values.Angle(high, fallbackHigh);
        var used = guard.OrderedPair(field.Path, field.Source, resolvedLow, resolvedHigh, field.Initial, field.Second,
            domain, strict: false);

        return ((float)used.Low, (float)used.High);
    }
}
