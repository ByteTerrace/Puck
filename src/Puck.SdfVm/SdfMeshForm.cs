namespace Puck.SdfVm;

/// <summary>One of the forms an owner can be drawn in (a body's look: its own body, or that body in a suit): the producer
/// keeps one for as long as the form's geometry lives in the program, and the bake answers through it which of an owner's
/// forms the frame draws (<see cref="Drawn"/>). Every form of an owner rides dynamic slots of its own
/// (<see cref="SdfMeshFormRange"/>), so its groups bake, land and stay resident while another form draws, and a switch
/// draws the new form as soon as each of its groups shows its own product.</summary>
/// <param name="Owner">What the forms are alternatives of, compared by reference: every form of one body names the same
/// owner, and no two bodies share one.</param>
public sealed class SdfMeshForm(object Owner) {
    private int m_drawn;

    /// <summary>Gets what the form is an alternative of: the forms of one owner are drawn one at a time.</summary>
    public object Owner { get; } = (Owner ?? throw new ArgumentNullException(paramName: nameof(Owner)));

    /// <summary>Gets or sets whether the frame draws this form of its owner. The bake writes it for every owner it is
    /// handed several forms of (<see cref="SdfMeshPathBake"/>'s forms): the named form once each of its groups shows its
    /// own product and stands in the mesh region, the form drawn before until then. A producer sets it only for a form it
    /// hands no alternative beside, which has nothing to hold, and reads it to know when a form it no longer names can
    /// leave the program.</summary>
    public bool Drawn {
        get => (Volatile.Read(location: ref m_drawn) != 0);
        set => Volatile.Write(location: ref m_drawn, value: (value ? 1 : 0));
    }
}

/// <summary>The dynamic slots one form of an owner rides in the composed program, as a frame publishes it
/// (<see cref="SdfFrame.MeshForms"/>).</summary>
/// <param name="Form">The form.</param>
/// <param name="FirstSlot">The first dynamic-transform slot its geometry rides, in the composed program's slots.</param>
/// <param name="SlotCount">The slots it rides from <paramref name="FirstSlot"/>.</param>
/// <param name="Named">Whether the owner names this form now (the look the body wears): the form the frame draws once its
/// groups have landed. Exactly one form of an owner is named.</param>
/// <param name="Lingers">Whether the producer keeps this form drawn after it loses the draws, while it plays a form
/// transition out (<see cref="SdfFormBand"/>): a lingering form the bake does not draw stays out of the dormant forms,
/// so its rows keep drawing beside its owner's drawn form until the producer lets it go. A named form never lingers.</param>
public readonly record struct SdfMeshFormRange(SdfMeshForm Form, int FirstSlot, int SlotCount, bool Named, bool Lingers = false);
