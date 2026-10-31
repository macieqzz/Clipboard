using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipwise;

public enum ClipKind { Text, Image }

/// <summary>
/// One entry in the merged list: captured live (listener) or read from the
/// Windows clipboard history (Win+V). Win+V items carry their history item id.
/// </summary>
public class ClipItem
{
    public string Id { get; set; } = "";
    public ClipKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public bool IsPinned { get; set; }
    public long Size { get; set; }

    /// <summary>true = read from the Windows clipboard history API.</summary>
    public bool FromWinV { get; set; }

    /// <summary>Win+V history item id (for restore/delete); empty for live items.</summary>
    [JsonIgnore]
    public string? HistoryId { get; set; }

    [JsonIgnore]
    public BitmapSource? Image { get; set; }

    /// <summary>Persisted bytes for pinned images only.</summary>
    public byte[]? ImageBytes { get; set; }

    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (Kind == ClipKind.Image)
            {
                var img = Image;
                if (img != null)
                    return $"Image · {img.PixelWidth}×{img.PixelHeight} · {FormatSize(Size)}";
                return $"Image · {FormatSize(Size)}";
            }
            var t = Text.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 200 ? t[..200] + "…" : t;
        }
    }

    [JsonIgnore]
    public string TimestampLabel
    {
        get
        {
            var d = DateTimeOffset.Now - Timestamp;
            if (d.TotalSeconds < 60) return "now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays}d";
            return Timestamp.ToString("MMM d");
        }
    }

    [JsonIgnore]
    public bool IsImage => Kind == ClipKind.Image;

    [JsonIgnore]
    public string SourceLabel => FromWinV ? "Win+V" : "captured";

    public static string HashText(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text)))[..16];

    public static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024):0.#} MB";
    }

    public void BuildImage()
    {
        if (ImageBytes is not { Length: > 0 }) return;
        try
        {
            using var ms = new MemoryStream(ImageBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Image = bmp;
        }
        catch
        {
            // Corrupt or unsupported image bytes: show as plain text row.
            Kind = ClipKind.Text;
            Image = null;
        }
    }
}
