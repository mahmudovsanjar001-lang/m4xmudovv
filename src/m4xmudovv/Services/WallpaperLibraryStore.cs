using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using m4xmudovv.Models;

namespace m4xmudovv.Services;

/// <summary>Everything persisted in library.json: the wallpapers (with their favorite flag) and the playlists.</summary>
public sealed class LibraryData
{
    public List<WallpaperItem> Wallpapers { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
}

/// <summary>
/// The single storage for the library. Older versions wrote library.json as a plain array of wallpapers;
/// that format is still read, and the file is rewritten in the new { wallpapers, playlists } shape on the next save.
/// </summary>
public sealed class WallpaperLibraryStore
{
    private readonly string _libraryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "m4xmudovv",
        "library.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LibraryData Load()
    {
        try
        {
            if (!File.Exists(_libraryFile)) return new LibraryData();
            var json = File.ReadAllText(_libraryFile);
            if (string.IsNullOrWhiteSpace(json)) return new LibraryData();

            // Legacy format: a bare array of wallpapers.
            if (json.TrimStart().StartsWith('['))
                return new LibraryData { Wallpapers = JsonSerializer.Deserialize<List<WallpaperItem>>(json, JsonOptions) ?? new() };

            return JsonSerializer.Deserialize<LibraryData>(json, JsonOptions) ?? new LibraryData();
        }
        catch (JsonException) { return new LibraryData(); }
        catch (IOException) { return new LibraryData(); }
        catch (UnauthorizedAccessException) { return new LibraryData(); }
    }

    public bool Save(IEnumerable<WallpaperItem> wallpapers, IEnumerable<Playlist> playlists)
    {
        try
        {
            var directory = Path.GetDirectoryName(_libraryFile)!;
            Directory.CreateDirectory(directory);
            var data = new LibraryData
            {
                Wallpapers = wallpapers.Select(item => new WallpaperItem
                {
                    Title = item.Title,
                    FilePath = item.FilePath,
                    PreviewPath = item.PreviewPath,
                    Type = item.Type,
                    DurationLabel = item.DurationLabel,
                    IsFavorite = item.IsFavorite
                }).ToList(),
                Playlists = playlists.Select(playlist => new Playlist
                {
                    Id = playlist.Id,
                    Name = playlist.Name,
                    Items = playlist.Items.ToList()
                }).ToList()
            };

            // Write to a temp file and move it over, so a crash can never leave a half-written library
            // (which would make deleted wallpapers, favorites or playlists reappear or vanish).
            var temp = _libraryFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(temp, _libraryFile, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
