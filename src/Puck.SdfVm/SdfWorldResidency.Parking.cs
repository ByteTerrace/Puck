using System.Globalization;

namespace Puck.SdfVm;

// A residency nothing observes gives its tables back on a live device and builds them again when it is read: the door a
// device loss uses (the residency keeps its bake, its lighting's measured words for ForgetLightingSeconds, its exposures and its frame, and its
// next preparation builds tables from its pipeline set, which it keeps leased), opened by the presentation
// (SdfWorldPasses.BeginFrame) for a world no view has shown for a while.
//
// Invariant: no submission recorded after a park reads the parked tables, no recording reads a residency that stands
// parked, and the tables are disposed only after every submission recorded before it completed. The first holds because
// m_tables is the one reference every recording, upload and background pump reaches the tables through, and parking
// clears it on the frame thread between frames, before any residency is staged. The second holds because every render
// reaches its residency through a staging (Stage, BeginStage, StageLate through Stage), which claims the wake
// (ClaimWake) before it prepares, and a preparation that finds the residency standing parked settles nothing for the
// frame (BeginPack), so whichever door prepared it first that frame, the staging builds new tables; a read that renders
// nothing finds no tables and stands on its views' last images (StandsParked, which SdfWorldPasses.IsUnchanged and the
// readers' preparation honour). The third holds because the parked tables go to the device's one retirement
// (GpuDeviceRetirement), which disposes them once the checkpoint queued after the frame's batch completes; nothing waits
// for the device. Where it stops: a woken residency whose tables' build is refused or still waits on its pipeline set has
// no tables, and SdfWorldResidency.Staged refuses its recording by name.
public sealed partial class SdfWorldResidency {
    /// <summary>The seconds a residency must go unobserved before it parks while the device's pool stands past its
    /// limit (<see cref="SdfResidencySelector.Excess"/>).</summary>
    public const double PressedParkSeconds = 2d;
    /// <summary>The seconds a residency must go unobserved before it parks when the device's pool fits its limit: a
    /// world no view has shown for this long holds none of its tables, as a world first shown does.</summary>
    public const double IdleParkSeconds = 30d;
    /// <summary>The longest a woken residency withholds its views (<see cref="Waking"/>) while its lighting republishes
    /// and its meshes land, in seconds: past it the views render whatever has arrived.</summary>
    public const double WakeHoldSeconds = 1.5d;
    /// <summary>The seconds a residency stays parked before it gives back its measured lighting too: past it, a wake
    /// lights the world from nothing, as a world first shown is lit.</summary>
    public const double ForgetLightingSeconds = 60d;

    // The clock the residency's observation and wake are timed by (its selector's), taken when its tables are first built.
    private TimeProvider? m_clock;
    // When a reader last asked for the residency (Observe), on m_clock; zero before the first. Which instance asked.
    private long m_observedAt;
    private string? m_observedBy;
    private bool m_parked;
    private long m_parks;
    private long m_wakes;
    // When the residency last woke, on m_clock, while its views wait for what parking took (Waking); zero otherwise.
    private long m_wokenAt;
    // When it last parked, on m_clock, and how many times over the pressed wait it must go unobserved before it parks
    // again: doubled by a wake within IdleParkSeconds of its park, so a world read now and then (a sky layer, a window at
    // the edge of sight) stops rebuilding its tables and lighting every few seconds, and reset by a park that lasts.
    private long m_parkedAt;
    private int m_parkBackoff = 1;
    // Whether the current park has given back the lighting (ForgetParkedLighting), and how many parks have.
    private bool m_lightingForgotten;
    private long m_lightingDrops;

    /// <summary>Gets the seconds the residency must go unobserved before it parks while the device's pool stands past
    /// its limit: <see cref="PressedParkSeconds"/>, longer for a residency that woke soon after its recent parks, and
    /// never past <see cref="IdleParkSeconds"/>.</summary>
    internal double PressedParkAfter => Math.Min(val1: (PressedParkSeconds * m_parkBackoff), val2: IdleParkSeconds);
    // One more for every tables the residency builds, so a view that bound the earlier tables' objects knows to bind again.
    private long m_tablesGeneration;

    /// <summary>Gets whether the residency has given its tables back (<see cref="Park"/>) and not been read since.</summary>
    public bool IsParked => m_parked;
    /// <summary>Gets one more for every tables the residency builds, a park's wake and a device loss's rebuild among
    /// them.</summary>
    public long TablesGeneration => m_tablesGeneration;

    // Whether the presentation's sight has ever judged the world (Want): a world behind glass, observed by whether a view
    // that renders sees or nears its glass, and not by being read, since the glass's image is read every frame a consumer
    // binds it. When the sight last wanted it, on m_clock. Whether a park is to end at the residency's next preparation.
    private bool m_sighted;
    private long m_wantedAt;
    private bool m_wakeOwed;
    // Who last wanted the world and why (Want), which world.residency prints.
    private string? m_wantedBy;
    private string? m_wantedWhy;
    private float m_wantedMetres = float.NaN;

    /// <summary>Gets whether the presentation's sight judges the residency (<see cref="Want"/>): its views' images may be
    /// read while it is parked, standing on the images they last rendered, and only a render or the sight wakes it.</summary>
    public bool Sighted => m_sighted;
    /// <summary>Gets whether the presentation's sight judges the residency and has not wanted it for
    /// <see cref="PressedParkSeconds"/>: no rendering view sees its glass or is led to it. A window kept rendering only so
    /// the world's meshes arrive is not kept for such a world, whose render would otherwise observe it every frame and
    /// keep it from ever parking.</summary>
    public bool SightLetGo => (m_sighted && (m_clock is { } clock) && (clock.GetElapsedTime(startingTimestamp: m_wantedAt).TotalSeconds >= PressedParkSeconds));
    /// <summary>Gets whether the residency is parked and stands only on its views' last images: no reader has asked to
    /// render it, and the sight has not wanted it, since it parked. A preparation builds no tables for it.</summary>
    public bool StandsParked => (m_parked && !m_wakeOwed);

    /// <summary>Records that the presentation's sight wants the world this frame: a view that renders sees the glass it
    /// shows on, or the glass stands near enough out of sight to be shown soon. The residency counts as observed, and a
    /// parked one builds its tables at its next preparation, which <see cref="SdfWorldPasses.BeginFrame"/> serves
    /// whether or not anything reads it, so its world and lighting stand before its glass is seen. Called on the frame
    /// thread.</summary>
    /// <param name="consumer">The instance whose sight wants the world, which <c>world.residency</c> names.</param>
    /// <param name="why">Why it is wanted, a constant word (the glass is seen, small, of unknown sight, or leads), which
    /// <c>world.residency</c> names.</param>
    /// <param name="metres">The distance the glass leads by, or <see cref="float.NaN"/> where the sight wants it.</param>
    public void Want(string consumer, string why, float metres = float.NaN) {
        m_sighted = true;
        m_wantedBy = consumer;
        m_wantedWhy = why;
        m_wantedMetres = metres;
        if (m_clock is { } clock) { m_wantedAt = clock.GetTimestamp(); }
        ClaimWake();
    }
    /// <summary>Records that a reader of the frame asked for the residency: a view the frame renders, or for a residency
    /// the sight does not judge (<see cref="Sighted"/>), a view the frame reads. A parked residency wakes at its next
    /// preparation.</summary>
    /// <param name="reader">The instance whose read or render asked, which <c>world.residency</c> names.</param>
    internal void Observe(string reader) {
        m_observedBy = reader;
        if (m_clock is { } clock) { m_observedAt = clock.GetTimestamp(); }
        ClaimWake();
    }
    // A render's, a reader's or the sight's claim on a parked residency: its next preparation builds tables. A preparation
    // that found it standing parked earlier in the frame settled nothing (BeginPack), so the claim is served this frame.
    private void ClaimWake() {
        if (m_parked) { m_wakeOwed = true; }
    }
    /// <summary>Returns the seconds since a reader last asked for the residency (<see cref="Observe"/>) or the sight last
    /// wanted it (<see cref="Want"/>), or since its tables were built when neither has, or zero before it has a
    /// clock.</summary>
    /// <returns>The seconds.</returns>
    internal double UnobservedSeconds() => ((m_clock is { } clock) && (m_observedAt != 0L))
        ? clock.GetElapsedTime(startingTimestamp: Math.Max(val1: m_observedAt, val2: m_wantedAt)).TotalSeconds
        : 0d;
    // The seconds since a timestamp on the residency's clock, or -1 for none.
    private double SecondsSince(long at) => ((m_clock is { } clock) && (at != 0L)) ? clock.GetElapsedTime(startingTimestamp: at).TotalSeconds : -1d;
    /// <summary>Gets the device bytes parking would give back: the tables' regions and buffers and their background
    /// work's slots and clients (the lighting's stores and caches among them); zero without tables.</summary>
    internal long ParkableBytes => ((m_tables is { } tables)
        ? ((long)(tables.TableBytes.DeviceLocal + tables.GpuWork.OwnedBytes.DeviceLocal))
        : 0L);
    /// <summary>Gets why a woken residency's views keep the image they last rendered, or <see langword="null"/>: for
    /// <see cref="WakeHoldSeconds"/> at most after a wake, until its lighting republishes the measured volume into its
    /// new tables and the meshes its draws name have landed.</summary>
    public string? Waking {
        get {
            if ((m_wokenAt == 0L) || (m_clock is not { } clock)) { return null; }
            if ((clock.GetElapsedTime(startingTimestamp: m_wokenAt).TotalSeconds >= WakeHoldSeconds) || !(MeshIrradiance.Republishing || (m_tables?.MeshesArriving ?? false))) {
                m_wokenAt = 0L;
                return null;
            }
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"its tables were built again after a park and {(MeshIrradiance.Republishing ? "its lighting republishes" : "its meshes land")}");
        }
    }

    /// <summary>Gives the residency's tables back on a live device: the tables, their regions and draw records, their
    /// shadows and their background work (the lighting's trace scene, hit cache and feedback among it) go to the device's
    /// retirement, which disposes them once every submission recorded before now has completed, and the residency's names
    /// leave the presentation's mesh region with them. The residency keeps its bake, its lighting's measured volume (for <see cref="ForgetLightingSeconds"/>), its
    /// exposures, its frame and its pipeline set, and builds its tables again when it is next read
    /// (<see cref="Prepare"/>). Called on the frame thread between frames, before any residency is staged.</summary>
    /// <returns><see langword="true"/> when the tables were given back; <see langword="false"/> when the residency has
    /// none, is released, holds its frame for a capture or has begun the frame's preparation.</returns>
    internal bool Park() {
        if (m_disposed || (m_tables is not { } tables) || (m_deviceContext is not { } device) || HoldsFrame || (m_pack != PackStage.None) || m_submitted) {
            return false;
        }

        m_tables = null;
        m_probeTables = null;
        m_parked = true;
        m_wakeOwed = false;
        m_wokenAt = 0L;
        m_parkedAt = (m_clock?.GetTimestamp() ?? 0L);
        m_lightingForgotten = false;
        m_parks++;
        ResetReady();
        Array.Clear(array: m_renderedSignatures);
        // The clients unwind now, so the lighting's worker waiting on a job the parked tables will never pump lets go
        // before the retirement disposes them.
        tables.GpuWork.StopClients();
        device.Services.Retirement.Retire(item: tables);

        return true;
    }
    // Gives back the measured lighting of a residency parked for ForgetLightingSeconds: its irradiance volume (the probe
    // cache's blocks and tables, the volume's words and the trace scene's hierarchies, all host memory) is disposed and a
    // fresh one stands in its place, which the next tables bind (EnsureTables hands the volume to the new probe client)
    // and which lights the world from nothing when it wakes. Only a parked residency drops it: no tables, so no client
    // or recording reads the old volume, and the parked tables' client was stopped at the park and retired long before.
    private void ForgetParkedLighting() {
        if (!m_parked || m_lightingForgotten || (m_clock is not { } clock) || (clock.GetElapsedTime(startingTimestamp: m_parkedAt).TotalSeconds < ForgetLightingSeconds)) {
            return;
        }

        var forgotten = MeshIrradiance;

        MeshIrradiance = new SdfMeshIrradiance();
        m_lightingForgotten = true;
        m_lightingDrops++;
        forgotten.Dispose();
    }
    // Leaves the parked state as new tables are built for a reader (EnsureTables).
    private void Wake() {
        m_tablesGeneration++;
        if (!m_parked) { return; }
        m_parked = false;
        m_wakeOwed = false;
        m_wakes++;
        m_wokenAt = (m_clock?.GetTimestamp() ?? 0L);
        m_parkBackoff = (((m_clock is { } clock) && (clock.GetElapsedTime(startingTimestamp: m_parkedAt).TotalSeconds < IdleParkSeconds))
            ? Math.Min(val1: (2 * m_parkBackoff), val2: ((int)Math.Ceiling(a: (IdleParkSeconds / PressedParkSeconds))))
            : 1);
    }
    /// <summary>Describes the residency's parking for <c>world.residency</c>.</summary>
    /// <returns>One line.</returns>
    public string DescribeParking() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"parking parked={(m_parked ? "yes" : "no")} sighted={(m_sighted ? "yes" : "no")} wake-owed={(m_wakeOwed ? "yes" : "no")} parks={m_parks} wakes={m_wakes} unobserved={UnobservedSeconds():0.0}s read-ago={SecondsSince(at: m_observedAt):0.0}s read-by={m_observedBy ?? "none"} wanted={m_wantedWhy ?? "never"}{(float.IsNaN(f: m_wantedMetres) ? string.Empty : $"@{m_wantedMetres:0.0}m")} by={m_wantedBy ?? "none"} wanted-ago={SecondsSince(at: m_wantedAt):0.0}s pressed-after={PressedParkAfter:0.0}s lighting-drops={m_lightingDrops} parkable-device-bytes={ParkableBytes} waking={(Waking is null ? "no" : "yes")}");
}
