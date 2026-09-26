namespace Puck.Abstractions.Machines;

/// <summary>
/// Where a pointer aimed at a machine's video output lands, the input a light gun reads. A pointer is either off the
/// screen, the default, or on it at an exact fraction of the output's width and height in sixteenth-bit units:
/// <see cref="X"/> and <see cref="Y"/> run from 0 at the left and top edges to 65535 just short of the right and bottom
/// ones. Integers keep the aim exact, so a recorded pointer replays to the same pixel on every machine.
/// </summary>
public readonly record struct MachinePointer {
    /// <summary>The number of fraction units across a whole output edge.</summary>
    public const int FractionUnits = 65536;

    /// <summary>Initializes a new instance of the <see cref="MachinePointer"/> struct that lands on the screen.</summary>
    /// <param name="x">The horizontal fraction, rightward, in units of 1/65536 of the output's width.</param>
    /// <param name="y">The vertical fraction, downward, in units of 1/65536 of the output's height.</param>
    public MachinePointer(ushort x, ushort y) {
        OnScreen = true;
        X = x;
        Y = y;
    }

    /// <summary>Gets a pointer off the screen.</summary>
    public static MachinePointer Off => default;
    /// <summary>Gets a value indicating whether the pointer lands on the screen.</summary>
    public bool OnScreen { get; }
    /// <summary>Gets the horizontal fraction, zero while <see cref="OnScreen"/> is <see langword="false"/>.</summary>
    public ushort X { get; }
    /// <summary>Gets the vertical fraction, zero while <see cref="OnScreen"/> is <see langword="false"/>.</summary>
    public ushort Y { get; }

    /// <summary>Returns the pixel column the pointer lands on in an output <paramref name="width"/> pixels wide.</summary>
    /// <param name="width">The output's width, in pixels.</param>
    /// <returns>The column, in <c>[0, width)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is not positive.</exception>
    public int Column(int width) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);

        return ((int)((X * ((long)width)) / FractionUnits));
    }
    /// <summary>Returns the pixel row the pointer lands on in an output <paramref name="height"/> pixels tall.</summary>
    /// <param name="height">The output's height, in pixels.</param>
    /// <returns>The row, in <c>[0, height)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> is not positive.</exception>
    public int Row(int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        return ((int)((Y * ((long)height)) / FractionUnits));
    }
}
