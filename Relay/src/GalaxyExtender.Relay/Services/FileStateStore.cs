using System.Text.Json;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// <see cref="IStateStore"/> backed by a single JSON file under <c>App_Data</c>.
///
/// The pool runs ONE worker process (confirmed 2026-08-05, /health process.id stable), so a plain
/// in-process lock suffices and the in-memory copy is authoritative: the file is read once at
/// startup and written through on every mutation. If the pool ever gains workers, this class needs
/// a <c>Global\</c> named mutex and a read-before-every-mutate — nothing outside it should change.
///
/// Writes are atomic: serialise to a temp file, then <see cref="File.Move(string,string,bool)"/>
/// over the target, so a recycle mid-write can never leave a half-written document.
/// </summary>
public sealed class FileStateStore : IStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<FileStateStore> _logger;

    private RelayState? _state;

    public FileStateStore(
        IWebHostEnvironment environment,
        IOptions<RelayOptions> options,
        ILogger<FileStateStore> logger)
    {
        _logger = logger;
        _path = options.Value.StateFilePath
                ?? Path.Combine(environment.ContentRootPath, "App_Data", "relay-state.json");
    }

    public T Mutate<T>(Func<RelayState, T> action)
    {
        lock (_lock)
        {
            var state = LoadLocked();
            var result = action(state);
            PersistLocked(state);
            return result;
        }
    }

    public T Read<T>(Func<RelayState, T> action)
    {
        lock (_lock)
        {
            return action(LoadLocked());
        }
    }

    private RelayState LoadLocked()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            if (File.Exists(_path))
            {
                _state = JsonSerializer.Deserialize<RelayState>(File.ReadAllText(_path));
            }
        }
        catch (Exception ex)
        {
            // A corrupt state file must not take the relay down; losing the dedupe window and
            // outbox is the lesser harm — but say so loudly, because a non-empty outbox means
            // lines were dropped here.
            _logger.LogError(ex,
                "State file {Path} could not be read; starting with fresh state. " +
                "Any queued outbox entries it held are lost.", _path);
        }

        _state ??= new RelayState();
        return _state;
    }

    private void PersistLocked(RelayState state)
    {
        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, SerializerOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
