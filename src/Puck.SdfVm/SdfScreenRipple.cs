using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The ripple of one opening that opts in (a window whose source authors <c>ripple</c>): the clock its idle waves turn on
/// and the contacts whose rings are crossing its glass. Presentation only; the screen shading reads it
/// (<c>sdfOpeningRipple</c> in <c>shade/sdf-environment.hlsli</c>) and nothing in the simulation does.
/// </summary>
/// <param name="Clock">The ripple clock in seconds, wrapped at <see cref="ClockPeriod"/>.</param>
/// <param name="Contact0">The first contact slot.</param>
/// <param name="Contact1">The second contact slot.</param>
/// <param name="Contact2">The third contact slot.</param>
/// <param name="Contact3">The fourth contact slot.</param>
public readonly record struct SdfScreenRipple(float Clock, Vector4 Contact0, Vector4 Contact1, Vector4 Contact2, Vector4 Contact3) {
    /// <summary>The seconds the clock wraps at. KEEP IN SYNC with <c>RippleClockPeriod</c> in <c>shade/sdf-environment.hlsli</c>.</summary>
    public const float ClockPeriod = 256f;

    /// <summary>The number of contacts an opening shows at once.</summary>
    public const int Contacts = 4;

    /// <summary>The seconds a contact's rings last. KEEP IN SYNC with <c>RippleLifetime</c> in <c>shade/sdf-environment.hlsli</c>.</summary>
    public const float Lifetime = 1.4f;

    /// <summary>The <c>float4</c> rows a screen's ripple record occupies: the state row, then one row a contact. KEEP IN
    /// SYNC with <c>ScreenRippleData</c> in <c>frame/sdf-screen-mapping.hlsli</c>.</summary>
    public const int Rows = (1 + Contacts);

    /// <summary>Returns a contact: where it touched, how long ago and how strongly.</summary>
    /// <param name="u">The horizontal surface coordinate, 0 at the glass's left edge and 1 at its right.</param>
    /// <param name="v">The vertical surface coordinate, 0 at the glass's top edge and 1 at its bottom.</param>
    /// <param name="age">The seconds since the contact, below <see cref="Lifetime"/> while its rings show.</param>
    /// <param name="strength">The contact's strength, in <c>(0, 1]</c>; zero marks an empty slot.</param>
    /// <returns>The contact's packed slot.</returns>
    public static Vector4 Contact(float u, float v, float age, float strength) => new(
        w: strength,
        x: u,
        y: v,
        z: age
    );
}
