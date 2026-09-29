namespace Puck.World;

// The converter-backed presentation values share these wire shapes. Explicit closed types retain native-AOT
// metadata without constructing generic types at runtime; document, registry and preparation walks use one map.
internal static class BindableValueShape {
    public static Type? KeysType(Type type) {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(BindableScalar) || type == typeof(BindableAngle)) { return typeof(WorldKeys<BindableScalar>); }
        if (type == typeof(BindableColor)) { return typeof(WorldKeys<BindableColor>); }
        if (type == typeof(BindableVector2)) { return typeof(WorldKeys<BindableVector2>); }
        if (type == typeof(BindableVector3)) { return typeof(WorldKeys<BindableVector3>); }
        if (type == typeof(BindableDirection)) { return typeof(WorldKeys<BindableDirection>); }
        return null;
    }

    public static int Components(Type type) => type == typeof(BindableVector2) ? 2
        : type == typeof(BindableVector3) || type == typeof(BindableDirection) ? 3 : 0;
}
