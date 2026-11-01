namespace Puck.World;

/// <summary>The document's camera-program table: every program a <see cref="WorldCameraProgramOp.Blend"/> or a
/// <see cref="WorldCameraProgramOp.SelectProgram"/> may name, in one place, so the validator, the rig compiler and the
/// reference census walk the same namespace.</summary>
public static class WorldCameraPrograms {
    // How deep a seat rig's default path is followed: the evaluator's own bound on blend and select nesting.
    private const int MaxDepth = 8;

    /// <summary>Enumerates every declared camera program with the document path that locates it:
    /// <c>views.seatRig</c>, <c>views.cameraRig</c>, each <c>views.programs[i]</c>, then each
    /// <c>cameras[i].rig</c>.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The programs, in that order; an absent or unnamed slot yields nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IEnumerable<(string Path, WorldCameraProgram Program)> Declared(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (definition.ViewsRaw is { } views) {
            foreach (var entry in Declared(views: views)) {
                yield return entry;
            }
        }

        var cameras = definition.Cameras;

        for (var index = 0; (index < cameras.Count); index++) {
            if (cameras[index]?.Rig is { } rig) {
                yield return ($"cameras[{index}].rig", rig);
            }
        }
    }
    /// <summary>Enumerates the camera programs a <c>views</c> section declares: its seat rig, its camera rig and its
    /// program library.</summary>
    /// <param name="views">The section.</param>
    /// <returns>The programs, with their document paths.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public static IEnumerable<(string Path, WorldCameraProgram Program)> Declared(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        if (views.SeatRigRaw is { } seatRig) {
            yield return ("views.seatRig", seatRig);
        }

        if (views.CameraRig is { } cameraRig) {
            yield return ("views.cameraRig", cameraRig);
        }

        var programs = views.Programs;

        for (var index = 0; (index < programs.Count); index++) {
            if (programs[index] is { } program) {
                yield return ($"views.programs[{index}]", program);
            }
        }
    }
    /// <summary>Returns the declared program a name resolves to, or <see langword="null"/>.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="name">The program's name.</param>
    /// <returns>The first declared program of that name (the validator refuses a second).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldCameraProgram? Find(WorldDefinition definition, string? name) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (string.IsNullOrEmpty(value: name)) {
            return null;
        }

        foreach (var (_, program) in Declared(definition: definition)) {
            if (string.Equals(a: program.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return program;
            }
        }

        return null;
    }
    /// <summary>Returns the orbit a seat steers: the seat rig's own, or, for a seat rig that selects or blends whole
    /// framings, the orbit its default path reaches through the <c>views</c> section's own programs (a select's
    /// <c>default</c>, a blend's <c>a</c>). The seat's live pitch is clamped about it and its distance is the boom's
    /// rest length.</summary>
    /// <param name="views">The section.</param>
    /// <returns>The orbit, or <see langword="null"/> when the default path reaches none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public static WorldCameraProgramOp.Orbit? SeatOrbit(WorldViewDefaults views) => SeatFraming(views: views)?.OrbitOp;
    /// <summary>Returns the program whose orbit a seat steers (<see cref="SeatOrbit"/>): the seat rig, or the program
    /// its default path reaches that authors an orbit.</summary>
    /// <param name="views">The section.</param>
    /// <returns>The program, or <see langword="null"/> when the default path reaches no orbit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public static WorldCameraProgram? SeatFraming(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var program = (WorldCameraProgram?)views.SeatRig;

        for (var depth = 0; ((depth < MaxDepth) && (program is not null)); depth++) {
            if (program.OrbitOp is not null) {
                return program;
            }

            program = Named(views: views, name: (program.SelectOp?.Default ?? program.BlendOp?.A));
        }

        return null;
    }

    // Walks the section's programs in Declared's order without an enumerator, since a seat asks every frame.
    private static WorldCameraProgram? Named(WorldViewDefaults views, string? name) {
        if (string.IsNullOrEmpty(value: name)) {
            return null;
        }

        if (Is(program: views.SeatRigRaw, name: name)) {
            return views.SeatRigRaw;
        }

        if (Is(program: views.CameraRig, name: name)) {
            return views.CameraRig;
        }

        var programs = views.Programs;

        for (var index = 0; (index < programs.Count); index++) {
            if (Is(program: programs[index], name: name)) {
                return programs[index];
            }
        }

        return null;
    }
    private static bool Is(WorldCameraProgram? program, string name) =>
        ((program is not null) && string.Equals(a: program.Name, b: name, comparisonType: StringComparison.Ordinal));
}
