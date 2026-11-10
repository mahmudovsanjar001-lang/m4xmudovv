using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using m4xmudovv.Models;

namespace m4xmudovv.Services;

/// <summary>
/// Creates, caches and loads library thumbnails.
/// Cache files are JPEGs in %LOCALAPPDATA%\m4xmudovv\thumbnails\&lt;quality&gt;, keyed by path + size + write time.
/// The quality setting (Low / Balanced / High) controls the stored size, JPEG quality and decode size.
/// Work is throttled so a large library never saturates the CPU, disk or the UI thread.
/// </summary>
public sealed class WallpaperThumbnailService
{
    /// <summary>Size and encoding of one quality level. Cards are 228 DIP wide; DecodeWidth keeps them sharp at high DPI.</summary>
    private readonly record struct Profile(int Width, int Height, int JpegQuality, int DecodeWidth);

    private static Profile ProfileFor(ThumbnailQuality quality) => quality switch
    {
        ThumbnailQuality.Low => new Profile(480, 270, 70, 320),
        ThumbnailQuality.High => new Profile(960, 540, 92, 640),
        _ => new Profile(640, 360, 84, 400)
    };

    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "m4xmudovv",
        "thumbnails");

    private volatile ThumbnailQuality _quality = ThumbnailQuality.Balanced;

    /// <summary>Changing the quality switches to that quality's own cache folder; call RebuildAsync-style work afterwards.</summary>
    public ThumbnailQuality Quality
    {
        get => _quality;
        set
        {
            if (_quality == value) return;
            _quality = value;
            _failed.Clear();   // a smaller/larger size may succeed where the previous attempt failed
        }
    }

    private string CacheDirectoryFor(ThumbnailQuality quality) => Path.Combine(_cacheRoot, quality.ToString().ToLowerInvariant());

    private readonly SemaphoreSlim _imageGate = new(4);
    private readonly SemaphoreSlim _shellGate = new(2);
    private readonly SemaphoreSlim _playerGate = new(1);   // MediaPlayer fallback must run on the UI thread, one at a time
    private readonly SemaphoreSlim _decodeGate = new(4);
    private readonly ConcurrentDictionary<string, byte> _failed = new();

    /// <summary>Returns the cache file path if a valid thumbnail already exists on disk; never creates anything.</summary>
    public string? TryGetCached(WallpaperItem item)
    {
        var cachePath = TryGetCachePath(item.FilePath, _quality);
        return cachePath is not null && IsValidCache(cachePath) ? cachePath : null;
    }

    /// <summary>Returns the cached thumbnail path, generating the thumbnail first when needed. Null if it cannot be made.</summary>
    public async Task<string?> GetOrCreateAsync(WallpaperItem item, CancellationToken cancellationToken = default)
    {
        var quality = _quality;                 // one consistent quality for the whole operation
        var profile = ProfileFor(quality);
        var cachePath = TryGetCachePath(item.FilePath, quality);
        if (cachePath is null) return null;
        if (IsValidCache(cachePath)) return cachePath;
        if (_failed.ContainsKey(cachePath)) return null;

        try
        {
            Directory.CreateDirectory(CacheDirectoryFor(quality));
            BitmapSource? bitmap = item.Type == WallpaperType.Image
                ? await CreateImageBitmapAsync(item.FilePath, profile, cancellationToken)
                : await CreateVideoBitmapAsync(item.FilePath, profile, cancellationToken);

            if (bitmap is null)
            {
                _failed.TryAdd(cachePath, 0);
                return null;
            }

            await Task.Run(() => SaveJpeg(bitmap, cachePath, profile.JpegQuality), cancellationToken);
            return cachePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            _failed.TryAdd(cachePath, 0);
            return null;
        }
    }

    /// <summary>Decodes a cached thumbnail file at card size on a worker thread and returns a frozen image.</summary>
    public async Task<ImageSource?> LoadThumbnailImageAsync(string thumbnailPath, CancellationToken cancellationToken = default)
    {
        await _decodeGate.WaitAsync(cancellationToken);
        try
        {
            var decodeWidth = ProfileFor(_quality).DecodeWidth;
            return await Task.Run(() => DecodeForDisplay(thumbnailPath, decodeWidth), cancellationToken);
        }
        finally { _decodeGate.Release(); }
    }

    private static ImageSource? DecodeForDisplay(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // reads the file fully, so it is not left locked
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    private async Task<BitmapSource?> CreateImageBitmapAsync(string sourcePath, Profile profile, CancellationToken cancellationToken)
    {
        await _imageGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = profile.Width;   // decode straight to thumbnail size, never the full bitmap
                    image.UriSource = new Uri(sourcePath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    return (BitmapSource?)image;
                }
                catch (Exception) { return null; }
            }, cancellationToken);
        }
        finally { _imageGate.Release(); }
    }

    private async Task<BitmapSource?> CreateVideoBitmapAsync(string sourcePath, Profile profile, CancellationToken cancellationToken)
    {
        // 1) Windows Shell thumbnail (what Explorer shows): fast, off the UI thread, no WPF media pipeline.
        await _shellGate.WaitAsync(cancellationToken);
        try
        {
            var shellBitmap = await ShellThumbnailProvider.TryGetAsync(sourcePath, profile.Width, profile.Height);
            if (shellBitmap is not null) return shellBitmap;
        }
        finally { _shellGate.Release(); }

        cancellationToken.ThrowIfCancellationRequested();

        // 2) Fallback for formats the shell has no handler for: capture a frame with MediaPlayer.
        await _playerGate.WaitAsync(cancellationToken);
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return null;
            var capture = await dispatcher.InvokeAsync(() => CaptureFrameAsync(sourcePath, profile.Width, profile.Height, cancellationToken));
            return await capture;
        }
        finally { _playerGate.Release(); }
    }

    /// <summary>
    /// Grabs one still frame of a video at the video's ORIGINAL resolution (no downscaling) and saves it as a lossless PNG.
    /// Used for the lock screen, which cannot play video. Returns the PNG path, or null if the frame could not be captured.
    /// </summary>
    public async Task<string?> CaptureOriginalFrameAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !File.Exists(sourcePath)) return null;

        await _playerGate.WaitAsync(cancellationToken);
        try
        {
            var capture = await dispatcher.InvokeAsync(() => CaptureOriginalFrameCoreAsync(sourcePath, cancellationToken));
            var bitmap = await capture;
            if (bitmap is null) return null;

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "m4xmudovv", "frames");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"frame-{Guid.NewGuid():N}.png");
            await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(target);
                encoder.Save(stream);
            }, cancellationToken);

            // Keep only the newest few captured frames.
            foreach (var old in new DirectoryInfo(folder).GetFiles("frame-*.png").OrderByDescending(f => f.CreationTimeUtc).Skip(3))
            {
                try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
        finally { _playerGate.Release(); }
    }

    // Runs on the UI thread (MediaPlayer is dispatcher-affine).
    private static async Task<BitmapSource?> CaptureOriginalFrameCoreAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onOpened = (_, _) => opened.TrySetResult(true);
        EventHandler<ExceptionEventArgs> onFailed = (_, _) => opened.TrySetResult(false);
        player.MediaOpened += onOpened;
        player.MediaFailed += onFailed;
        try
        {
            player.Open(new Uri(sourcePath, UriKind.Absolute));
            var finished = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(finished, opened.Task) || !opened.Task.Result) return null;

            var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            var seek = duration > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromMilliseconds(Math.Min(duration.TotalMilliseconds * 0.2, 5000))
                : TimeSpan.Zero;

            player.Play();
            player.Position = seek;
            await Task.Delay(500, cancellationToken);
            player.Pause();
            await Task.Delay(250, cancellationToken);

            var width = player.NaturalVideoWidth;
            var height = player.NaturalVideoHeight;
            if (width <= 0 || height <= 0) return null;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawVideo(player, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            player.MediaOpened -= onOpened;
            player.MediaFailed -= onFailed;
            player.Close();
        }
    }

    // Runs on the UI thread (MediaPlayer is dispatcher-affine).
    private static async Task<BitmapSource?> CaptureFrameAsync(string sourcePath, int thumbWidth, int thumbHeight, CancellationToken cancellationToken)
    {
        var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onOpened = (_, _) => opened.TrySetResult(true);
        EventHandler<ExceptionEventArgs> onFailed = (_, _) => opened.TrySetResult(false);
        player.MediaOpened += onOpened;
        player.MediaFailed += onFailed;
        try
        {
            player.Open(new Uri(sourcePath, UriKind.Absolute));
            var finished = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(finished, opened.Task) || !opened.Task.Result) return null;

            var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            var seek = duration > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromMilliseconds(Math.Min(duration.TotalMilliseconds * 0.2, 5000))
                : TimeSpan.Zero;

            // A MediaPlayer that was never played/paused has not decoded any frame yet (the old code drew a blank).
            player.Play();
            player.Position = seek;
            await Task.Delay(300, cancellationToken);
            player.Pause();
            await Task.Delay(150, cancellationToken);

            var videoWidth = player.NaturalVideoWidth;
            var videoHeight = player.NaturalVideoHeight;
            var target = new Rect(0, 0, thumbWidth, thumbHeight);
            if (videoWidth > 0 && videoHeight > 0)
            {
                var scale = Math.Max((double)thumbWidth / videoWidth, (double)thumbHeight / videoHeight);
                var w = videoWidth * scale;
                var h = videoHeight * scale;
                target = new Rect((thumbWidth - w) / 2, (thumbHeight - h) / 2, w, h);
            }

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, thumbWidth, thumbHeight));
                context.PushClip(new RectangleGeometry(new Rect(0, 0, thumbWidth, thumbHeight)));
                context.DrawVideo(player, target);
                context.Pop();
            }
            var bitmap = new RenderTargetBitmap(thumbWidth, thumbHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            player.MediaOpened -= onOpened;
            player.MediaFailed -= onFailed;
            player.Close();
        }
    }

    // Write to a temp file and move it into place, so a crash or encoder failure can never leave a
    // truncated file that would later be treated as a valid cached thumbnail.
    private static void SaveJpeg(BitmapSource bitmap, string cachePath, int jpegQuality)
    {
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(tempPath)) encoder.Save(stream);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { }
        }
    }

    private static bool IsValidCache(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string? TryGetCachePath(string sourcePath, ThumbnailQuality quality)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) return null;
            // "v2" salt: earlier builds could cache blank video frames forever, so those entries are not reused.
            var identity = $"v2|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            return Path.Combine(CacheDirectoryFor(quality), hash + ".jpg");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    // ---------------- Cache management (Settings > Library) ----------------

    /// <summary>Number of files and total bytes in the whole thumbnail cache (all quality levels).</summary>
    public (int Files, long Bytes) GetCacheStats()
    {
        var files = 0;
        long bytes = 0;
        try
        {
            if (!Directory.Exists(_cacheRoot)) return (0, 0);
            foreach (var file in Directory.EnumerateFiles(_cacheRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (files, bytes);
    }

    /// <summary>Deletes every cached thumbnail (including files from older builds). Returns what was removed.</summary>
    public (int Files, long Bytes) ClearCache()
    {
        var files = 0;
        long bytes = 0;
        try
        {
            if (Directory.Exists(_cacheRoot))
            {
                foreach (var file in Directory.EnumerateFiles(_cacheRoot, "*", SearchOption.AllDirectories).ToList())
                {
                    try
                    {
                        var length = new FileInfo(file).Length;
                        File.Delete(file);
                        files++;
                        bytes += length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (var directory in Directory.EnumerateDirectories(_cacheRoot).ToList())
                {
                    try { Directory.Delete(directory, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _failed.Clear();
        return (files, bytes);
    }

    /// <summary>After a quality change has been rebuilt: removes the other quality folders and files left by older builds.</summary>
    public void PurgeOtherQualities()
    {
        try
        {
            if (!Directory.Exists(_cacheRoot)) return;
            var keep = CacheDirectoryFor(_quality);
            foreach (var file in Directory.EnumerateFiles(_cacheRoot).ToList())
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var directory in Directory.EnumerateDirectories(_cacheRoot).ToList())
            {
                if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
