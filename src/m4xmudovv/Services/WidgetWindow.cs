using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using m4xmudovv.Models;
using m4xmudovv.Views;

namespace m4xmudovv.Services;

internal sealed class WidgetWindow : Window
{
    private readonly WidgetDefinition _definition;
    private readonly WidgetInstanceState _state;
    private readonly Border _surface;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _contentText;
    private readonly TextBlock _title;
    private bool _closing;
    private readonly Stopwatch _cpuWatch = Stopwatch.StartNew();
    private TimeSpan _lastCpu;
    private static readonly HttpClient WeatherClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private CancellationTokenSource? _weatherCts;
    private bool _weatherRequestActive;

    public WidgetWindow(WidgetDefinition definition, WidgetInstanceState state)
    {
        _definition = definition;
        _state = state;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = state.AlwaysOnTop;
        Left = state.Left; Top = state.Top; Width = state.Width; Height = state.Height;
        MinWidth = 160; MinHeight = 90;
        RestoreMonitorPlacement();
        _surface = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 17, 19, 24)), BorderBrush = new SolidColorBrush(Color.FromArgb(130, 190, 205, 235)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16) };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _title = new TextBlock { Text = definition.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
        _title.MouseLeftButtonDown += DragStart;
        root.Children.Add(_title);
        _contentText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(225, 230, 240)), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(_contentText, 1); root.Children.Add(_contentText);
        _surface.Child = root; Content = _surface;
        ContextMenu = BuildContextMenu();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(definition.Kind == WidgetKind.DigitalClock ? 1 : 5) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) => { ConfigureNativeWindow(); await LoadContentAsync(); Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _closing = true; _weatherCts?.Cancel(); _weatherCts?.Dispose(); };
        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) => SaveBounds();
    }

    public WidgetInstanceState State => _state;
    public event Action<WidgetWindow>? StateChanged;
    public event Action<WidgetWindow>? HideRequested;
    public event Action<WidgetWindow>? RemoveRequested;

    private void ConfigureNativeWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    private async Task LoadContentAsync()
    {
        if (_definition.Kind != WidgetKind.Html || string.IsNullOrWhiteSpace(_definition.PackageDirectory)) return;
        try
        {
            var web = new WebView2 { IsHitTestVisible = true };
            web.NavigationStarting += (_, args) =>
            {
                if (!IsAllowedLocalUri(args.Uri)) args.Cancel = true;
            };
            web.CoreWebView2InitializationCompleted += (_, _) =>
            {
                if (web.CoreWebView2 is not null)
                {
                    web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    web.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                    web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                    web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                    web.CoreWebView2.Navigate(new Uri(Path.Combine(_definition.PackageDirectory, _definition.EntryPoint ?? "index.html")).AbsoluteUri);
                }
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_definition.PackageDirectory, ".webview"), new CoreWebView2EnvironmentOptions("--disable-features=msWebOOUI,msPdfOOUI"));
            await web.EnsureCoreWebView2Async(environment);
            ((Grid)_surface.Child).Children.Remove(_contentText);
            Grid.SetRow(web, 1); ((Grid)_surface.Child).Children.Add(web);
        }
        catch (Exception ex) { AppLog.Warn($"Widget HTML view failed for '{_definition.Id}'.", ex); _contentText.Text = "This widget could not be opened."; }
    }

    private void Refresh()
    {
        if (_closing) return;
        switch (_definition.Kind)
        {
            case WidgetKind.DigitalClock:
                var now = DateTime.Now; var format = _state.Options.TryGetValue("24Hour", out var h) && h == "true" ? "HH:mm:ss" : "hh:mm:ss tt";
                _contentText.Text = $"{now.ToString(format, CultureInfo.InvariantCulture)}\n{now:dddd, d MMMM yyyy}"; _contentText.FontSize = 26; break;
            case WidgetKind.Calendar:
                _contentText.Text = BuildCalendar(DateTime.Today); _contentText.FontSize = 15; break;
            case WidgetKind.SystemMonitor:
                var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var elapsed = _cpuWatch.Elapsed; var cpuPercent = elapsed.TotalMilliseconds <= 0 ? 0 : Math.Clamp((cpu - _lastCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100, 0, 100); _lastCpu = cpu; _cpuWatch.Restart();
                _contentText.Text = $"CPU {cpuPercent:0}%\nMemory {process.WorkingSet64 / 1024d / 1024d:0} MB"; break;
            case WidgetKind.MusicPlayer:
                _contentText.Text = "Windows media session\nNo active media session detected."; break;
            case WidgetKind.Weather:
                _contentText.Text = _state.Options.TryGetValue("Location", out var location) && !string.IsNullOrWhiteSpace(location) ? $"Weather\n{location}\nLoading…" : "Weather\nSet a location in the context menu.";
                if (!string.IsNullOrWhiteSpace(location) && !_weatherRequestActive) _ = RefreshWeatherAsync(location);
                break;
        }
    }

    private static string BuildCalendar(DateTime date)
    {
        var first = new DateTime(date.Year, date.Month, 1); var days = DateTime.DaysInMonth(date.Year, date.Month); var text = $"{date:MMMM yyyy}\nMo Tu We Th Fr Sa Su\n"; var offset = ((int)first.DayOfWeek + 6) % 7; text += new string(' ', offset * 3);
        for (var day = 1; day <= days; day++) { text += day == date.Day ? $"[{day:00}]" : $"{day:00}"; text += ((offset + day) % 7 == 0 ? "\n" : " "); }
        return text;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        if (_definition.Kind == WidgetKind.Weather)
        {
            var location = new MenuItem { Header = "Set location (latitude, longitude)…" };
            location.Click += (_, _) => ConfigureWeatherLocation();
            menu.Items.Add(location);
        }
        var lockItem = new MenuItem { Header = "Lock position", IsCheckable = true, IsChecked = _state.Locked };
        lockItem.Click += (_, _) => { _state.Locked = lockItem.IsChecked; StateChanged?.Invoke(this); };
        var topItem = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = _state.AlwaysOnTop };
        topItem.Click += (_, _) => { _state.AlwaysOnTop = topItem.IsChecked; Topmost = _state.AlwaysOnTop; StateChanged?.Invoke(this); };
        var hide = new MenuItem { Header = "Hide" }; hide.Click += (_, _) => HideRequested?.Invoke(this);
        var remove = new MenuItem { Header = "Remove" }; remove.Click += (_, _) => RemoveRequested?.Invoke(this);
        menu.Items.Add(lockItem); menu.Items.Add(topItem); menu.Items.Add(hide); menu.Items.Add(remove); return menu;
    }

    private void ConfigureWeatherLocation()
    {
        var current = _state.Options.TryGetValue("Location", out var value) ? value : "";
        var chosen = AppDialogWindow.Prompt(this, "Weather location", "Enter coordinates, for example 41.3111, 69.2797. Data comes from Open-Meteo.", current, "Save", text =>
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180 ? null : "Use latitude, longitude within valid ranges.";
        });
        if (string.IsNullOrWhiteSpace(chosen)) return;
        var parts = chosen.Split(',', StringSplitOptions.TrimEntries);
        _state.Options["Location"] = $"{double.Parse(parts[0], CultureInfo.InvariantCulture):0.####}, {double.Parse(parts[1], CultureInfo.InvariantCulture):0.####}";
        StateChanged?.Invoke(this); Refresh();
    }

    private async Task RefreshWeatherAsync(string location)
    {
        if (_weatherRequestActive) return;
        _weatherRequestActive = true;
        _weatherCts?.Cancel();
        _weatherCts?.Dispose();
        _weatherCts = new CancellationTokenSource();
        var cancellationToken = _weatherCts.Token;
        try
        {
            var parts = location.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return;
            using var response = await WeatherClient.GetAsync($"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,weather_code,wind_speed_10m&timezone=auto", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var current = document.RootElement.GetProperty("current");
            var temperature = current.GetProperty("temperature_2m").GetDouble();
            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            var code = current.GetProperty("weather_code").GetInt32();
            await Dispatcher.InvokeAsync(() => { if (!_closing) _contentText.Text = $"Weather\n{temperature:0.#}°C · {WeatherDescription(code)}\nWind {wind:0.#} km/h\n{location}"; });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Warn($"Weather update failed for '{location}'.", ex);
            if (!_closing) await Dispatcher.InvokeAsync(() => _contentText.Text = $"Weather\nUnavailable\n{location}");
        }
        finally { _weatherRequestActive = false; }
    }

    private static string WeatherDescription(int code) => code switch
    {
        0 => "Clear sky", 1 or 2 or 3 => "Partly cloudy", 45 or 48 => "Fog", 51 or 53 or 55 or 56 or 57 => "Drizzle",
        61 or 63 or 65 or 66 or 67 => "Rain", 71 or 73 or 75 or 77 => "Snow", 80 or 81 or 82 => "Rain showers", 95 or 96 or 99 => "Thunderstorm", _ => "Unknown conditions"
    };

    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        if (_state.Locked || e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
        SaveBounds();
    }

    private void SaveBounds()
    {
        if (!IsLoaded) return;
        _state.Left = Left; _state.Top = Top; _state.Width = Width; _state.Height = Height;
        var centerX = Left + Width / 2; var centerY = Top + Height / 2;
        var monitor = DesktopWallpaperApi.GetMonitors().FirstOrDefault(item => centerX >= item.Left && centerX < item.Right && centerY >= item.Top && centerY < item.Bottom);
        if (monitor is not null) _state.MonitorId = monitor.Id;
        StateChanged?.Invoke(this);
    }

    private void RestoreMonitorPlacement()
    {
        var monitors = DesktopWallpaperApi.GetMonitors();
        if (monitors.Count == 0) return;
        var monitor = monitors.FirstOrDefault(item => string.Equals(item.Id, _state.MonitorId, StringComparison.OrdinalIgnoreCase))
                      ?? monitors.FirstOrDefault(item => _state.Left + Width / 2 >= item.Left && _state.Left + Width / 2 < item.Right && _state.Top + Height / 2 >= item.Top && _state.Top + Height / 2 < item.Bottom)
                      ?? monitors[0];
        _state.MonitorId = monitor.Id;
        var maxLeft = Math.Max(monitor.Left, monitor.Right - Width);
        var maxTop = Math.Max(monitor.Top, monitor.Bottom - Height);
        Left = Math.Clamp(_state.Left, monitor.Left, maxLeft);
        Top = Math.Clamp(_state.Top, monitor.Top, maxTop);
    }

    private bool IsAllowedLocalUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(_definition.PackageDirectory) || !Uri.TryCreate(uri, UriKind.Absolute, out var candidate) || !candidate.IsFile) return false;
        var root = Path.GetFullPath(_definition.PackageDirectory) + Path.DirectorySeparatorChar;
        try { return Path.GetFullPath(candidate.LocalPath).StartsWith(root, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
