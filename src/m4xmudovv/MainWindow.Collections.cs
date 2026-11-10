using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using m4xmudovv.Models;
using m4xmudovv.Views;

namespace m4xmudovv;

/// <summary>
/// Page navigation plus the "collections" features: Favorites, Playlists and the wallpaper card menu
/// (Add to Favorites / Add to Playlist / Delete). Everything is stored through the existing
/// <see cref="Services.WallpaperLibraryStore"/> (library.json), never in a second storage.
/// </summary>
public partial class MainWindow
{
    private enum AppPage { Wallpapers, Favorites, Playlists, Settings, About }

    private readonly ObservableCollection<Playlist> _playlists = new();
    private readonly ObservableCollection<WallpaperItem> _favorites = new();           // live view of IsFavorite items (library order)
    private readonly ObservableCollection<WallpaperItem> _playlistWallpapers = new();  // wallpapers of the opened playlist
    private AppPage _page = AppPage.Wallpapers;
    private Playlist? _openPlaylist;
    private bool _favoritesDirty = true;   // _favorites must be rebuilt before it is shown

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ======================================================================================
    //  Navigation (every destination has its own page and its own active state)
    // ======================================================================================

    private void NavWallpapers_OnClick(object sender, RoutedEventArgs e) => ShowPage(AppPage.Wallpapers);
    private void NavFavorites_OnClick(object sender, RoutedEventArgs e) => ShowPage(AppPage.Favorites);

    private void NavPlaylists_OnClick(object sender, RoutedEventArgs e)
    {
        _openPlaylist = null;   // the sidebar entry always lands on the list of playlists
        ShowPage(AppPage.Playlists);
    }

    private void NavSettings_OnClick(object sender, RoutedEventArgs e) => ShowPage(AppPage.Settings);
    private void NavAbout_OnClick(object sender, RoutedEventArgs e) => ShowPage(AppPage.About);
    private void BrowseWallpapers_OnClick(object sender, RoutedEventArgs e) => ShowPage(AppPage.Wallpapers);

    private void ShowLibraryPage() => ShowPage(AppPage.Wallpapers);
    private void ShowSettingsPage() => ShowPage(AppPage.Settings);

    private void ShowPage(AppPage page)
    {
        StopHoverPreview();
        _page = page;
        var wide = page is AppPage.Settings or AppPage.About;   // these two use the full width and hide the details panel

        LibraryPage.Visibility = Vis(page == AppPage.Wallpapers);
        FavoritesPage.Visibility = Vis(page == AppPage.Favorites);
        PlaylistsPage.Visibility = Vis(page == AppPage.Playlists);
        SettingsPage.Visibility = Vis(page == AppPage.Settings);
        AboutPage.Visibility = Vis(page == AppPage.About);
        DetailsPage.Visibility = Vis(!wide);
        StatusBarPanel.Visibility = Vis(!wide);

        SetActive(NavWallpapersButton, page == AppPage.Wallpapers);
        SetActive(NavFavoritesButton, page == AppPage.Favorites);
        SetActive(NavPlaylistsButton, page == AppPage.Playlists);
        SetActive(NavSettingsButton, page == AppPage.Settings);
        SetActive(NavAboutButton, page == AppPage.About);

        switch (page)
        {
            case AppPage.Favorites: RefreshFavoritesView(); break;
            case AppPage.Playlists: RefreshPlaylistsView(); break;
            case AppPage.Settings: _settingsVm.Refresh(); break;
        }
        UpdateItemCountText();
    }

    private void UpdateItemCountText()
    {
        if (ItemCountText is null) return;
        ItemCountText.Text = _page switch
        {
            AppPage.Favorites => Plural(_wallpapers.Count(w => w.IsFavorite), "favorite"),
            AppPage.Playlists => _openPlaylist is null ? Plural(_playlists.Count, "playlist") : Plural(_playlistWallpapers.Count, "wallpaper"),
            _ => Plural(WallpaperItems?.Items.Count ?? _wallpapers.Count, "wallpaper")
        };
    }

    private void SaveLibrary()
    {
        if (!_libraryStore.Save(_wallpapers, _playlists))
            StatusText.Text = "Warning: the library could not be saved to disk";
    }

    /// <summary>Loads the playlists that were saved next to the wallpapers (called from LoadLibrary).</summary>
    private void LoadPlaylists(IEnumerable<Playlist> saved)
    {
        foreach (var playlist in saved)
        {
            if (string.IsNullOrWhiteSpace(playlist.Name)) continue;
            playlist.Items = playlist.Items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();   // never keep duplicates
            _playlists.Add(playlist);
        }
    }

    // ======================================================================================
    //  Favorites
    // ======================================================================================

    private void ToggleFavorite(WallpaperItem item)
    {
        item.IsFavorite = !item.IsFavorite;

        if (!_favoritesDirty)
        {
            if (!item.IsFavorite) _favorites.Remove(item);
            else if (!_favorites.Contains(item))
            {
                // keep library order: insert after the favorites that precede it in the library
                var index = 0;
                foreach (var wallpaper in _wallpapers)
                {
                    if (ReferenceEquals(wallpaper, item)) break;
                    if (wallpaper.IsFavorite) index++;
                }
                _favorites.Insert(Math.Min(index, _favorites.Count), item);
            }
        }

        SaveLibrary();
        UpdateFavoritesPageState();
        UpdateDetailsFavoriteGlyph();
        UpdateItemCountText();
        StatusText.Text = item.IsFavorite ? $"Added “{item.Title}” to Favorites" : $"Removed “{item.Title}” from Favorites";
    }

    private void RefreshFavoritesView()
    {
        if (_favoritesDirty)
        {
            _favorites.Clear();
            foreach (var wallpaper in _wallpapers.Where(w => w.IsFavorite)) _favorites.Add(wallpaper);
            _favoritesDirty = false;
        }
        if (!ReferenceEquals(FavoriteItems.ItemsSource, _favorites)) FavoriteItems.ItemsSource = _favorites;
        UpdateFavoritesPageState();
    }

    private void UpdateFavoritesPageState()
    {
        if (FavoritesPage is null || _favoritesDirty) return;
        FavoritesEmptyPanel.Visibility = Vis(_favorites.Count == 0);
        FavoritesSubtitleText.Text = _favorites.Count == 0
            ? "Wallpapers you marked as favorites."
            : $"{Plural(_favorites.Count, "wallpaper")} you marked as favorites.";
    }

    private void UpdateDetailsFavoriteGlyph()
    {
        if (DetailsFavoriteButton is null) return;
        // EB52 = filled heart, EB51 = outline heart
        DetailsFavoriteButton.Content = _selectedWallpaper?.IsFavorite == true ? "\uEB52" : "\uEB51";
    }

    private void DetailsFavorite_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper is null)
        {
            StatusText.Text = "Select a wallpaper first";
            return;
        }
        ToggleFavorite(_selectedWallpaper);
    }

    private void DetailsMore_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper is null)
        {
            StatusText.Text = "Select a wallpaper first";
            return;
        }
        ShowWallpaperMenu(_selectedWallpaper, DetailsMoreButton);
    }

    // ======================================================================================
    //  Wallpaper card menu: Add to Favorites / Add to Playlist / Delete
    // ======================================================================================

    private void CardMenu_OnClick(object sender, RoutedEventArgs e)
    {
        // Each card's button has that card's wallpaper as its DataContext, so every menu acts on the right item.
        if (sender is Button { DataContext: WallpaperItem item } button) ShowWallpaperMenu(item, button);
    }

    private void ShowWallpaperMenu(WallpaperItem item, FrameworkElement target)
    {
        StopHoverPreview();
        var menu = NewMenu(target);
        menu.Items.Add(NewMenuItem("Rename", "\uE70F", () => RenameWallpaper(item)));
        menu.Items.Add(NewMenuItem(item.IsFavorite ? "Remove from Favorites" : "Add to Favorites",
                                   item.IsFavorite ? "\uEB52" : "\uEB51", () => ToggleFavorite(item)));
        menu.Items.Add(NewMenuItem("Add to Playlist", "\uE710", () => AddToPlaylist(item)));
        menu.Items.Add(NewMenuItem("Delete", "\uE74D", () => DeleteWallpaper(item), danger: true));
        menu.IsOpen = true;
    }

    private void RenameWallpaper(WallpaperItem item)
    {
        var name = AppDialogWindow.Prompt(this, "Rename wallpaper", "Enter a new name. The file itself is not changed.", item.Title, "Rename",
            _ => null);
        if (name is null || name == item.Title) return;
        item.Title = name;
        if (ReferenceEquals(item, _selectedWallpaper)) SelectedTitleText.Text = name;
        SaveLibrary();
        StatusText.Text = $"Renamed to “{name}”";
    }

    private ContextMenu NewMenu(FrameworkElement target) => new()
    {
        Style = (Style)FindResource("CardContextMenu"),
        PlacementTarget = target,
        Placement = PlacementMode.Bottom,
        HorizontalOffset = target.ActualWidth - 218,   // right-align the menu under the three-dot button
        StaysOpen = false                              // closes on a click outside, on Esc, or after an action
    };

    private MenuItem NewMenuItem(string header, string glyph, Action action, bool danger = false)
    {
        var entry = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("CardMenuItem"),
            Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15 }
        };
        if (danger) entry.Tag = "Danger";
        // Run after the menu has closed so a dialog never opens on top of a closing popup.
        entry.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(action));
        return entry;
    }

    // ---------------- Add to Playlist ----------------

    private void AddToPlaylist(WallpaperItem item)
    {
        var choices = new ObservableCollection<DialogChoice>(_playlists.Select(playlist => new DialogChoice
        {
            Name = playlist.Name,
            Detail = playlist.CountText,
            Tag = playlist,
            IsChecked = playlist.Contains(item.FilePath)
        }));
        var message = _playlists.Count == 0
            ? $"You have no playlists yet. Create one to add “{item.Title}” to it."
            : $"Switch on the playlists “{item.Title}” should be in. Switching one off removes it from that playlist.";

        var saved = AppDialogWindow.Choose(this, "Add to Playlist", message, choices,
            owner => CreatePlaylistChoice(owner, choices), "Save");
        if (!saved) return;

        int added = 0, removed = 0;
        foreach (var choice in choices)
        {
            var playlist = choice.Tag as Playlist;
            if (choice.IsNew)
            {
                if (!choice.IsChecked) continue;   // created in the dialog but left off: do not create it
                playlist = new Playlist { Name = choice.Name };
                _playlists.Add(playlist);
            }
            if (playlist is null) continue;

            var contains = playlist.Contains(item.FilePath);
            if (choice.IsChecked && !contains)       // never a duplicate entry in the same playlist
            {
                playlist.Items.Add(item.FilePath);
                added++;
            }
            else if (!choice.IsChecked && contains)
            {
                playlist.Items.RemoveAll(path => SamePath(path, item.FilePath));
                removed++;
            }
            playlist.RaiseContentChanged();
        }

        SaveLibrary();
        AfterPlaylistsChanged();
        StatusText.Text = (added, removed) switch
        {
            (0, 0) => "No changes to playlists",
            (_, 0) => $"Added “{item.Title}” to {Plural(added, "playlist")}",
            (0, _) => $"Removed “{item.Title}” from {Plural(removed, "playlist")}",
            _ => $"Updated playlists for “{item.Title}”"
        };
    }

    private DialogChoice? CreatePlaylistChoice(Window owner, ObservableCollection<DialogChoice> choices)
    {
        var name = AppDialogWindow.Prompt(owner, "New playlist", "Give the playlist a name.", string.Empty, "Create",
            text => ValidatePlaylistName(text, null, choices.Select(c => c.Name)));
        return name is null ? null : new DialogChoice { Name = name, Detail = "New playlist" };
    }

    // ---------------- Delete ----------------

    private void DeleteWallpaper(WallpaperItem item)
    {
        var inPlaylists = _playlists.Count(playlist => playlist.Contains(item.FilePath));
        var scope = item.IsFavorite || inPlaylists > 0
            ? $" It will also be removed from {(item.IsFavorite ? "Favorites" : string.Empty)}{(item.IsFavorite && inPlaylists > 0 ? " and " : string.Empty)}{(inPlaylists > 0 ? Plural(inPlaylists, "playlist") : string.Empty)}."
            : string.Empty;
        var message = $"“{item.Title}” will be removed from your m4xmudovv library.{scope}\n\nThe original file on your disk is not deleted.";
        if (!AppDialogWindow.Confirm(this, "Delete wallpaper?", message, "Delete")) return;

        StopHoverPreview();
        var wasSelected = ReferenceEquals(_selectedWallpaper, item);

        _wallpapers.Remove(item);
        _favorites.Remove(item);
        _playlistWallpapers.Remove(item);
        foreach (var playlist in _playlists)
        {
            if (playlist.Items.RemoveAll(path => SamePath(path, item.FilePath)) > 0) playlist.RaiseContentChanged();
        }
        if (wasSelected) ClearSelection();
        if (SamePath(_settings.LastSelectedPath ?? string.Empty, item.FilePath))
        {
            _settings.LastSelectedPath = null;
            _settingsService.ScheduleSave();
        }

        SaveLibrary();                 // persisted immediately: a deleted wallpaper must not come back after a restart
        RefreshLibraryView();          // also refreshes a filtered / searched grid and the empty state
        UpdateFavoritesPageState();
        AfterPlaylistsChanged();
        UpdateItemCountText();
        StatusText.Text = $"Removed “{item.Title}” from the library";
    }

    /// <summary>Back to the "nothing selected" state of the details panel.</summary>
    private void ClearSelection()
    {
        _selectedWallpaper = null;
        _resumeWhenUnsuspended = false;
        foreach (var wallpaper in _wallpapers) wallpaper.IsActive = false;
        StopPreview(true);
        try { PreviewMedia.Close(); } catch (InvalidOperationException) { }
        PreviewMedia.Source = null;
        PreviewImage.Source = null;
        PreviewMedia.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewControls.Visibility = Visibility.Collapsed;
        PreviewEmptyText.Text = "Select a wallpaper to preview";
        PreviewEmptyText.Visibility = Visibility.Visible;
        SelectedTitleText.Text = "No wallpaper selected";
        SelectedTypeText.Text = "Import a wallpaper to begin";
        SelectedPathText.Text = "—";
        ResolutionText.Text = "—";
        DurationText.Text = "—";
        FileSizeText.Text = "—";
        UpdateDetailsFavoriteGlyph();
    }

    // ======================================================================================
    //  Playlists page
    // ======================================================================================

    private void RefreshPlaylistsView()
    {
        if (_openPlaylist is not null && !_playlists.Contains(_openPlaylist)) _openPlaylist = null;
        if (!ReferenceEquals(PlaylistItems.ItemsSource, _playlists)) PlaylistItems.ItemsSource = _playlists;
        UpdatePlaylistCovers();

        var detail = _openPlaylist is not null;
        PlaylistListHeader.Visibility = Vis(!detail);
        PlaylistDetailHeader.Visibility = Vis(detail);
        PlaylistDetailActions.Visibility = Vis(detail);
        NewPlaylistButton.Visibility = Vis(!detail);
        PlaylistListView.Visibility = Vis(!detail);
        PlaylistDetailView.Visibility = Vis(detail);

        if (detail)
        {
            RebuildOpenPlaylist();
            PlaylistTitleText.Text = _openPlaylist!.Name;
            PlaylistDetailSubtitleText.Text = _openPlaylist.CountText;
            PlaylistDetailEmptyPanel.Visibility = Vis(_playlistWallpapers.Count == 0);
        }
        else
        {
            PlaylistsEmptyPanel.Visibility = Vis(_playlists.Count == 0);
            PlaylistsSubtitleText.Text = _playlists.Count == 0 ? "Collections of your wallpapers." : $"{Plural(_playlists.Count, "playlist")} of your wallpapers.";
        }
        UpdateItemCountText();
    }

    private void RebuildOpenPlaylist()
    {
        if (_openPlaylist is null) return;
        var byPath = new Dictionary<string, WallpaperItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var wallpaper in _wallpapers) byPath[wallpaper.FilePath] = wallpaper;
        _playlistWallpapers.Clear();
        foreach (var path in _openPlaylist.Items)
            if (byPath.TryGetValue(path, out var wallpaper)) _playlistWallpapers.Add(wallpaper);
        if (!ReferenceEquals(PlaylistWallpaperItems.ItemsSource, _playlistWallpapers)) PlaylistWallpaperItems.ItemsSource = _playlistWallpapers;
    }

    private void UpdatePlaylistCovers()
    {
        foreach (var playlist in _playlists)
        {
            ImageSource? cover = null;
            foreach (var path in playlist.Items)
            {
                var wallpaper = _wallpapers.FirstOrDefault(w => SamePath(w.FilePath, path));
                if (wallpaper?.Thumbnail is { } thumbnail)
                {
                    cover = thumbnail;
                    break;
                }
            }
            playlist.Cover = cover;
        }
    }

    /// <summary>Refreshes whatever shows playlists after they (or their contents) changed.</summary>
    private void AfterPlaylistsChanged()
    {
        UpdatePlaylistCovers();
        if (_page == AppPage.Playlists) RefreshPlaylistsView();
    }

    private void OpenPlaylist(Playlist playlist)
    {
        _openPlaylist = playlist;
        RefreshPlaylistsView();
    }

    private void PlaylistCard_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Playlist playlist }) OpenPlaylist(playlist);
    }

    private void PlaylistBack_OnClick(object sender, RoutedEventArgs e)
    {
        StopHoverPreview();
        _openPlaylist = null;
        RefreshPlaylistsView();
    }

    private void PlaylistMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Playlist playlist } button) return;
        var menu = NewMenu(button);
        menu.Items.Add(NewMenuItem("Open", "\uE8A7", () => OpenPlaylist(playlist)));
        menu.Items.Add(NewMenuItem("Rename", "\uE70F", () => RenamePlaylist(playlist)));
        menu.Items.Add(NewMenuItem("Delete", "\uE74D", () => DeletePlaylist(playlist), danger: true));
        menu.IsOpen = true;
    }

    private void NewPlaylist_OnClick(object sender, RoutedEventArgs e)
    {
        var name = AppDialogWindow.Prompt(this, "New playlist", "Give the playlist a name.", string.Empty, "Create",
            text => ValidatePlaylistName(text, null, Enumerable.Empty<string>()));
        if (name is null) return;
        var playlist = new Playlist { Name = name };
        _playlists.Add(playlist);
        SaveLibrary();
        if (_page != AppPage.Playlists) ShowPage(AppPage.Playlists); else RefreshPlaylistsView();
        StatusText.Text = $"Created playlist “{name}”";
    }

    private void PlaylistRename_OnClick(object sender, RoutedEventArgs e)
    {
        if (_openPlaylist is not null) RenamePlaylist(_openPlaylist);
    }

    private void PlaylistDelete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_openPlaylist is not null) DeletePlaylist(_openPlaylist);
    }

    private void RenamePlaylist(Playlist playlist)
    {
        var name = AppDialogWindow.Prompt(this, "Rename playlist", "Enter a new name for this playlist.", playlist.Name, "Rename",
            text => ValidatePlaylistName(text, playlist, Enumerable.Empty<string>()));
        if (name is null || name == playlist.Name) return;
        playlist.Name = name;
        SaveLibrary();
        if (_page == AppPage.Playlists) RefreshPlaylistsView();
        StatusText.Text = $"Renamed playlist to “{name}”";
    }

    private void DeletePlaylist(Playlist playlist)
    {
        var message = $"“{playlist.Name}” will be deleted. The wallpapers in it stay in your library.";
        if (!AppDialogWindow.Confirm(this, "Delete playlist?", message, "Delete")) return;
        _playlists.Remove(playlist);
        if (ReferenceEquals(_openPlaylist, playlist)) _openPlaylist = null;
        SaveLibrary();
        if (_page == AppPage.Playlists) RefreshPlaylistsView();
        StatusText.Text = $"Deleted playlist “{playlist.Name}”";
    }

    /// <summary>Returns an error message, or null when the name is acceptable.</summary>
    private string? ValidatePlaylistName(string name, Playlist? ignore, IEnumerable<string> pendingNames)
    {
        if (name.Length > 60) return "Use at most 60 characters.";
        var taken = _playlists.Where(p => !ReferenceEquals(p, ignore)).Select(p => p.Name).Concat(pendingNames);
        return taken.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            ? "A playlist with this name already exists."
            : null;
    }
}
