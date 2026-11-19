using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;

namespace m4xmudovv.Services;

/// <summary>Applies an inexpensive DWM backdrop when supported and falls back to translucent WPF surfaces.</summary>
internal static class GlassEffectService
{
    private static readonly Dictionary<object, object> OriginalBrushes = new();
    private static ResourceDictionary? _resourceDictionary;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseHostBackdropBrush = 17;
    private const int BackdropNone = 1;
    private const int BackdropAcrylic = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, Border surface, bool enabled)
    {
        var resources = Application.Current.Resources.MergedDictionaries.Count > 0
            ? Application.Current.Resources.MergedDictionaries[0] : null;
        if (!ReferenceEquals(resources, _resourceDictionary))
        {
            OriginalBrushes.Clear();
            _resourceDictionary = resources;
        }
        if (resources is not null) ApplySharedBrushes(resources, enabled);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (enabled)
        {
            surface.Background = new SolidColorBrush(Color.FromArgb(218, 17, 19, 24));
            surface.BorderBrush = new SolidColorBrush(Color.FromArgb(110, 210, 225, 255));
            surface.BorderThickness = new Thickness(1);
            window.Opacity = 1;
            if (hwnd != IntPtr.Zero)
            {
                var backdrop = BackdropAcrylic;
                var hostBrush = 1;
                _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseHostBackdropBrush, ref hostBrush, sizeof(int));
            }
        }
        else
        {
            surface.Background = (Brush)window.FindResource("CanvasBrush");
            surface.ClearValue(Border.BorderBrushProperty);
            surface.BorderThickness = new Thickness(0);
            if (hwnd != IntPtr.Zero)
            {
                var backdrop = BackdropNone;
                var hostBrush = 0;
                _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseHostBackdropBrush, ref hostBrush, sizeof(int));
            }
        }
    }

    private static void ApplySharedBrushes(ResourceDictionary resources, bool enabled)
    {
        foreach (var key in resources.Keys)
        {
            if (key is not string name || (!name.StartsWith("Bg_", StringComparison.Ordinal) &&
                name is not ("CanvasBrush" or "PanelBrush" or "PanelAltBrush" or "LineBrush" or "DividerBrush"))) continue;
            if (resources[key] is not SolidColorBrush brush) continue;
            if (enabled)
            {
                if (!OriginalBrushes.ContainsKey(key)) OriginalBrushes[key] = brush;
                var color = brush.Color;
                var alpha = name is "CanvasBrush" or "PanelBrush" or "PanelAltBrush" ? (byte)188 : (byte)205;
                resources[key] = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            }
            else if (OriginalBrushes.TryGetValue(key, out var original)) resources[key] = original;
        }
        if (!enabled) OriginalBrushes.Clear();
    }
}
