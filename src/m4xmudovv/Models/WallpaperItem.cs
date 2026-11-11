using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace m4xmudovv.Models;

public enum WallpaperType
{
    Image,
    Video
}

public sealed class WallpaperItem : INotifyPropertyChanged
{
    private bool _isActive;

    private string _title = string.Empty;

    /// <summary>Display name (renamable). Only the name in the library changes, never the file on disk.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            OnPropertyChanged();
        }
    }

    public string FilePath { get; set; } = string.Empty;
    private string _previewPath = string.Empty;
    public string PreviewPath
    {
        get => _previewPath;
        set
        {
            if (_previewPath == value) return;
            _previewPath = value;
            OnPropertyChanged();
        }
    }

    private ImageSource? _thumbnail;

    /// <summary>
    /// Decoded, frozen, card-sized thumbnail shown in the library grid. Runtime only (never persisted);
    /// <see cref="PreviewPath"/> persists the path of the cached thumbnail file instead.
    /// </summary>
    [JsonIgnore]
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public WallpaperType Type { get; init; }
    public string TypeLabel => Type == WallpaperType.Video ? "VIDEO" : "IMAGE";
    public string DurationLabel { get; init; } = string.Empty;

    private bool _isFavorite;

    /// <summary>Marked as a favorite. Persisted with the library entry.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
