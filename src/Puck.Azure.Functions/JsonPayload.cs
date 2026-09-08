namespace Puck.Azure.Functions;

public class JsonPayload<T>
{
    public T? Value { get; set; }
}

public static class JsonPayload
{
    public static JsonPayload<T> Create<T>(T value) => new() { Value = value, };
}

