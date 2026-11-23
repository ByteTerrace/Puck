using Puck.Abstractions.Collections;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.SdfVm;

// The images of other instances a view's passes sample (a screen's source, a sky layer's infinity view), as cadence
// inputs. Such an image is another instance's output, updated in place behind the same image handle, so no packed span
// the tables hash sees it change; the publication the render graph acquired it under (RenderGraphExternalInput.Publication)
// is its identity.
//
// Invariant: a pass that samples another instance's image stands only while every image it samples carries the
// publication and taint it carried when the pass last rendered. ReadSignatureOf is the one place that enforces it, inside
// SignatureOf, the door every SdfVm pass's signature passes through. Each frame the first signature an instance asks for
// observes every read of the instance; a read's revision takes a fresh value of the passes' one counter whenever its
// image's publication or taint differs from the last observation, so a revision never names two images. An image that
// carries no publication (a handle-only lease) leaves every pass that samples it without a signature.
//
// Which passes sample which images follows the kernels and their recorders: the views pass shades the screen surfaces its
// primary hits land on (sampleScreenSource, shade/sdf-environment.hlsli), so it folds each screen whose face its mesh
// scope's cull keeps (SdfWorldTables.ScreenFaceShows): a face that cull leaves out has no pixel the mesh pass drew, and a
// change of cull moves the scope's revision, which the pass signs with. The sky and the composite sample the screens a
// sky layer names (SdfSky.ScreenAt) and the view's infinity images (SdfSkyRecorder.BindInfinityViews). A pass named
// nowhere in ReadsOf folds every screen, so a pass that starts sampling one cannot stand on a stale image.
public sealed partial class SdfWorldPasses {
    private long m_readRevision;

    private enum ExternalReads : byte {
        None,
        Sky,
        Screens,
    }

    private static ExternalReads ReadsOf(string part) => part switch {
        SdfWorldPackage.Parts.Mesh or SdfWorldPackage.Parts.Pyramid or SdfWorldPackage.Parts.GlassClear or
            SdfWorldPackage.Parts.Glass or SdfWorldPackage.Parts.Surface or SdfWorldPackage.Parts.ViewsProbes or
            SdfWorldPackage.Parts.ViewsFilter or SdfWorldPackage.Parts.ViewsBins or SdfWorldPackage.Parts.GlassShade or
            SdfWorldPackage.Resolve or SdfWorldPackage.Display => ExternalReads.None,
        SdfWorldPackage.Parts.Sky or SdfWorldPackage.Parts.Composite => ExternalReads.Sky,
        _ when part.StartsWith(value: "bloom-", comparisonType: StringComparison.Ordinal) => ExternalReads.None,
        _ => ExternalReads.Screens,
    };

    // The identity of the other instances' images a part samples this frame, or null when one carries no publication.
    private ulong? ReadSignatureOf(Entry entry, SdfWorldView view, SdfFrame frame, SdfWorldTables tables, RenderGraphExternalReads? reads, in DeliveryLatch latch, string part) {
        var sampled = ReadsOf(part: part);

        if (sampled == ExternalReads.None) { return 0UL; }
        var images = (entry.Reads ??= new ReadImages());

        if (images.Frame != m_frame) {
            images.Frame = m_frame;
            images.List = reads;
            Observe(images: images, latch: in latch, reads: reads);
        }
        Span<bool> screens = stackalloc bool[SdfWorldTables.MaxScreenSurfaces];

        MarkSampled(entry: entry, frame: frame, screens: screens, sky: (sampled == ExternalReads.Sky), tables: tables, view: view, views: (part == SdfWorldPackage.Parts.Views));
        var hash = Fnv1aHash.Create();

        for (var screen = 0; (screen < screens.Length); screen++) {
            if (!screens[screen]) { continue; }
            if (!Fold(hash: ref hash, images: images, read: view.Residency.ScreenRead(reads: reads, screen: screen, view: view.View))) { return null; }
        }
        if (sampled == ExternalReads.Sky) {
            foreach (var binding in (frame.Views[Math.Min(val1: view.View, val2: (frame.Views.Count - 1))].SkyViews ?? [])) {
                if (!Fold(hash: ref hash, images: images, read: (((binding.Producer is { } producer) && (reads is not null)) ? reads.IndexOf(producer: producer) : -1))) { return null; }
            }
        }
        return hash.Value;

        static bool Fold(ref Fnv1aHash hash, ReadImages images, int read) {
            if ((read >= 0) && images.Unknown[read]) { return false; }
            hash.Add(value: unchecked((ulong)read));
            hash.Add(value: unchecked((ulong)((read >= 0) ? images.Revisions[read] : 0L)));
            return true;
        }
    }
    // Marks the screens a part samples: every declared screen (only a declared screen binds a source,
    // SdfWorldResidency.DeclareScreens), only those a sky layer names for the sky and the composite, and only those whose
    // face the mesh scope's cull keeps for the views pass, whose scope holds the cull its mesh pass draws this frame
    // (MotionOf, asked first).
    private static void MarkSampled(Entry entry, SdfWorldView view, SdfFrame frame, SdfWorldTables tables, Span<bool> screens, bool sky, bool views) {
        Span<bool> named = stackalloc bool[SdfWorldTables.MaxScreenSurfaces];

        view.Residency.DeclareScreens(declared: screens);
        if (sky) { frame.Sky.MarkScreens(screens: named); }
        var faces = (views ? entry.DrawnOf(scope: DrawScope.Mesh, tables: tables).Motion : null);

        for (var screen = 0; (screen < screens.Length); screen++) {
            screens[screen] &= ((!sky || named[screen]) && ((faces is null) || tables.ScreenFaceShows(scope: faces, screen: screen)));
        }
    }
    // Whether every image of another instance the view's passes sample stands this frame with the publication its latest
    // render sampled (RenderGraphReadStands), so the whole view may stand while a screen shows a source: the screens the
    // views pass and the sky sample (MarkSampled, ReadsOf) and the view's infinity images. A screen with no source
    // samples the filler and stands; one whose source the view has never observed does not.
    private bool ReadsStand(Entry entry, SdfWorldView view, SdfWorldTables tables, in RenderGraphReadStands stands) {
        var frame = view.Residency.Frame!;
        var images = entry.Reads;
        Span<bool> views = stackalloc bool[SdfWorldTables.MaxScreenSurfaces];
        Span<bool> sky = stackalloc bool[SdfWorldTables.MaxScreenSurfaces];

        MarkSampled(entry: entry, frame: frame, screens: views, sky: false, tables: tables, view: view, views: true);
        MarkSampled(entry: entry, frame: frame, screens: sky, sky: true, tables: tables, view: view, views: false);
        for (var screen = 0; (screen < views.Length); screen++) {
            if ((views[screen] || sky[screen]) && (view.Residency.ScreenProducerOf(screen: screen, view: view.View) is { } producer) && !Stands(producer: producer, stands: in stands)) { return false; }
        }
        foreach (var binding in (frame.Views[Math.Min(val1: view.View, val2: (frame.Views.Count - 1))].SkyViews ?? [])) {
            if ((binding.Producer is { } producer) && !Stands(producer: producer, stands: in stands)) { return false; }
        }
        return true;

        bool Stands(string producer, in RenderGraphReadStands stands) {
            var read = ((images?.List is { } list) ? list.IndexOf(producer: producer) : -1);

            return ((read >= 0) && !images!.Unknown[read] && stands.TryStanding(producer: producer, publication: out var publication) && (publication == images.Publications[read]));
        }
    }
    // Reads the image each read of the instance binds this frame, as its recorders bind it, without taking its lease: a
    // pass that stands samples nothing.
    private void Observe(ReadImages images, RenderGraphExternalReads? reads, in DeliveryLatch latch) {
        var count = (reads?.Count ?? 0);

        if (images.Revisions.Length < count) {
            Array.Resize(array: ref images.Publications, newSize: count);
            Array.Resize(array: ref images.Labels, newSize: count);
            Array.Resize(array: ref images.Unknown, newSize: count);
            Array.Resize(array: ref images.Revisions, newSize: count);
        }
        for (var read = 0; (read < count); read++) {
            var input = reads![read];
            var bound = (input.TryRead(latch: in latch, read: out var resolved) && (resolved.ImageViewHandle != 0));
            var publication = (bound ? resolved.Publication : default);
            var label = (bound ? input.Label : default);
            var unknown = (bound && !publication.IsPublished());

            if (unknown || (publication != images.Publications[read]) || (label != images.Labels[read])) {
                images.Revisions[read] = Interlocked.Increment(location: ref m_readRevision);
            }
            images.Publications[read] = publication;
            images.Labels[read] = label;
            images.Unknown[read] = unknown;
        }
    }

    // The image each read of an instance bound at its last observation (Observe), by its position in the instance's reads.
    private sealed class ReadImages {
        // The instance's reads the observation was taken over, whose positions index the arrays below.
        public RenderGraphExternalReads? List;
        public ReferenceRevision[] Publications = [];
        public DeliveryLabel[] Labels = [];
        public bool[] Unknown = [];
        public long[] Revisions = [];
        public long Frame = -1;
    }

    private sealed partial class Entry {
        public ReadImages? Reads { get; set; }
    }
}
