namespace Puck.SignedDistance;

/// <summary>Names the lanes of an <see cref="SdfInstruction"/> as a set, for its lane contract
/// (<see cref="SdfLaneContract"/>).</summary>
[Flags]
public enum SdfOperandLanes : ushort {
    /// <summary>No lane.</summary>
    None = 0,
    /// <summary>The <see cref="SdfInstruction.Shape"/> lane.</summary>
    Shape = (1 << 0),
    /// <summary>The <see cref="SdfInstruction.Blend"/> lane.</summary>
    Blend = (1 << 1),
    /// <summary>The <see cref="SdfInstruction.Material"/> lane.</summary>
    Material = (1 << 2),
    /// <summary>The x component of <see cref="SdfInstruction.Data0"/>.</summary>
    Data0X = (1 << 3),
    /// <summary>The y component of <see cref="SdfInstruction.Data0"/>.</summary>
    Data0Y = (1 << 4),
    /// <summary>The z component of <see cref="SdfInstruction.Data0"/>.</summary>
    Data0Z = (1 << 5),
    /// <summary>The w component of <see cref="SdfInstruction.Data0"/>.</summary>
    Data0W = (1 << 6),
    /// <summary>The x component of <see cref="SdfInstruction.Data1"/>.</summary>
    Data1X = (1 << 7),
    /// <summary>The y component of <see cref="SdfInstruction.Data1"/>.</summary>
    Data1Y = (1 << 8),
    /// <summary>The z component of <see cref="SdfInstruction.Data1"/>.</summary>
    Data1Z = (1 << 9),
    /// <summary>The w component of <see cref="SdfInstruction.Data1"/>.</summary>
    Data1W = (1 << 10),
    /// <summary>The three integer header lanes.</summary>
    Header = (Shape | Blend | Material),
    /// <summary>Every component of <see cref="SdfInstruction.Data0"/>.</summary>
    Data0 = (Data0X | Data0Y | Data0Z | Data0W),
    /// <summary>Every component of <see cref="SdfInstruction.Data1"/>.</summary>
    Data1 = (Data1X | Data1Y | Data1Z | Data1W),
    /// <summary>Every lane.</summary>
    All = (Header | Data0 | Data1),
}
/// <summary>How an instruction's lanes are classed by the program's admission door
/// (<see cref="SdfProgram.LaneContractOf"/>). A lane in no set is authored: the caller's value, checked against the domain
/// <see cref="SdfProgramBuilder"/> enforces for it.</summary>
/// <param name="Derived">The lanes recomputed from the instruction's other lanes (or, for a path, from its edge table) by
/// the function the builder calls (<see cref="SdfDerivedLanes"/>), and refused unless equal bit for bit.</param>
/// <param name="Unit">The lanes that hold a rotation's quaternion or a plane's normal, refused unless of unit length
/// within <see cref="SdfProgram.UnitLengthTolerance"/>, since the step analysis counts each as an isometry.</param>
/// <param name="Zero">The lanes the operation leaves unused, refused unless zero.</param>
/// <param name="Written">The lanes the program itself writes while it builds (a side table's packed place, a field scope's
/// baked scale, a relief mask's band in its region's units), whatever the caller supplied.</param>
public readonly record struct SdfLaneContract(SdfOperandLanes Derived, SdfOperandLanes Unit, SdfOperandLanes Zero, SdfOperandLanes Written);
