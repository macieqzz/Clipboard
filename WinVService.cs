using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Clipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipwise;

/// <summary>
/// Reads the real Windows clipboard history (the same store Win+V shows) and
/// restores/deletes items in it. Requires: clipboard history enabled in Windows
/// Settings, the Win10 manifest GUID (in app.manifest), and a non-elevated process.
/// </summary>
public static class WinVService
{
    // Access-denied (missing manifest GUID / elevated / history off) lands here.
    public static string? LastError { get; private set; }
    public static bool IsSupported { get; private set; } = true;
    public static int LastItemCount { get; private set; }

    /// <summary>Merge the current Win+V history into the store. Call on a UI thread.</summary>
    public static async Task RefreshAsync(Store store)
    {
        try
        {
            if (!Clipboard.IsHistoryEnabled())
            {
                LastError = "Win+V history is off. Enable it in Settings > System > Clipboard.";
                IsSupported = false;
                return;
            }
            IsSupported = true;

            var result = await Clipboard.GetHistoryItemsAsync();
            if (result.Status != ClipboardHistoryItemsResultStatus.Success)
            {
                LastError = "Win+V history unavailable: " + result.Status;
                return;
            }

            LastItemCount = result.Items.Count;
            LastError = null;

            foreach (var item in result.Items)
            {
                var clip = await ToClipItemAsync(item);
                if (clip != null)
                {
                    clip.FromWinV = true;
                    clip.HistoryId = item.Id;
                    store.Upsert(clip);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = "Win+V API failed: " + ex.Message;
            IsSupported = false;
        }
    }

    private static async Task<ClipItem?> ToClipItemAsync(ClipboardHistoryItem item)
    {
        var content = item.Content;
        try
        {
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return null;
                return new ClipItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = ClipKind.Text,
                    Text = text,
                    Hash = ClipItem.HashText(text),
                    Timestamp = item.Timestamp,
                    Size = Encoding.UTF8.GetByteCount(text),
                };
            }

            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var refStream = await content.GetBitmapAsync();
                var stream = await refStream.OpenReadAsync();
                byte[] bytes = await ReadAllAsync(stream);

                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                return new ClipItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = ClipKind.Image,
                    Hash = ClipItem.HashBytes(bytes),
                    Timestamp = item.Timestamp,
                    Size = bytes.Length,
                    Image = bmp,
                    ImageBytes = bytes,
                };
            }

            return null;
        }
        catch
        {
            return null; // Format present but content not readable (e.g. custom format).
        }
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = stream.Size;
        if (size == 0) return Array.Empty<byte>();
        uint read = await reader.LoadAsync((uint)size);
        var bytes = new byte[read];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Put a Win+V item back into the clipboard (same as clicking it in Win+V).</summary>
    public static async Task<bool> RestoreAsync(ClipItem clip)
    {
        try
        {
            if (clip.HistoryId == null || !IsSupported) return false;
            var result = await Clipboard.GetHistoryItemsAsync();
            if (result.Status != ClipboardHistoryItemsResultStatus.Success) return false;
            foreach (var item in result.Items)
                if (item.Id == clip.HistoryId)
                    return Clipboard.SetHistoryItemAsContent(item) == SetHistoryItemAsContentStatus.Success;
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> DeleteAsync(ClipItem clip)
    {
        try
        {
            if (clip.HistoryId == null || !IsSupported) return false;
            var result = await Clipboard.GetHistoryItemsAsync();
            if (result.Status != ClipboardHistoryItemsResultStatus.Success) return false;
            foreach (var item in result.Items)
                if (item.Id == clip.HistoryId)
                    return Clipboard.DeleteItemFromHistory(item);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fire when the OS clipboard history changes (Win+V adds/removes items).</summary>
    public static void SubscribeToHistoryChanged(Action handler)
    {
        try { Clipboard.HistoryChanged += (_, _) => handler(); }
        catch { /* event unsupported in this environment */ }
    }

    public static bool HasHistoryEnabled() => Clipboard.IsHistoryEnabled();
}
