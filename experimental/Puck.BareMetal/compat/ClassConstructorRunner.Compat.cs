// Lazy static-constructor support for GC (reference-typed) statics. Part of Puck.BareMetal.
//
// For a type whose .cctor cannot be preinitialized away, the stock .NET 10 ILC emits a
// static-field access as a call (resolved BY NAME) to one of two ClassConstructorRunner helpers:
// the NonGC variant for value-typed statics, the GC variant for reference-typed (GC) statics.
// This supplies the GC variant; Puck.Runtime supplies the NonGC twin and the shared core.
//
// GC twin of Puck.Runtime's CheckStaticClassConstructionReturnNonGCStaticBase: run the .cctor
// once (the shared CheckStaticClassConstruction zeroes the cctor pointer before invoking it, so
// reentrant access from within the .cctor sees "already running" and does not recurse), then
// return the GC static base the JIT passed in.
//
// THREADING: single-threaded model. Race-safety would need a per-context running/done state so
// threads racing a first-access block until the .cctor completes rather than observing a
// half-built base; matters mainly for .cctors first triggered after startup from a worker thread.

namespace System.Runtime.CompilerServices {
    // NonGC variant + shared CheckStaticClassConstruction core: Puck.Runtime.
    internal static partial class ClassConstructorRunner {
        private static object CheckStaticClassConstructionReturnGCStaticBase(ref StaticClassConstructionContext context, object gcStaticBase) {
            CheckStaticClassConstruction(ref context);
            return gcStaticBase;
        }
    }
}
