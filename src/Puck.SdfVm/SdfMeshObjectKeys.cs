using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The object keys a draw carries (<see cref="SdfMeshDraw.ObjectKey"/>): a placed copy's from its index in the frame's
/// copies, a bake group's from the first program instance of the object its first instance belongs to, in two halves of
/// the key's range so no copy shares a group's (<see cref="SdfMeshRecord.GroupObjectKeyBit"/>). The dresser keys every
/// draw through it and the field view keys its entries through it (<see cref="SdfFieldEntries"/>), so the proximity view
/// tells a pixel's own object from every other by the same key.
/// </summary>
public static class SdfMeshObjectKeys {
    private const uint GroupObjectKeys = SdfMeshRecord.GroupObjectKeyBit;

    /// <summary>Returns the first instance of the object an instance belongs to: the instance itself unless it joins the
    /// one before it (<see cref="SdfInstanceRange.JoinsPreviousObject"/>). An instance the program does not hold (a group
    /// kept from another program) is its own head.</summary>
    /// <param name="instances">The program's instances.</param>
    /// <param name="instance">The instance.</param>
    /// <returns>The object's first instance.</returns>
    public static int HeadOf(IReadOnlyList<SdfInstanceRange> instances, int instance) {
        ArgumentNullException.ThrowIfNull(argument: instances);

        if ((instance < 0) || (instance >= instances.Count)) {
            return instance;
        }
        while ((instance > 0) && instances[instance].JoinsPreviousObject) {
            instance--;
        }
        return instance;
    }
    /// <summary>Returns a placed copy's object key.</summary>
    /// <param name="copy">The copy's index in the frame's copies.</param>
    /// <returns>The key, in the copies' half of the range and never zero.</returns>
    public static uint OfCopy(int copy) => (1u + (((uint)copy) % (GroupObjectKeys - 1u)));
    /// <summary>Returns a bake group's object key.</summary>
    /// <param name="head">The first instance of the group's object (<see cref="HeadOf"/>), or -1 for the segments no
    /// instance owns.</param>
    /// <returns>The key, in the groups' half of the range.</returns>
    public static uint OfGroup(int head) => (GroupObjectKeys | (((uint)head) & (GroupObjectKeys - 1u)));
}
