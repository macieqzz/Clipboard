using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Clipwise;

/// <summary>
/// In-memory list of clips + JSON persistence. Unpinned images stay memory-only;
/// pinned items (including images) keep their bytes on disk.
/// </summary>
public class Store
{
    public const int MaxUnpinned = 200;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipwise");

    private static readonly string File = Path.Combine(Dir, "store.json");

    public ObservableCollection<ClipItem> Items { get; } = new();

    public string? LastError { get; private set; }

    public void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            var json = System.IO.File.ReadAllText(File);
            var saved = JsonSerializer.Deserialize<List<ClipItem>>(json);
            if (saved == null) return;
            foreach (var item in saved)
            {
                item.BuildImage();
                Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            LastError = "Failed to load store: " + ex.Message;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Only persist pinned images; unpinned images are reconstructed by the listener.
            foreach (var item in Items)
                if (item.Kind == ClipKind.Image && !item.IsPinned)
                    item.ImageBytes = null;
            var json = JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = false });
            System.IO.File.WriteAllText(File, json);
        }
        catch (Exception ex)
        {
            LastError = "Failed to save store: " + ex.Message;
        }
    }

    public ClipItem? FindByHash(string hash) => Items.FirstOrDefault(i => i.Hash == hash);

    /// <summary>Add or refresh a clip; dedupes by hash. Returns true when a new row appeared.</summary>
    public bool Upsert(ClipItem clip)
    {
        var existing = FindByHash(clip.Hash);
        if (existing != null)
        {
            if (clip.FromWinV && !existing.FromWinV)
            {
                existing.FromWinV = true;
                existing.HistoryId = clip.HistoryId;
            }
            if (clip.Image is not null && existing.Image is null)
            {
                existing.Image = clip.Image;
                existing.ImageBytes = clip.ImageBytes;
            }
            if (clip.Timestamp > existing.Timestamp)
            {
                existing.Timestamp = clip.Timestamp;
                Items.Remove(existing);
                InsertSorted(existing);
            }
            return false;
        }

        InsertSorted(clip);
        Trim();
        return true;
    }

    /// <summary>Keep the list sorted: pinned first, then newest timestamp first
    /// (Win+V merge items must not leap over fresher captures).</summary>
    public void InsertSorted(ClipItem clip)
    {
        int i = 0;
        while (i < Items.Count && ShouldPrecede(Items[i], clip)) i++;
        Items.Insert(i, clip);
    }

    private static bool ShouldPrecede(ClipItem a, ClipItem b)
    {
        if (a.IsPinned != b.IsPinned) return a.IsPinned;
        return a.Timestamp >= b.Timestamp;
    }

    private void Trim()
    {
        var unpinned = Items.Where(i => !i.IsPinned).OrderByDescending(i => i.Timestamp).ToList();
        int remove = unpinned.Count - MaxUnpinned;
        if (remove <= 0) return;
        var drop = unpinned.Skip(MaxUnpinned).ToHashSet();
        for (int i = Items.Count - 1; i >= 0; i--)
            if (drop.Contains(Items[i]))
                Items.RemoveAt(i);
    }

    public void TogglePin(ClipItem item)
    {
        item.IsPinned = !item.IsPinned;
        Save();
    }

    public void Remove(ClipItem item) => Items.Remove(item);

    public ClipItem? GetByHistoryId(string id) => Items.FirstOrDefault(i => i.HistoryId == id);
}
