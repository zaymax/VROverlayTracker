#nullable enable
using System.Text.Json;

namespace ExfilZoneTracker;

public sealed class ChecklistEntry
{
    public string ItemId { get; set; } = "";
    public bool Found { get; set; }
}

/// <summary>
/// The user's active checklist: which items they are hunting and what is already found.
/// Persisted to checklist.json next to the executable; entries reference the item
/// database by id. Adding/removing items is done by editing that file (MVP scope);
/// both it and the item database are watched and hot-reloaded, so edits show up on
/// the wrist panel without restarting the app.
/// </summary>
public sealed class ChecklistData : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _checklistPath;
    private readonly string _databasePath;
    private readonly List<FileSystemWatcher> _watchers = new();

    private ItemDatabase _database;
    private List<ChecklistEntry> _entries = new();
    private volatile bool _filesChanged;

    private ChecklistData(string checklistPath, string databasePath, ItemDatabase database)
    {
        _checklistPath = checklistPath;
        _databasePath = databasePath;
        _database = database;
    }

    public IReadOnlyList<ChecklistEntry> Entries => _entries;

    public (int Found, int Total) Progress => (_entries.Count(e => e.Found), _entries.Count);

    public static ChecklistData LoadOrCreate(string checklistPath, string databasePath)
    {
        var data = new ChecklistData(checklistPath, databasePath, ItemDatabase.Load(databasePath));
        data.LoadEntries();
        data.StartWatching();
        return data;
    }

    public string DisplayName(ChecklistEntry entry) =>
        _database.Find(entry.ItemId)?.Name ?? $"{entry.ItemId} (not in database)";

    public void ToggleFound(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return;
        _entries[index].Found = !_entries[index].Found;
        Save();
    }

    /// <summary>True once after any watched file changed; reloads state as a side effect.</summary>
    public bool ConsumeFileChanges()
    {
        if (!_filesChanged)
            return false;
        _filesChanged = false;
        try
        {
            _database = ItemDatabase.Load(_databasePath);
            LoadEntries();
            Console.WriteLine("Checklist / item database reloaded from disk.");
        }
        catch (Exception ex)
        {
            // Likely caught the editor mid-write; keep current state, the next event retries.
            Console.Error.WriteLine($"warning: reload failed, keeping previous state: {ex.Message}");
        }
        return true;
    }

    private void LoadEntries()
    {
        if (!File.Exists(_checklistPath))
        {
            _entries = SampleEntries();
            Save();
            Console.WriteLine($"Created sample checklist: {_checklistPath}");
            return;
        }

        var file = JsonSerializer.Deserialize<ChecklistFile>(File.ReadAllText(_checklistPath), JsonOptions);
        _entries = file?.Entries?.Where(e => !string.IsNullOrWhiteSpace(e.ItemId)).ToList() ?? new List<ChecklistEntry>();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_checklistPath, JsonSerializer.Serialize(new ChecklistFile { Entries = _entries }, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not save checklist: {ex.Message}");
        }
    }

    private List<ChecklistEntry> SampleEntries()
    {
        // Seed from known database ids so a fresh install shows something meaningful.
        var sample = new List<ChecklistEntry>();
        foreach (var id in new[] { "meds-first-aid-kit", "elec-graphics-card", "quest-usb-drive" })
        {
            if (_database.Find(id) != null)
                sample.Add(new ChecklistEntry { ItemId = id });
        }
        return sample;
    }

    private void StartWatching()
    {
        Watch(_checklistPath);
        Watch(_databasePath);
    }

    private void Watch(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // Our own Save() also trips this; the redundant reload is harmless (same state).
        watcher.Changed += (_, _) => _filesChanged = true;
        watcher.Created += (_, _) => _filesChanged = true;
        watcher.Renamed += (_, _) => _filesChanged = true;
        _watchers.Add(watcher);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    private sealed class ChecklistFile
    {
        public List<ChecklistEntry>? Entries { get; set; }
    }
}
