namespace NotebookApi.Infrastructure;

/// <summary>
/// Lets a background job act as one named user, for the length of one operation: the same query filters, the same per-user write gate, and the
/// same tenant header to the AI service as that user's own request would get. Web requests never set it, and it is restored when the operation ends.
/// Only the index reconciler calls <see cref="Begin"/>; anywhere else it would be a way to become someone else.
/// </summary>
public static class AmbientUser
{
    private static readonly AsyncLocal<Guid?> Value = new();

    public static Guid? Current => Value.Value;

    public static IDisposable Begin(Guid user)
    {
        var previous = Value.Value;
        Value.Value = user;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}
