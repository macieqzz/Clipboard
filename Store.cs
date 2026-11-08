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
            Dedupe();
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

    /// <summary>
    /// Add or refresh a clip. No duplicates: copying content that is already in the
    /// list discards the previous row and keeps only the fresh copy, on top.
    /// Returns true when the clip is new or was just refreshed.
    /// </summary>
    public bool Upsert(ClipItem clip)
    {
        var existing = FindByHash(clip.Hash);
        if (existing == null)
        {
            InsertSorted(clip);
            Trim();
            return true;
        }

        bool fresher = clip.Timestamp >= existing.Timestamp;
        if (!fresher)
        {
            // Same content resurfacing from the Win+V history: merge metadata in place
            // without disturbing the position of the row we already have.
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
            return false;
        }

        // Copied again: drop the old row and re-insert the fresh copy so there is
        // only ever one row per distinct clip, with the newest one at the top.
        Items.Remove(existing);
        clip.IsPinned = existing.IsPinned;
        if (!clip.FromWinV)
        {
            // Keep the Win+V link the old row had (restore/delete still work).
            clip.FromWinV = existing.FromWinV;
            clip.HistoryId = existing.HistoryId;
        }
        if (clip.Image is null && existing.Image is not null)
        {
            clip.Image = existing.Image;
            clip.ImageBytes = existing.ImageBytes;
        }

        // Older duplicates from a previously saved store must not survive either.
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].Hash == clip.Hash)
                Items.RemoveAt(i);

        InsertSorted(clip);
        return true;
    }

    /// <summary>Collapse duplicate rows (same hash) left in an older store, keeping
    /// the pin flag and the newest timestamp on the surviving row.</summary>
    private void Dedupe()
    {
        var seen = new Dictionary<string, ClipItem>();
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (string.IsNullOrEmpty(item.Hash))
            {
                item.Hash = item.Kind == ClipKind.Image && item.ImageBytes is { Length: > 0 }
                    ? ClipItem.HashBytes(item.ImageBytes)
                    : ClipItem.HashText(item.Text);
            }

            if (!seen.TryGetValue(item.Hash, out var keeper))
            {
                seen[item.Hash] = item;
                continue;
            }

            if (item.IsPinned) keeper.IsPinned = true;
            if (item.Timestamp > keeper.Timestamp) keeper.Timestamp = item.Timestamp;
            if (keeper.Image is null && item.Image is not null)
            {
                keeper.Image = item.Image;
                keeper.ImageBytes = item.ImageBytes ?? keeper.ImageBytes;
            }
            Items.RemoveAt(i--);
        }

        // Merging may have changed timestamps/pins; restore pinned-first, newest-first.
        var ordered = Items.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp).ToList();
        for (int target = 0; target < ordered.Count; target++)
        {
            int current = Items.IndexOf(ordered[target]);
            if (current != target) Items.Move(current, target);
        }
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
