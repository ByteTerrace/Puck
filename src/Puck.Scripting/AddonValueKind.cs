namespace Puck.Scripting;

/// <summary>Specifies the value shape a pad command record carries, derived from its <c>padId</c> via
/// <see cref="PadCommandId.KindOf"/> rather than an on-the-wire kind byte.</summary>
public enum AddonValueKind {
    /// <summary>A pressed/released button: <c>valueX</c> is <c>0</c> or <see cref="AddonAbi.One"/>, <c>valueY</c> is <c>0</c>.</summary>
    Digital = 0,

    /// <summary>A single-axis analog value in <c>valueX</c>; <c>valueY</c> is <c>0</c>.</summary>
    Axis1D = 1,

    /// <summary>A two-axis analog value: <c>valueX</c> and <c>valueY</c> are both free.</summary>
    Axis2D = 2,
}
