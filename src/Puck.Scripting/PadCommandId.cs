namespace Puck.Scripting;

/// <summary>
/// The frozen <c>puck.addon.v1</c> abstract virtual-pad vocabulary a guest emits and the kind lookup that
/// derives each record's value shape. The names/kinds are a closed set — adding an eleventh is an ABI-version
/// event, not configuration. The pad-to-game binding lives in the consumer, never here.
/// </summary>
public static class PadCommandId {
    /// <summary>The two-axis, camera-relative floor-plane move vector (<c>0</c>).</summary>
    public const ushort Move = 0;
    /// <summary>The South face button (<c>1</c>).</summary>
    public const ushort South = 1;
    /// <summary>The East face button (<c>2</c>).</summary>
    public const ushort East = 2;
    /// <summary>The West face button (<c>3</c>).</summary>
    public const ushort West = 3;
    /// <summary>The North face button (<c>4</c>).</summary>
    public const ushort North = 4;
    /// <summary>The left shoulder button (<c>5</c>).</summary>
    public const ushort ShoulderLeft = 5;
    /// <summary>The right shoulder button (<c>6</c>).</summary>
    public const ushort ShoulderRight = 6;
    /// <summary>The left trigger, a single analog axis (<c>7</c>).</summary>
    public const ushort TriggerLeft = 7;
    /// <summary>The right trigger, a single analog axis (<c>8</c>).</summary>
    public const ushort TriggerRight = 8;
    /// <summary>The right stick, a two-axis analog value (<c>9</c>).</summary>
    public const ushort RightStick = 9;

    /// <summary>The count of pad ids in the frozen vocabulary (<c>10</c>).</summary>
    public const ushort Count = 10;

    /// <summary>Determines whether <paramref name="padId"/> names a pad in the frozen vocabulary.</summary>
    /// <param name="padId">The pad id to test.</param>
    /// <returns><see langword="true"/> if the id is in <c>[0, 10)</c>; otherwise <see langword="false"/>.</returns>
    public static bool IsKnown(ushort padId) {
        return (padId < Count);
    }

    /// <summary>Returns the value shape a record with <paramref name="padId"/> carries.</summary>
    /// <param name="padId">A known pad id (see <see cref="IsKnown"/>).</param>
    /// <returns>The pad's <see cref="AddonValueKind"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="padId"/> is not a known pad id.</exception>
    public static AddonValueKind KindOf(ushort padId) {
        return padId switch {
            Move => AddonValueKind.Axis2D,
            South => AddonValueKind.Digital,
            East => AddonValueKind.Digital,
            West => AddonValueKind.Digital,
            North => AddonValueKind.Digital,
            ShoulderLeft => AddonValueKind.Digital,
            ShoulderRight => AddonValueKind.Digital,
            TriggerLeft => AddonValueKind.Axis1D,
            TriggerRight => AddonValueKind.Axis1D,
            RightStick => AddonValueKind.Axis2D,
            _ => throw new ArgumentOutOfRangeException(
                actualValue: padId,
                message: "Pad id is not in the frozen puck.addon.v1 vocabulary.",
                paramName: nameof(padId)
            ),
        };
    }
}
