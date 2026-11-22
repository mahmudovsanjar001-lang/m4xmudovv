using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows;

namespace m4xmudovv.Models;

public enum WidgetKind
{
    DigitalClock,
    Weather,
    Calendar,
    SystemMonitor,
    MusicPlayer,
    Html
}

public sealed class WidgetDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = "m4xmudovv";
    public string Version { get; init; } = "1.0.0";
    public WidgetKind Kind { get; init; }
    public bool BuiltIn { get; init; }
    public string? PackageDirectory { get; init; }
    public string? EntryPoint { get; init; }
    public int DefaultWidth { get; init; } = 280;
    public int DefaultHeight { get; init; } = 160;
    [JsonIgnore] public string StatusText => BuiltIn ? "Built-in" : "Imported ZIP";
}

public sealed class WidgetInstanceState
{
    public string WidgetId { get; set; } = string.Empty;
    public string? MonitorId { get; set; }
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 40;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 160;
    public bool Enabled { get; set; } = true;
    public bool Locked { get; set; }
    public bool AlwaysOnTop { get; set; }
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
