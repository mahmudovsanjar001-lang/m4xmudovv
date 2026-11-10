using System;
using System.Windows;
using Microsoft.Win32;

namespace m4xmudovv.Services;

/// <summary>
/// Swaps the theme resource dictionary (Themes/Dark.xaml or Themes/Light.xaml). All colours in the UI are
/// DynamicResource brushes, so the whole window re-themes instantly. "System" follows the Windows app mode live.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private AppTheme _theme = AppTheme.Dark;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Apply(AppTheme theme)
    {
        _theme = theme;
        var useLight = theme == AppTheme.Light || (theme == AppTheme.System && SystemUsesLightTheme());
        var source = new Uri($"/m4xmudovv;component/Themes/{(useLight ? "Light" : "Dark")}.xaml", UriKind.Relative);
        var merged = Application.Current.Resources.MergedDictionaries;
        var dictionary = new ResourceDictionary { Source = source };
        // The theme dictionary is always the first merged dictionary (see App.xaml).
        if (merged.Count > 0) merged[0] = dictionary;
        else merged.Add(dictionary);
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception) { return false; }   // unknown -> keep the dark identity
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
        // Raised on a background thread.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(_theme)));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
