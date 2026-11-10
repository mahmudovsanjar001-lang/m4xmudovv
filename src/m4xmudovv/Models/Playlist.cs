using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace m4xmudovv.Models;

/// <summary>
/// A named, ordered list of library wallpapers. Wallpapers are referenced by file path (the library's identity key),
/// so a playlist never owns or copies a file. Persisted inside library.json together with the wallpapers.
/// </summary>
public sealed class Playlist : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private ImageSource? _cover;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>File paths of the wallpapers in this playlist, in the order they were added.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>Thumbnail of the first wallpaper, shown on the playlist card. Runtime only.</summary>
    [JsonIgnore]
    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value)) return;
            _cover = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string CountText => Items.Count == 1 ? "1 wallpaper" : $"{Items.Count} wallpapers";

    public bool Contains(string filePath) =>
        Items.Exists(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Call after changing <see cref="Items"/> so bound UI refreshes.</summary>
    public void RaiseContentChanged() => OnPropertyChanged(nameof(CountText));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
