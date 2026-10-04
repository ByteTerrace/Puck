namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>Gets whether root field operands are independently unioned, so omitting a whole instance cannot
    /// remove an operand of another instance's subtraction, intersection or modifier. Internal scoped CSG is allowed.</summary>
    public bool IndirectInstancesComposable { get; }
}
