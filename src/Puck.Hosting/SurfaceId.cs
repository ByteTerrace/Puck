namespace Puck.Hosting;

public readonly record struct SurfaceId(Guid Value) {
    public static SurfaceId New() {
        return new SurfaceId(Value: Guid.NewGuid());
    }
    public override string ToString() {
        return $"surface/{Value:n}";
    }
}
