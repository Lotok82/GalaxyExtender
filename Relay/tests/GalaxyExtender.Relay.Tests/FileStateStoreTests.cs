using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The property that matters on shared hosting: state survives the process. A "recycle" here is
/// simply a second store instance reading the same file.
/// </summary>
public sealed class FileStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"relay-statestore-test-{Guid.NewGuid():N}.json");

    private FileStateStore CreateStore() => new(
        environment: null!, // never dereferenced when StateFilePath is set
        Microsoft.Extensions.Options.Options.Create(new RelayOptions { StateFilePath = _path }),
        NullLogger<FileStateStore>.Instance);

    [Fact]
    public void State_survives_a_simulated_recycle()
    {
        var store = CreateStore();

        store.Mutate<object?>(state =>
        {
            state.Dedupe.Add(new DedupeEntry
            {
                Key = "abc:1",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                FirstSeenBy = "kaelen"
            });
            state.Outbox.Add(new OutboxEntry { Payload = "{}", LineCount = 1 });
            return null;
        });

        // "Recycle": a brand-new store instance over the same file.
        var reloaded = CreateStore();

        var (dedupeKey, outboxCount) = reloaded.Read(state =>
            (state.Dedupe.Single().Key, state.Outbox.Count));

        Assert.Equal("abc:1", dedupeKey);
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public void A_corrupt_state_file_starts_fresh_instead_of_crashing()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = CreateStore();

        Assert.Equal(0, store.Read(state => state.Dedupe.Count));

        // And the store must be able to persist over the corrupt file.
        store.Mutate<object?>(state =>
        {
            state.Stage2Cursor = "42";
            return null;
        });

        Assert.Equal("42", CreateStore().Read(state => state.Stage2Cursor));
    }

    [Fact]
    public void A_missing_file_is_a_valid_empty_state()
    {
        Assert.Equal(0, CreateStore().Read(state => state.Batches.Count));
        Assert.False(File.Exists(_path), "a pure read must not create the file");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
