namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Serialised access to the durable <see cref="RelayState"/>.
///
/// The seam exists so the file implementation can be swapped (SQLite if volume outgrows it, or a
/// mutex-guarded variant if the app pool ever gains a second worker process) without touching any
/// caller. See discord-relay-plan.md — the single-worker finding is what allows the current
/// in-process-lock implementation.
/// </summary>
public interface IStateStore
{
    /// <summary>Run <paramref name="action"/> against the state and persist the result.</summary>
    T Mutate<T>(Func<RelayState, T> action);

    /// <summary>Run a read-only <paramref name="action"/> against the state. Nothing is persisted.</summary>
    T Read<T>(Func<RelayState, T> action);
}
