using System;
using System.IO;
using System.Linq;
using m4xmudovv.Models;

namespace m4xmudovv.Services;

public sealed class WallpaperImportService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".wmv", ".mov", ".mkv"];

    public WallpaperItem CreateItem(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var type = VideoExtensions.Contains(extension) ? WallpaperType.Video : WallpaperType.Image;
        return new WallpaperItem
        {
            Title = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            // Cards must never decode the original full-resolution file; the thumbnail service fills this in.
            PreviewPath = string.Empty,
            Type = type,
            DurationLabel = type == WallpaperType.Video ? "LOCAL VIDEO" : "LOCAL IMAGE"
        };
    }

    /// <summary>A wallpaper that ships with the app (Wallpapers folder next to m4xmudovv.exe).</summary>
    public WallpaperItem CreateBundledItem(string path)
    {
        var item = CreateItem(path);
        return new WallpaperItem
        {
            Title = item.Title,
            FilePath = item.FilePath,
            PreviewPath = string.Empty,
            Type = item.Type,
            DurationLabel = item.Type == WallpaperType.Video ? "BUNDLED LIVE" : "BUNDLED IMAGE"
        };
    }

    /// <summary>The supported media files shipped in the app's Wallpapers folder (empty if the folder is missing).</summary>
    public static string[] FindBundledFiles()
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Wallpapers");
            if (!Directory.Exists(folder)) return [];
            var service = new WallpaperImportService();
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(service.IsSupported)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception) { return []; }
    }

    public bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension);
    }
}
