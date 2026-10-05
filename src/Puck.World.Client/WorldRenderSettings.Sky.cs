namespace Puck.World;

public sealed partial class WorldRenderSettings {
    private float m_skyFieldScale = 1f;

    /// <summary>The independent sky field grid fraction, one or one half. A change moves the field dispatch and
    /// composite sampling grid without moving scene resolution or retained allocation capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is neither one nor one half.</exception>
    public float SkyFieldScale {
        get => m_skyFieldScale;
        set {
            if (value is not (1f or .5f)) { throw new ArgumentOutOfRangeException(paramName: nameof(value)); }
            m_skyFieldScale = value;
            m_revision++;
        }
    }
}
