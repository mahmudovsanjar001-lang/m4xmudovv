using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using m4xmudovv.Models;

namespace m4xmudovv.Services;

internal sealed class WidgetManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly WidgetCatalogService _catalog;
    private readonly Dictionary<string, WidgetDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WidgetWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _pausedForFullscreen;
    private bool _disposed;

    public WidgetManager(AppSettings settings, SettingsService settingsService, WidgetCatalogService catalog)
    {
        _settings = settings; _settingsService = settingsService; _catalog = catalog;
        foreach (var definition in catalog.BuiltIns.Concat(catalog.DiscoverImported())) _definitions[definition.Id] = definition;
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenState();
    }

    public IReadOnlyCollection<WidgetDefinition> Definitions => _definitions.Values;
    public IReadOnlyCollection<string> ActiveWidgetIds => _windows.Keys.ToList();

    public void RestoreEnabledWidgets()
    {
        foreach (var definition in _definitions.Values)
        {
            var state = GetOrCreateState(definition);
            if (state.Enabled) Show(definition.Id);
        }
        ConfigureGamingMode(_settings.GamingModeEnabled);
    }

    public void ConfigureGamingMode(bool enabled)
    {
        if (_disposed) return;
        if (enabled) _fullscreenTimer.Start();
        else
        {
            _fullscreenTimer.Stop();
            _pausedForFullscreen = false;
            foreach (var window in _windows.Values.Where(window => window.State.Enabled)) window.Show();
        }
    }

    public void Show(string id)
    {
        if (!_definitions.TryGetValue(id, out var definition) || _windows.ContainsKey(id)) return;
        var state = GetOrCreateState(definition); state.Enabled = true;
        var window = new WidgetWindow(definition, state);
        window.WidgetStateChanged += PersistWindow;
        window.HideRequested += HideWindow;
        window.RemoveRequested += RemoveWindow;
        _windows[id] = window;
        window.Show();
        _settingsService.ScheduleSave();
    }

    public void Hide(string id)
    {
        if (!_windows.TryGetValue(id, out var window)) return;
        GetOrCreateState(_definitions[id]).Enabled = false;
        _windows.Remove(id);
        try { window.Close(); } catch { }
        _settingsService.ScheduleSave();
    }

    public void Remove(string id)
    {
        if (_windows.TryGetValue(id, out var window)) { window.Close(); _windows.Remove(id); }
        if (_definitions.TryGetValue(id, out var definition) && !definition.BuiltIn) { _catalog.Remove(definition); _definitions.Remove(id); }
        _settings.WidgetStates.RemoveAll(state => string.Equals(state.WidgetId, id, StringComparison.OrdinalIgnoreCase));
        _settingsService.ScheduleSave();
    }

    public void RegisterImported(WidgetDefinition definition) => _definitions[definition.Id] = definition;

    private WidgetInstanceState GetOrCreateState(WidgetDefinition definition)
    {
        var state = _settings.WidgetStates.FirstOrDefault(item => string.Equals(item.WidgetId, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (state is not null) return state;
        state = new WidgetInstanceState { WidgetId = definition.Id, Width = definition.DefaultWidth, Height = definition.DefaultHeight, Enabled = false };
        _settings.WidgetStates.Add(state); return state;
    }

    private void PersistWindow(WidgetWindow window) => _settingsService.ScheduleSave();
    private void HideWindow(WidgetWindow window) => Hide(window.State.WidgetId);
    private void RemoveWindow(WidgetWindow window) => Remove(window.State.WidgetId);

    private void UpdateFullscreenState()
    {
        var fullscreen = new GamingModeService().IsFullscreenGameActive();
        if (fullscreen == _pausedForFullscreen) return;
        _pausedForFullscreen = fullscreen;
        foreach (var window in _windows.Values)
        {
            if (fullscreen) window.Hide();
            else if (window.State.Enabled) window.Show();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fullscreenTimer.Stop();
        foreach (var window in _windows.Values.ToList()) { try { window.Close(); } catch { } }
        _windows.Clear();
    }
}
