using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private readonly record struct SkyWriterContext(WorldEnvironmentResolve Owner, WorldDefinition Definition,
        WorldValueDomainGroup Domain, ISdfSkyParameterTable Table, int Index, WorldSkyMotion? Motion,
        SdfSkyParameterTable<SdfSkyStopData> Stops, int FirstStop, string Path);

    private abstract class SkyWriter(in SkyWriterContext context) {
        protected SkyWriterContext Context { get; } = context;
        protected WorldValueDomainGroup Domain => Context.Domain;
        public abstract bool Write(WorldValueResolver values);
        public virtual bool Moves => false;
        public virtual void Move(PresentedTick tick) { }
        protected float Integral(WorldRateIntegral? rate, PresentedTick tick, double modulus) => Context.Owner.Evaluate(rate, tick, modulus);
    }

    private abstract class SkyWriter<T>(in SkyWriterContext context) : SkyWriter(context) where T : unmanaged {
        private readonly SdfSkyParameterTable<T> m_table = (SdfSkyParameterTable<T>)context.Table;
        protected ref T Data => ref m_table.Rows[Context.Index];
    }

    private static float Periodic(float value, float period) => value % period;
}
