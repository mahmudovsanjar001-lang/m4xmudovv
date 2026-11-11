using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using m4xmudovv.Models;
using m4xmudovv.Services;
using m4xmudovv.ViewModels;
using m4xmudovv.Views;

namespace m4xmudovv;

public partial class MainWindow : Window
{
    private readonly WallpaperImportService _importService = new();
    private readonly WallpaperLibraryStore _libraryStore = new();
    private readonly WallpaperThumbnailService _thumbnailService = new();
    private readonly DesktopWallpaperHost _wallpaperHost = new();
    private readonly ObservableCollection<WallpaperItem> _wallpapers = new();
    private readonly DispatcherTimer _playbackTimer;
    private WallpaperItem? _selectedWallpaper;
    private readonly DispatcherTimer _hoverDelayTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private FrameworkElement? _hoverCandidate;   // card the pointer just entered (waiting for the hover delay)
    private FrameworkElement? _hoverCard;        // card that currently hosts the one active hover preview
    private Panel? _hoverHost;
    private MediaElement? _hoverVideo;
    private bool _isSeeking;
    private bool _isPlaying;

    // ---- settings / state ----
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly ThemeService _themeService;
    private readonly SettingsViewModel _settingsVm;
    private readonly TrayService _tray = new();
    private readonly DispatcherTimer _idleTimer;
    private readonly HashSet<string> _suspendReasons = new();   // why the details preview is currently paused
    private LibraryCategory _category = LibraryCategory.All;
    private bool _syncing = true;            // true while defaults are pushed into controls: do not write them back to settings
    private bool _exiting;
    private bool _cleanedUp;
    private bool _boundsReady;               // the window has been shown at least once
    private bool _inTray;                    // hidden to the tray
    private bool _inIdleMode;
    private bool _previewReleased;           // idle mode released the preview's media / image
    private bool _resumeWhenUnsuspended;     // the preview was playing when it was suspended
    private TimeSpan? _pendingSeek;          // position to return to after the preview was reloaded
    private readonly bool _startedFromWindows;
    private bool _restoredAppliedWallpaper;

    public MainWindow(SettingsService settingsService, ThemeService themeService, bool startedFromWindows = false)
    {
        // Settings must exist before InitializeComponent: controls raise events while the XAML loads.
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        _themeService = themeService;
        _startedFromWindows = startedFromWindows;
        _thumbnailService.Quality = _settings.ThumbnailQuality;
        _settingsVm = new SettingsViewModel(_settings, _settingsService, _thumbnailService, GetLibraryStats);

        InitializeComponent();
        SettingsPage.DataContext = _settingsVm;
        AboutPage.DataContext = _settingsVm;
        AppLog.Info($"m4xmudovv started (pid {Environment.ProcessId}). Log file: {AppLog.LogPath}");
        RestoreWindowBounds();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += PlaybackTimer_OnTick;
        PreviewFrame.SizeChanged += (_, e) =>
            PreviewFrame.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 10, 10);
        _hoverDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.HoverPreviewDelayMs)) };
        _hoverDelayTimer.Tick += HoverDelayTimer_OnTick;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _idleTimer.Tick += (_, _) => EnterIdleMode();

        // Video wallpaper windows are extra top-level windows; the app must still exit when the main window closes.
        Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        Closed += (_, _) => Cleanup();
        Activated += (_, _) =>
        {
            UpdateBackgroundState();
            if (_page == AppPage.Settings) _settingsVm.RefreshStartupState();   // it may have been changed in Task Manager meanwhile
        };
        Deactivated += (_, _) => { StopHoverPreview(); UpdateBackgroundState(); };
        StateChanged += (_, _) => { StopHoverPreview(); UpdateBackgroundState(); CaptureWindowBounds(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) _boundsReady = true; UpdateBackgroundState(); };
        LocationChanged += (_, _) => CaptureWindowBounds();
        SizeChanged += (_, _) => CaptureWindowBounds();

        _settingsVm.SettingChanged += OnSettingChanged;
        _settingsVm.ErrorRaised += message => MessageBox.Show(this, message, "m4xmudovv", MessageBoxButton.OK, MessageBoxImage.Warning);
        _tray.OpenRequested += () => Dispatcher.BeginInvoke(new Action(ShowFromTray));
        _tray.ContextRequested += (x, y) => Dispatcher.BeginInvoke(new Action(() => ShowTrayMenu(x, y)));
        _tray.SettingsRequested += () => Dispatcher.BeginInvoke(new Action(() => { ShowFromTray(); ShowSettingsPage(); }));
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(new Action(ExitApplication));
        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

        ApplyPlaybackDefaults();
        LoadLibrary();
        _category = _settings.RememberLastCategory ? _settings.LastCategory : LibraryCategory.All;
        UpdateCategoryChips();
        RefreshLibraryView();
        UpdateTrayVisibility();
        _syncing = false;
        UpdateBackgroundState();
        Loaded += MainWindow_OnLoaded;
    }

    private void LoadLibrary()
    {
        var library = _libraryStore.Load();
        LoadPlaylists(library.Playlists);
        foreach (var wallpaper in library.Wallpapers)
        {
            // The persisted value may be a stale path (older builds stored the original image path here).
            // It is refreshed from the thumbnail cache once the window has loaded.
            wallpaper.PreviewPath = string.Empty;
            _wallpapers.Add(wallpaper);
        }
        AddBundledWallpapers();
    }

    private const int BundledWallpaperSetVersion = 2;

    /// <summary>First run (or a newer bundled set): adds the wallpapers that ship with the app to the library, once.</summary>
    private void AddBundledWallpapers()
    {
        if (_settings.BundledWallpaperSet >= BundledWallpaperSetVersion) return;

        // Drop bundled entries from an older set whose files no longer ship with the app.
        for (var i = _wallpapers.Count - 1; i >= 0; i--)
        {
            if (_wallpapers[i].DurationLabel.StartsWith("BUNDLED", StringComparison.Ordinal) && !File.Exists(_wallpapers[i].FilePath))
                _wallpapers.RemoveAt(i);
        }

        var files = WallpaperImportService.FindBundledFiles();
        if (files.Length == 0) return;   // nothing shipped (yet): try again next launch instead of marking the set as done

        var known = new HashSet<string>(_wallpapers.Select(w => w.FilePath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in files)
        {
            if (known.Contains(path)) continue;
            _wallpapers.Add(_importService.CreateBundledItem(path));
            added++;
        }
        _settings.BundledWallpaperSet = BundledWallpaperSetVersion;
        _settingsService.ScheduleSave();
        SaveLibrary();
        AppLog.Info($"Bundled wallpapers added to the library: {added}");
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_OnLoaded;
        if (_wallpapers.Count == 0) return;
        // The desktop wallpaper comes first: it does not need thumbnails, so it must not wait for them.
        var restoreTask = RestoreLastAppliedWallpaperAsync();
        await PrepareLibraryUiAsync();
        await restoreTask;
        if (!_lifetime.IsCancellationRequested) StatusText.Text = "Ready to preview";
    }

    private bool _libraryUiPrepared;

    /// <summary>Selection + thumbnails: only needed once the window is actually shown.</summary>
    private async Task PrepareLibraryUiAsync()
    {
        if (_libraryUiPrepared || _wallpapers.Count == 0) return;
        _libraryUiPrepared = true;
        RestoreSelection();
        StatusText.Text = "Preparing wallpaper thumbnails…";
        // Snapshot: the collection can change (imports) while thumbnails are being prepared.
        await PrepareThumbnailsAsync(_wallpapers.ToList());
        if (_lifetime.IsCancellationRequested) return;
        SaveLibrary();
        UpdateCount();
    }

    private async void ImportMedia_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import wallpaper media", Multiselect = true, Filter = "Supported media|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.webm;*.wmv;*.mov;*.mkv|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var importedItems = new List<WallpaperItem>();
        var skipped = 0;
        foreach (var path in dialog.FileNames)
        {
            if (!_importService.IsSupported(path)) { skipped++; continue; }
            var item = _importService.CreateItem(path);
            // Let the user name it right away (Cancel keeps the file name).
            var position = dialog.FileNames.Length > 1 ? $" ({importedItems.Count + skipped + 1} of {dialog.FileNames.Length})" : string.Empty;
            var chosen = AppDialogWindow.Prompt(this, "Name this wallpaper" + position, $"File: {Path.GetFileName(path)}\nCancel keeps the file name.",
                item.Title, "Import", _ => null);
            if (!string.IsNullOrWhiteSpace(chosen)) item.Title = chosen;
            _wallpapers.Add(item);
            importedItems.Add(item);
        }
        SaveLibrary();          // persist the import immediately; thumbnails follow in the background
        RefreshLibraryView();
        StatusText.Text = "Generating thumbnails…";
        await PrepareThumbnailsAsync(importedItems);
        var imported = importedItems.Count;
        SaveLibrary();
        if (_selectedWallpaper is null && _wallpapers.Count > 0) SelectWallpaper(_wallpapers[0]);
        UpdateCount();
        StatusText.Text = skipped == 0 ? $"Imported {imported} media item{(imported == 1 ? "" : "s")}" : $"Imported {imported}; skipped {skipped} unsupported item{(skipped == 1 ? "" : "s")}";
    }

    private async Task PrepareThumbnailsAsync(IReadOnlyList<WallpaperItem> items)
    {
        if (items.Count == 0) return;
        try
        {
            // The service throttles the actual work (image decode, shell/MediaPlayer capture, disk decode),
            // so starting every item here does not flood the CPU or the UI thread.
            await Task.WhenAll(items.Select(item => EnsureThumbnailAsync(item, _lifetime.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task EnsureThumbnailAsync(WallpaperItem item, CancellationToken cancellationToken)
    {
        try
        {
            var cachedPath = await _thumbnailService.GetOrCreateAsync(item, cancellationToken);
            if (string.IsNullOrWhiteSpace(cachedPath)) return;
            var image = await _thumbnailService.LoadThumbnailImageAsync(cachedPath, cancellationToken);
            if (image is null) return;
            item.Thumbnail = image;        // continuation resumes on the UI thread
            item.PreviewPath = cachedPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* a missing/unreadable file simply keeps its placeholder card */ }
    }

    private void WallpaperCard_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WallpaperItem item }) SelectWallpaper(item);
    }

    private void WallpaperCard_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WallpaperItem { Type: WallpaperType.Video } } card) return;
        if (!_settings.HoverPreviewEnabled) return;
        if (_settings.PausePreviewsWhenInactive && !IsActive) return;
        StopHoverPreview();                       // only one preview at a time
        _hoverCandidate = card;
        if (_settings.HoverPreviewDelayMs <= 0)
        {
            HoverDelayTimer_OnTick(null, EventArgs.Empty);   // "Instant"
            return;
        }
        _hoverDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverPreviewDelayMs);
        _hoverDelayTimer.Start();                 // short delay so sweeping across the grid does not open every video
    }

    private void WallpaperCard_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _hoverCandidate))
        {
            _hoverDelayTimer.Stop();
            _hoverCandidate = null;
        }
        if (ReferenceEquals(sender, _hoverCard)) StopHoverPreview();
    }

    private void HoverDelayTimer_OnTick(object? sender, EventArgs e)
    {
        _hoverDelayTimer.Stop();
        var card = _hoverCandidate;
        _hoverCandidate = null;
        if (card is null || !card.IsMouseOver || card.DataContext is not WallpaperItem item) return;
        StartHoverPreview(card, item);
    }

    private void StartHoverPreview(FrameworkElement card, WallpaperItem item)
    {
        StopHoverPreview();
        if (!File.Exists(item.FilePath)) return;
        var host = FindNamedChild<Panel>(card, "VideoHost");
        if (host is null) return;

        // The player is created on demand inside the hovered card only: no per-card MediaElement exists,
        // so a library of hundreds of videos costs nothing until one card is actually hovered.
        var video = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Volume = 0,
            IsMuted = true,
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false,
            Opacity = 0                           // thumbnail stays visible until the first frame is ready
        };
        video.MediaOpened += HoverVideo_OnMediaOpened;
        video.MediaFailed += HoverVideo_OnMediaFailed;
        video.MediaEnded += HoverVideo_OnMediaEnded;
        // Round the top corners to match the card; the extra height keeps the bottom edge square.
        host.Clip = new RectangleGeometry(new Rect(0, 0, host.ActualWidth, host.ActualHeight + 12), 8, 8);
        host.Children.Add(video);
        _hoverCard = card;
        _hoverHost = host;
        _hoverVideo = video;
        try
        {
            video.Source = new Uri(item.FilePath, UriKind.Absolute);
            video.Play();
        }
        catch (Exception) { StopHoverPreview(); }
    }

    private void StopHoverPreview()
    {
        _hoverDelayTimer.Stop();
        _hoverCandidate = null;
        var video = _hoverVideo;
        if (video is null) return;
        _hoverVideo = null;
        _hoverCard = null;
        video.MediaOpened -= HoverVideo_OnMediaOpened;
        video.MediaFailed -= HoverVideo_OnMediaFailed;
        video.MediaEnded -= HoverVideo_OnMediaEnded;
        try { video.Stop(); video.Close(); } catch (InvalidOperationException) { }   // releases the file handle
        _hoverHost?.Children.Remove(video);
        if (_hoverHost is not null) _hoverHost.Clip = null;       // removing the player restores the cached thumbnail underneath
        _hoverHost = null;
    }

    private void HoverVideo_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement video && ReferenceEquals(video, _hoverVideo)) video.Opacity = 1;
    }

    private void HoverVideo_OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is MediaElement video && ReferenceEquals(video, _hoverVideo)) StopHoverPreview();
    }

    private void HoverVideo_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement video || !ReferenceEquals(video, _hoverVideo)) return;
        if (!_settings.LoopVideoPreviews)
        {
            StopHoverPreview();                   // restores the thumbnail
            return;
        }
        video.Position = TimeSpan.Zero;
        video.Play();
    }

    private void Library_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0) StopHoverPreview();
    }

    private static T? FindNamedChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name) return match;
            var nested = FindNamedChild<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void SelectWallpaper(WallpaperItem item, bool autoPlay = true)
    {
        _selectedWallpaper = item;
        UpdateDetailsFavoriteGlyph();
        _resumeWhenUnsuspended = false;
        _settings.LastSelectedPath = item.FilePath;
        _settingsService.ScheduleSave();
        foreach (var wallpaper in _wallpapers) wallpaper.IsActive = ReferenceEquals(wallpaper, item);
        StopPreview(false);
        PreviewControls.Visibility = item.Type == WallpaperType.Video ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Text = "—";
        SelectedTitleText.Text = item.Title;
        SelectedTypeText.Text = item.Type == WallpaperType.Video ? "LOCAL VIDEO • ready for playback" : "LOCAL IMAGE • static preview";
        SelectedPathText.Text = item.FilePath;
        PreviewEmptyText.Visibility = Visibility.Collapsed;
        ResolutionText.Text = "—";
        FileSizeText.Text = "—";

        if (item.Type == WallpaperType.Image)
        {
            PreviewMedia.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            try
            {
                if (!File.Exists(item.FilePath)) throw new FileNotFoundException("The image file no longer exists.");
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(item.FilePath, UriKind.Absolute); image.EndInit();
                PreviewImage.Source = image;
                ResolutionText.Text = $"{image.PixelWidth} × {image.PixelHeight}";
                FileSizeText.Text = FormatFileSize(item.FilePath);
                StatusText.Text = $"Previewing {item.Title}";
            }
            catch (Exception ex) { ShowPreviewError(ex.Message); }
        }
        else
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewMedia.Visibility = Visibility.Visible;
            try
            {
                PreviewMedia.Source = new Uri(item.FilePath, UriKind.Absolute);
                PreviewMedia.Play();
                if (autoPlay && _suspendReasons.Count == 0)
                {
                    _playbackTimer.Start();
                    SetPlaying(true);
                }
                else
                {
                    // Paused (window hidden / inactive / idle): show the first frame and resume later if it was playing.
                    PreviewMedia.Pause();
                    _resumeWhenUnsuspended = autoPlay;
                }
                FileSizeText.Text = FormatFileSize(item.FilePath);
                StatusText.Text = $"Loading {item.Title}";
            }
            catch (Exception ex) { ShowPreviewError($"Unable to open video: {ex.Message}"); }
        }
    }

    private void Play_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper is null) { StatusText.Text = "Select a wallpaper before playing"; return; }
        if (_selectedWallpaper.Type == WallpaperType.Image) { StatusText.Text = "Images do not have playback controls"; return; }
        try { PreviewMedia.Play(); _playbackTimer.Start(); SetPlaying(true); StatusText.Text = $"Playing {_selectedWallpaper.Title}"; } catch (Exception ex) { ShowPreviewError($"Unable to play video: {ex.Message}"); }
    }

    private void Pause_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper?.Type != WallpaperType.Video) { StatusText.Text = "Select a video to pause"; return; }
        PreviewMedia.Pause(); _playbackTimer.Stop(); SetPlaying(false); StatusText.Text = "Playback paused";
    }

    private void Stop_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper?.Type != WallpaperType.Video) { StatusText.Text = "Select a video to stop"; return; }
        StopPreview(true); StatusText.Text = "Playback stopped";
    }

    private void PlayPause_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper?.Type != WallpaperType.Video) return;
        if (_isPlaying) Pause_OnClick(sender, e); else Play_OnClick(sender, e);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        if (PlayPauseGlyph is not null) PlayPauseGlyph.Text = playing ? "\uE769" : "\uE768";   // pause : play
    }

    private void StopPreview(bool resetPosition)
    {
        _playbackTimer.Stop();
        SetPlaying(false);
        try { PreviewMedia.Stop(); } catch (InvalidOperationException) { }
        if (resetPosition) TimelineSlider.Value = 0;
        TimeText.Text = "00:00 / 00:00";
    }

    private void PreviewMedia_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (PreviewMedia.NaturalDuration.HasTimeSpan)
        {
            TimelineSlider.Maximum = Math.Max(1, PreviewMedia.NaturalDuration.TimeSpan.TotalSeconds);
            DurationText.Text = $"{PreviewMedia.NaturalDuration.TimeSpan:hh\\:mm\\:ss}";
            UpdateTimeText();
        }
        if (PreviewMedia.NaturalVideoWidth > 0) ResolutionText.Text = $"{PreviewMedia.NaturalVideoWidth} × {PreviewMedia.NaturalVideoHeight}";
        if (_pendingSeek is { } seek)
        {
            _pendingSeek = null;
            try { PreviewMedia.Position = seek; } catch (InvalidOperationException) { }
        }
        StatusText.Text = _selectedWallpaper is null ? "Video ready" : (_isPlaying ? $"Playing {_selectedWallpaper.Title}" : $"{_selectedWallpaper.Title} is paused");
    }

    private void PreviewMedia_OnMediaFailed(object sender, ExceptionRoutedEventArgs e) => ShowPreviewError($"Video playback failed: {e.ErrorException?.Message ?? "unsupported or unreadable media"}");

    private void PreviewMedia_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_settings.LoopVideoPreviews && _selectedWallpaper?.Type == WallpaperType.Video && _isPlaying)
        {
            PreviewMedia.Position = TimeSpan.Zero;   // loop: keep playing
            PreviewMedia.Play();
            return;
        }
        _playbackTimer.Stop(); SetPlaying(false); TimelineSlider.Value = 0; StatusText.Text = "Playback ended";
    }

    private void PlaybackTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_isSeeking && PreviewMedia.NaturalDuration.HasTimeSpan) { TimelineSlider.Value = Math.Min(TimelineSlider.Maximum, PreviewMedia.Position.TotalSeconds); UpdateTimeText(); }
    }

    private void Timeline_OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;
    private void Timeline_OnPreviewMouseUp(object sender, MouseButtonEventArgs e) { _isSeeking = false; if (_selectedWallpaper?.Type == WallpaperType.Video) { try { PreviewMedia.Position = TimeSpan.FromSeconds(TimelineSlider.Value); } catch (InvalidOperationException) { } } UpdateTimeText(); }
    private void Timeline_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_isSeeking && _selectedWallpaper?.Type == WallpaperType.Video) UpdateTimeText(); }
    private void Volume_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewMedia is null || VolumeText is null || VolumeSlider is null) return;   // can fire while the XAML is still being loaded
        PreviewMedia.Volume = e.NewValue;
        if (!_syncing)
        {
            // The user moved the slider: that unmutes this session and becomes the remembered default volume.
            PreviewMedia.IsMuted = false;
            _settings.DefaultVolume = e.NewValue;
            _settingsVm.NotifyChangedExternally(nameof(AppSettings.DefaultVolume));
        }
        if (_wallpaperHost.IsVideoWallpaperRunning) _wallpaperHost.UpdateVideoOptions(GetApplyOptions());
        UpdateVolumeText();
    }

    private void UpdateVolumeText()
    {
        if (VolumeText is null || VolumeSlider is null || PreviewMedia is null) return;
        VolumeText.Text = PreviewMedia.IsMuted ? "Muted" : $"{Math.Round(VolumeSlider.Value * 100)}%";
    }

    private void Speed_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewMedia is null || SpeedCombo is null) return;
        if (SpeedCombo.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            PreviewMedia.SpeedRatio = ratio;
            if (!_syncing)
            {
                _settings.DefaultSpeed = ratio;   // remembered as the default speed
                _settingsVm.NotifyChangedExternally(nameof(AppSettings.DefaultSpeed));
            }
        }
        if (_wallpaperHost.IsVideoWallpaperRunning) _wallpaperHost.UpdateVideoOptions(GetApplyOptions());
    }

    private void Fit_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewMedia is null || PreviewImage is null || FitCombo is null) return;
        if (FitCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var stretch = tag switch
        {
            "Fit" => Stretch.Uniform,
            "Stretch" => Stretch.Fill,
            "Center" => Stretch.None,
            _ => Stretch.UniformToFill
        };
        PreviewMedia.Stretch = stretch;
        PreviewImage.Stretch = stretch;
        if (!_syncing)
        {
            _settings.LastFitMode = tag;
            _settingsService.ScheduleSave();
        }
        if (_wallpaperHost.IsVideoWallpaperRunning) _wallpaperHost.UpdateVideoOptions(GetApplyOptions());
    }


    private void UpdateTimeText()
    {
        var current = TimeSpan.FromSeconds(Math.Max(0, TimelineSlider.Value));
        var total = PreviewMedia.NaturalDuration.HasTimeSpan ? PreviewMedia.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{current:mm\\:ss} / {total:mm\\:ss}";
    }

    private void ShowPreviewError(string message)
    {
        _playbackTimer.Stop(); SetPlaying(false); PreviewControls.Visibility = Visibility.Collapsed; PreviewMedia.Visibility = Visibility.Collapsed; PreviewImage.Visibility = Visibility.Collapsed; PreviewEmptyText.Visibility = Visibility.Visible; PreviewEmptyText.Text = message; StatusText.Text = message;
    }

    private static string FormatFileSize(string path)
    {
        if (!File.Exists(path)) return "—";
        var bytes = new FileInfo(path).Length;
        return bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024d):0} KB";
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => RefreshLibraryView();
    private WallpaperApplyOptions GetApplyOptions()
    {
        var fit = WallpaperFitMode.Fill;
        if (FitCombo?.SelectedItem is ComboBoxItem { Tag: string fitTag }) fit = WallpaperApplyOptions.ParseFitMode(fitTag);

        var speed = 1.0;
        if (SpeedCombo?.SelectedItem is ComboBoxItem { Tag: string speedTag } &&
            double.TryParse(speedTag, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            speed = ratio;

        var volume = _settings.DesktopWallpaperSoundEnabled ? _settings.DefaultVolume : 0;
        return new WallpaperApplyOptions(fit, speed, volume);
    }

    private async void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedWallpaper is null)
        {
            StatusText.Text = "Select a wallpaper before applying";
            return;
        }

        var item = _selectedWallpaper;
        var options = GetApplyOptions();
        AppLog.Info($"Set as Wallpaper clicked: title='{item.Title}' type={item.Type} path='{item.FilePath}' fit={options.FitMode} speed={options.SpeedRatio}x");
        ApplyButton.IsEnabled = false;
        StatusText.Text = item.Type == WallpaperType.Video
            ? $"Starting video wallpaper “{item.Title}”…"
            : $"Setting “{item.Title}” as the desktop wallpaper…";

        await ApplyWallpaperAsync(item, options, showFailureDialog: true);
    }

    private async Task<bool> ApplyWallpaperAsync(WallpaperItem item, WallpaperApplyOptions options, bool showFailureDialog, bool applyLockScreen = true)
    {
        WallpaperApplyResult result;
        try
        {
            result = await _wallpaperHost.ApplyAsync(item, options, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            ApplyButton.IsEnabled = true;
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("Unhandled exception while applying the wallpaper.", ex);
            result = WallpaperApplyResult.FailWithLog($"Unexpected error while applying the wallpaper: {ex.Message}");
        }

        ApplyButton.IsEnabled = true;
        AppLog.Info($"Apply finished: success={result.Success}");
        // Status line: first line only (the dialog carries the full details for failures).
        StatusText.Text = result.Success ? result.Message : "Wallpaper not applied: " + result.Message.Split('\n')[0];
        if (result.Success)
        {
            // Persist only after DesktopWallpaper/LibVLC verification has reported success.
            _settings.LastAppliedWallpaperPath = item.FilePath;
            _settings.LastAppliedFitMode = options.FitMode.ToString();
            _settings.LastAppliedSpeed = options.SpeedRatio;
            _settingsService.Flush();
            AppLog.Info($"Last applied wallpaper saved: '{item.FilePath}'");

            // Not done when the wallpaper is only being restored at app start: that would re-copy the image (and could
            // raise a UAC prompt) on every sign-in, and the lock screen already has the image from last time.
            if (applyLockScreen && _settings.ApplyToLockScreen)
            {
                var lockResult = await ApplyItemToLockScreenAsync(item);
                if (!lockResult.Success) StatusText.Text = result.Message + " Lock screen: " + lockResult.Message;
            }
            return true;
        }
        else if (showFailureDialog)
            MessageBox.Show(this, result.Message, "Set as Wallpaper — not applied", MessageBoxButton.OK, MessageBoxImage.Warning);

        return false;
    }

    private async Task RestoreLastAppliedWallpaperAsync()
    {
        if (_restoredAppliedWallpaper || !_settings.RestoreAppState || string.IsNullOrWhiteSpace(_settings.LastAppliedWallpaperPath)) return;
        var item = _wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, _settings.LastAppliedWallpaperPath, StringComparison.OrdinalIgnoreCase));
        if (item is null || !File.Exists(item.FilePath))
        {
            AppLog.Warn($"Saved wallpaper could not be restored because it is missing: '{_settings.LastAppliedWallpaperPath}'");
            return;
        }

        // At sign-in Explorer may still be starting. Retry instead of giving up permanently on the first launch.
        var attempts = _startedFromWindows && item.Type == WallpaperType.Video ? 12 : 3;
        for (var attempt = 1; attempt <= attempts && !_lifetime.IsCancellationRequested; attempt++)
        {
            if (_startedFromWindows && item.Type == WallpaperType.Video && !NativeMethods.DiscoverShell().IsValid)
            {
                await Task.Delay(Math.Min(250 * attempt, 1500), _lifetime.Token);
                continue;
            }

            var fit = WallpaperApplyOptions.ParseFitMode(_settings.LastAppliedFitMode);
            var options = new WallpaperApplyOptions(fit, _settings.LastAppliedSpeed, 0);
            StatusText.Text = $"Restoring wallpaper “{item.Title}”…";
            AppLog.Info($"Restoring last applied wallpaper (attempt {attempt}/{attempts}): '{item.FilePath}' fit={fit} speed={options.SpeedRatio}x startup={_startedFromWindows}");
            if (await ApplyWallpaperAsync(item, options, showFailureDialog: false, applyLockScreen: false))
            {
                _restoredAppliedWallpaper = true;
                return;
            }

            if (attempt < attempts)
                await Task.Delay(Math.Min(300 * attempt, 1500), _lifetime.Token);
        }

        AppLog.Warn($"Wallpaper restore did not succeed after {attempts} attempt(s); it can be retried from the tray.");
    }
    private void UpdateCount()
    {
        _settingsVm.RefreshLibrary();
        UpdateItemCountText();
        if (EmptyStatePanel is null) return;
        var visibleCount = WallpaperItems?.Items.Count ?? _wallpapers.Count;
        EmptyStatePanel.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_wallpapers.Count == 0)
        {
            EmptyStateTitleText.Text = "Your library is empty";
            EmptyStateMessageText.Text = "Import an image or video to start building your wallpaper library.";
            StatusText.Text = "Your library is empty";
        }
        else if (visibleCount == 0)
        {
            EmptyStateTitleText.Text = "No wallpapers found";
            EmptyStateMessageText.Text = "Try a different search term or import another wallpaper.";
        }
    }
    // ======================================================================================
    //  Pages and library view
    // ======================================================================================

    private (int Total, int Images, int Videos) GetLibraryStats()
    {
        var images = _wallpapers.Count(item => item.Type == WallpaperType.Image);
        return (_wallpapers.Count, images, _wallpapers.Count - images);
    }

    /// <summary>Applies the current category and search text to the grid.</summary>
    private void RefreshLibraryView()
    {
        if (WallpaperItems is null || SearchBox is null) return;
        StopHoverPreview();
        var query = SearchBox.Text.Trim();
        if (_category == LibraryCategory.All && query.Length == 0)
        {
            WallpaperItems.ItemsSource = _wallpapers;   // the live collection: imports appear immediately
        }
        else
        {
            IEnumerable<WallpaperItem> source = _wallpapers;
            if (_category == LibraryCategory.Images) source = source.Where(item => item.Type == WallpaperType.Image);
            else if (_category == LibraryCategory.Videos) source = source.Where(item => item.Type == WallpaperType.Video);
            if (query.Length > 0) source = source.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            WallpaperItems.ItemsSource = source.ToList();
        }
        UpdateCount();
    }

    private void Category_OnClick(object sender, RoutedEventArgs e)
    {
        var category = ReferenceEquals(sender, CategoryImagesButton) ? LibraryCategory.Images
            : ReferenceEquals(sender, CategoryVideosButton) ? LibraryCategory.Videos
            : LibraryCategory.All;
        _category = category;
        UpdateCategoryChips();
        if (_settings.RememberLastCategory)
        {
            _settings.LastCategory = category;
            _settingsService.ScheduleSave();
        }
        RefreshLibraryView();
    }

    private void UpdateCategoryChips()
    {
        SetActive(CategoryAllButton, _category == LibraryCategory.All);
        SetActive(CategoryImagesButton, _category == LibraryCategory.Images);
        SetActive(CategoryVideosButton, _category == LibraryCategory.Videos);
    }

    private static void SetActive(Button button, bool active)
    {
        if (active) button.Tag = "Active";
        else button.ClearValue(Button.TagProperty);
    }

    /// <summary>Re-selects the wallpaper that was open when the app was closed (Settings > General > Restore previous state).</summary>
    private void RestoreSelection()
    {
        if (!_settings.RestoreAppState || string.IsNullOrWhiteSpace(_settings.LastSelectedPath) || _selectedWallpaper is not null) return;
        var item = _wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, _settings.LastSelectedPath, StringComparison.OrdinalIgnoreCase));
        if (item is not null) SelectWallpaper(item);
    }

    // ======================================================================================
    //  Applying settings
    // ======================================================================================

    /// <summary>Called whenever a setting changes on the Settings page; applies it immediately.</summary>
    private void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.StartMinimizedToTray):
            case nameof(AppSettings.MinimizeToTrayOnClose):
                UpdateTrayVisibility();
                break;
            case nameof(AppSettings.RememberWindowBounds):
                CaptureWindowBounds();
                break;
            case nameof(AppSettings.PausePreviewsWhenMinimized):
            case nameof(AppSettings.PausePreviewsWhenInactive):
            case nameof(AppSettings.ReduceBackgroundActivityWhenIdle):
                UpdateBackgroundState();
                break;
            case nameof(AppSettings.HoverPreviewEnabled):
                if (!_settings.HoverPreviewEnabled) StopHoverPreview();
                break;
            case nameof(AppSettings.Theme):
                _themeService.Apply(_settings.Theme);
                break;
            case nameof(AppSettings.DefaultVolume):
                _syncing = true;
                try { VolumeSlider.Value = _settings.DefaultVolume; }
                finally { _syncing = false; }
                if (_wallpaperHost.IsVideoWallpaperRunning) _wallpaperHost.UpdateVideoOptions(GetApplyOptions());
                break;
            case nameof(AppSettings.DesktopWallpaperSoundEnabled):
                if (_wallpaperHost.IsVideoWallpaperRunning) _wallpaperHost.UpdateVideoOptions(GetApplyOptions());
                break;
            case nameof(AppSettings.ApplyToLockScreen):
                _ = HandleLockScreenToggleAsync(_settings.ApplyToLockScreen);
                break;
            case nameof(AppSettings.DefaultSpeed):
                _syncing = true;
                try
                {
                    SelectSpeed(_settings.DefaultSpeed);
                    PreviewMedia.SpeedRatio = _settings.DefaultSpeed;
                }
                finally { _syncing = false; }
                break;
            case nameof(AppSettings.MutePreviewsByDefault):
                PreviewMedia.IsMuted = _settings.MutePreviewsByDefault;
                UpdateVolumeText();
                break;
            case nameof(AppSettings.RememberLastCategory):
                if (_settings.RememberLastCategory)
                {
                    _settings.LastCategory = _category;
                    _settingsService.ScheduleSave();
                }
                break;
            case nameof(AppSettings.ThumbnailQuality):
                _ = RebuildThumbnailsAsync();
                break;
        }
    }

    /// <summary>The lock screen can only show a still image: the image itself, or the cached thumbnail of a video.</summary>
    private async Task<(bool Success, string Message)> ApplyItemToLockScreenAsync(WallpaperItem item)
    {
        string? source = item.FilePath;
        if (item.Type == WallpaperType.Video)
        {
            // Windows cannot play video on the lock screen: use one frame at the video's original resolution.
            source = await _thumbnailService.CaptureOriginalFrameAsync(item.FilePath, _lifetime.Token);
            if (source is null)
            {
                // Fallback: the small cached thumbnail (lower quality, but better than nothing).
                if (string.IsNullOrWhiteSpace(item.PreviewPath) || !File.Exists(item.PreviewPath))
                    await EnsureThumbnailAsync(item, _lifetime.Token);
                source = File.Exists(item.PreviewPath) ? item.PreviewPath : null;
            }
            if (source is null) return (false, "A still frame of the video could not be captured.");
        }

        var result = await LockScreenWallpaperService.ApplyAsync(source);
        AppLog.Info("Lock screen: " + result.Message);
        if (result.Success)
        {
            _settings.LockScreenPolicyApplied = LockScreenWallpaperService.UsedPolicyPath;
            _settingsService.ScheduleSave();
        }
        return result;
    }

    /// <summary>Settings toggle: ON applies the current wallpaper to the lock screen right away; OFF gives it back to Windows.</summary>
    private async Task HandleLockScreenToggleAsync(bool enabled)
    {
        if (!enabled)
        {
            if (!_settings.LockScreenPolicyApplied) { StatusText.Text = "Lock-screen wallpaper turned off."; return; }
            var cleared = await LockScreenWallpaperService.ClearPolicyAsync();
            if (cleared.Success) { _settings.LockScreenPolicyApplied = false; _settingsService.ScheduleSave(); }
            StatusText.Text = cleared.Message;
            return;
        }

        // Prefer what is actually applied to the desktop; fall back to the wallpaper selected in the library.
        var item = string.IsNullOrWhiteSpace(_settings.LastAppliedWallpaperPath)
            ? null
            : _wallpapers.FirstOrDefault(w => string.Equals(w.FilePath, _settings.LastAppliedWallpaperPath, StringComparison.OrdinalIgnoreCase));
        item ??= _selectedWallpaper;
        if (item is null)
        {
            StatusText.Text = "Select a wallpaper first, then turn on lock-screen wallpaper.";
            return;
        }

        StatusText.Text = "Setting the lock screen…";
        var result = await ApplyItemToLockScreenAsync(item);
        StatusText.Text = result.Message;
        if (!result.Success)
        {
            AppLog.Warn("Lock-screen setting was enabled but application failed: " + result.Message);
            MessageBox.Show(this, result.Message, "Lock screen — not applied", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Pushes the saved playback preferences into the details panel (volume, mute, speed, fit mode).</summary>
    private void ApplyPlaybackDefaults()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            VolumeSlider.Value = _settings.DefaultVolume;
            PreviewMedia.Volume = _settings.DefaultVolume;
            PreviewMedia.IsMuted = _settings.MutePreviewsByDefault;
            SelectSpeed(_settings.DefaultSpeed);
            PreviewMedia.SpeedRatio = _settings.DefaultSpeed;
            var fitTag = _settings.RestoreAppState ? _settings.LastFitMode : "Fill";
            foreach (var item in FitCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && tag == fitTag)
                {
                    FitCombo.SelectedItem = item;
                    break;
                }
            }
            UpdateVolumeText();
        }
        finally { _syncing = wasSyncing; }
    }

    private void SelectSpeed(double speed)
    {
        foreach (var item in SpeedCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                Math.Abs(value - speed) < 0.001)
            {
                SpeedCombo.SelectedItem = item;
                return;
            }
        }
    }

    private async Task RebuildThumbnailsAsync()
    {
        _thumbnailService.Quality = _settings.ThumbnailQuality;
        StopHoverPreview();
        StatusText.Text = "Rebuilding thumbnails…";
        await PrepareThumbnailsAsync(_wallpapers.ToList());
        if (_lifetime.IsCancellationRequested) return;
        _thumbnailService.PurgeOtherQualities();   // drop the previous quality's files
        SaveLibrary();
        _settingsVm.RefreshCache();
        StatusText.Text = "Thumbnails updated";
    }

    // ======================================================================================
    //  Pausing previews and idle mode
    // ======================================================================================

    /// <summary>
    /// Recomputes why the details preview should be paused (minimized / hidden, inactive) and whether the app is
    /// idle in the background. Called whenever activation, visibility, window state or the related settings change.
    /// </summary>
    private void UpdateBackgroundState()
    {
        if (_idleTimer is null) return;   // not constructed yet
        var hidden = !IsVisible || WindowState == WindowState.Minimized;
        SetSuspended("hidden", hidden && _settings.PausePreviewsWhenMinimized);
        SetSuspended("inactive", !IsActive && _settings.PausePreviewsWhenInactive);

        var background = hidden || !IsActive;
        if (_settings.ReduceBackgroundActivityWhenIdle && background)
        {
            if (!_inIdleMode && !_idleTimer.IsEnabled) _idleTimer.Start();
        }
        else
        {
            _idleTimer.Stop();
            if (_inIdleMode) ExitIdleMode();
        }
    }

    private void SetSuspended(string reason, bool suspended)
    {
        if (suspended)
        {
            if (!_suspendReasons.Add(reason)) return;
            if (_suspendReasons.Count == 1 && _isPlaying)
            {
                _resumeWhenUnsuspended = true;
                PreviewMedia.Pause();
                _playbackTimer.Stop();
                SetPlaying(false);
            }
        }
        else
        {
            if (!_suspendReasons.Remove(reason)) return;
            if (_suspendReasons.Count == 0 && _resumeWhenUnsuspended)
            {
                _resumeWhenUnsuspended = false;
                if (_selectedWallpaper?.Type == WallpaperType.Video)
                {
                    PreviewMedia.Play();
                    _playbackTimer.Start();
                    SetPlaying(true);
                }
            }
        }
    }

    /// <summary>After 60 s in the background: pause the preview, release its media and decoded image, and give memory back.</summary>
    private void EnterIdleMode()
    {
        _idleTimer.Stop();
        if (_inIdleMode) return;
        _inIdleMode = true;
        StopHoverPreview();
        SetSuspended("idle", true);

        if (_selectedWallpaper is not null && !_previewReleased)
        {
            _previewReleased = true;
            try
            {
                if (_selectedWallpaper.Type == WallpaperType.Video && PreviewMedia.NaturalDuration.HasTimeSpan)
                    _pendingSeek = PreviewMedia.Position;
                PreviewMedia.Stop();
                PreviewMedia.Close();
            }
            catch (InvalidOperationException) { }
            PreviewMedia.Source = null;
            PreviewImage.Source = null;
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        NativeMethods.TrimWorkingSet();
        AppLog.Info("Idle mode: preview released, memory trimmed.");
    }

    private void ExitIdleMode()
    {
        _inIdleMode = false;
        if (_previewReleased)
        {
            _previewReleased = false;
            if (_selectedWallpaper is not null)
                SelectWallpaper(_selectedWallpaper, autoPlay: _resumeWhenUnsuspended);   // reloads (paused while "idle" is still a reason)
        }
        SetSuspended("idle", false);
    }

    // ======================================================================================
    //  Tray, closing and window bounds
    // ======================================================================================

    public void StartInTray()
    {
        // Sign-in start: do NOT build/show the window (that is the slow part). Only the wallpaper is restored, in the
        // background; the window, its selection and its thumbnails are prepared when it is opened from the tray.
        _inTray = true;
        UpdateTrayVisibility();
        if (_wallpapers.Count > 0)
            Dispatcher.BeginInvoke(new Action(async () => await RestoreLastAppliedWallpaperAsync()), DispatcherPriority.Normal);
    }

    /// <summary>About → Help &amp; support: opens a ready-to-send Gmail compose window (the address is not shown in the UI).</summary>
    private void Support_OnClick(object sender, RoutedEventArgs e)
    {
        const string to = "sanjarplus14@gmail.com";
        var version = _settingsVm.VersionText;
        var subject = $"m4xmudovv support (v{version})";
        var body = $"Hello,\n\n\n\n---\nm4xmudovv {version}\nWindows {Environment.OSVersion.Version}";
        var gmail = $"https://mail.google.com/mail/?view=cm&fs=1&to={Uri.EscapeDataString(to)}" +
                    $"&su={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(gmail) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not open Gmail in the browser; trying the default mail app.", ex);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"mailto:{to}?subject={Uri.EscapeDataString(subject)}") { UseShellExecute = true });
            }
            catch (Exception) { StatusText.Text = "Could not open the mail window."; }
        }
    }

    private Views.TrayMenuWindow? _trayMenu;

    private void ShowTrayMenu(int x, int y)
    {
        if (_trayMenu is not null) { _trayMenu.CloseMenu(); _trayMenu = null; return; }
        if (Views.TrayMenuWindow.JustClosed) return;   // this very click just closed the menu

        var status = _wallpaperHost.IsVideoWallpaperRunning ? "Live wallpaper is playing" : "Live wallpapers";
        var menu = new Views.TrayMenuWindow(
            open: ShowFromTray,
            settings: () => { ShowFromTray(); ShowSettingsPage(); },
            exit: ExitApplication,
            statusText: status);
        _trayMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; };
        menu.ShowNear(x, y);
    }

    public void ShowFromTray()
    {
        _inTray = false;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        UpdateTrayVisibility();
    }

    private void HideToTray()
    {
        CaptureWindowBounds();
        _settingsService.Flush();
        StopHoverPreview();
        _inTray = true;
        Hide();
        UpdateTrayVisibility();
    }

    private void UpdateTrayVisibility() =>
        _tray.Visible = _settings.StartMinimizedToTray || _settings.MinimizeToTrayOnClose || _inTray;

    /// <summary>Marks the next close as a real exit (tray "Exit", Windows sign-out) so it is not turned into "hide to tray".</summary>
    public void PrepareForExit() => _exiting = true;

    private void ExitApplication()
    {
        PrepareForExit();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _exiting = true;
        CaptureWindowBounds();
        _settingsService.Flush();
        base.OnClosing(e);
    }

    /// <summary>Releases everything the window owns. Safe to call more than once (window Closed and App.OnExit both call it).</summary>
    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        StopHoverPreview();
        _idleTimer.Stop();
        _lifetime.Cancel();
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        _wallpaperHost.Shutdown();
        _tray.Dispose();
        _settingsService.Flush();
    }

    private void SystemEvents_SessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_wallpaperHost.IsVideoWallpaperRunning) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _wallpaperHost.UpdateVideoOptions(GetApplyOptions() with { Volume = 0 });
                AppLog.Info("Windows session locked: desktop wallpaper audio muted.");
            }
            else
            {
                _wallpaperHost.UpdateVideoOptions(GetApplyOptions());
                AppLog.Info("Windows session unlocked: desktop wallpaper audio restored from Settings.");
            }
        }));
    }

    private void RestoreWindowBounds()
    {
        if (!_settings.RememberWindowBounds) return;
        var width = _settings.WindowWidth;
        var height = _settings.WindowHeight;
        if (width < MinWidth || height < MinHeight || double.IsNaN(_settings.WindowLeft) || double.IsNaN(_settings.WindowTop)) return;

        var saved = new Rect(_settings.WindowLeft, _settings.WindowTop, width, height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(saved, screen);
        // The monitor it was on may be gone: if the saved window would be (almost) off-screen, keep the default placement.
        if (visible.IsEmpty || visible.Width < 120 || visible.Height < 80) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void CaptureWindowBounds()
    {
        if (!_settings.RememberWindowBounds || !_boundsReady || _settings is null) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top) || bounds.Width <= 0 || bounds.Height <= 0) return;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settingsService.ScheduleSave();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) Maximize_OnClick(sender, e); else DragMove(); }
    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
