using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using m4xmudovv.Models;

namespace m4xmudovv.Services;

internal sealed class WidgetCatalogService
{
    private const long MaxArchiveBytes = 50L * 1024 * 1024;
    private const long MaxUncompressedBytes = 200L * 1024 * 1024;
    private const int MaxEntries = 500;
    private static readonly string[] ForbiddenExtensions = [".exe", ".dll", ".bat", ".cmd", ".ps1", ".com", ".scr", ".msi"];
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "m4xmudovv", "Widgets");

    public IReadOnlyList<WidgetDefinition> BuiltIns { get; } =
    [
        new() { Id = "builtin.clock", Name = "Digital Clock", Description = "A lightweight digital clock with date and 12/24-hour display.", Author = "m4xmudovv", Kind = WidgetKind.DigitalClock, BuiltIn = true, DefaultWidth = 300, DefaultHeight = 150 },
        new() { Id = "builtin.weather", Name = "Weather", Description = "Current weather from Open-Meteo after you choose a location.", Author = "m4xmudovv", Kind = WidgetKind.Weather, BuiltIn = true, DefaultWidth = 320, DefaultHeight = 190 },
        new() { Id = "builtin.calendar", Name = "Calendar", Description = "A compact monthly calendar with the current day highlighted.", Author = "m4xmudovv", Kind = WidgetKind.Calendar, BuiltIn = true, DefaultWidth = 300, DefaultHeight = 250 },
        new() { Id = "builtin.system", Name = "System Monitor", Description = "Live CPU and memory usage with low-frequency updates.", Author = "m4xmudovv", Kind = WidgetKind.SystemMonitor, BuiltIn = true, DefaultWidth = 300, DefaultHeight = 180 },
        new() { Id = "builtin.music", Name = "Music Player", Description = "A safe shell for Windows media controls when a media session is available.", Author = "m4xmudovv", Kind = WidgetKind.MusicPlayer, BuiltIn = true, DefaultWidth = 320, DefaultHeight = 150 }
    ];

    public WidgetCatalogService() => Directory.CreateDirectory(_root);

    public IReadOnlyList<WidgetDefinition> DiscoverImported()
    {
        var result = new List<WidgetDefinition>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            try
            {
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                var manifest = JsonSerializer.Deserialize<WidgetManifest>(File.ReadAllText(manifestPath));
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || !IsSafeId(manifest.Id) || !string.Equals(manifest.EntryPoint, "index.html", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory, "index.html"))) continue;
                result.Add(ToDefinition(manifest, directory));
            }
            catch (Exception ex) { AppLog.Warn($"Skipping invalid widget package '{directory}'.", ex); }
        }
        return result;
    }

    public (WidgetDefinition? Definition, string Message) ImportZip(string archivePath)
    {
        try
        {
            var info = new FileInfo(archivePath);
            if (!info.Exists) return (null, "The selected ZIP file does not exist.");
            if (info.Length > MaxArchiveBytes) return (null, "Widget ZIP files must be 50 MB or smaller.");
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaxEntries) return (null, "The widget contains too many files.");
            if (archive.Entries.Sum(entry => Math.Max(0, entry.Length)) > MaxUncompressedBytes) return (null, "The widget expands beyond the 200 MB safety limit.");
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null) return (null, "The ZIP must contain a root manifest.json file.");
            var manifest = JsonSerializer.Deserialize<WidgetManifest>(ReadEntry(manifestEntry));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name)) return (null, "manifest.json is missing id or name.");
            if (!string.Equals(manifest.EntryPoint, "index.html", StringComparison.OrdinalIgnoreCase)) return (null, "Only index.html entry points are supported.");
            if (archive.GetEntry("index.html") is null) return (null, "The ZIP must contain index.html at its root.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Split('/').Any(part => part == "..")) return (null, "The ZIP contains an unsafe path.");
                if (!paths.Add(normalized)) return (null, "The ZIP contains duplicate paths.");
                if (ForbiddenExtensions.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase)) return (null, "Executable files are not allowed in widget packages.");
            }
            var safeId = manifest.Id;
            if (!IsSafeId(safeId)) return (null, "The widget id contains unsupported characters.");
            var destination = Path.Combine(_root, safeId);
            if (Directory.Exists(destination)) return (null, "A widget with this id is already installed. Remove it before importing a replacement.");
            var temporary = Path.Combine(_root, "." + safeId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var output = Path.GetFullPath(Path.Combine(temporary, entry.FullName));
                    if (!output.StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return (null, "The ZIP contains an unsafe destination path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var input = entry.Open();
                    using var outputStream = File.Create(output);
                    input.CopyTo(outputStream);
                }
                Directory.Move(temporary, destination);
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
            return (ToDefinition(manifest, destination), "Widget imported successfully.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Widget ZIP import failed.", ex);
            return (null, "The widget could not be imported. Check the package and try again.");
        }
    }

    public void Remove(WidgetDefinition widget)
    {
        if (widget.BuiltIn || string.IsNullOrWhiteSpace(widget.PackageDirectory)) return;
        try { if (Directory.Exists(widget.PackageDirectory)) Directory.Delete(widget.PackageDirectory, true); }
        catch (Exception ex) { AppLog.Warn($"Could not remove widget '{widget.Id}'.", ex); }
    }

    private static string ReadEntry(ZipArchiveEntry entry) { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }
    private static bool IsSafeId(string id)
    {
        return id.Length >= 3 && id.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') && !id.Contains("..", StringComparison.Ordinal);
    }
    private static WidgetDefinition ToDefinition(WidgetManifest m, string directory) => new() { Id = m.Id, Name = m.Name, Version = m.Version ?? "1.0.0", Author = m.Author ?? "Unknown", Description = m.Description ?? "Imported HTML widget.", Kind = WidgetKind.Html, BuiltIn = false, PackageDirectory = directory, EntryPoint = m.EntryPoint, DefaultWidth = Math.Clamp(m.Width ?? 320, 160, 1200), DefaultHeight = Math.Clamp(m.Height ?? 180, 100, 900) };

    private sealed class WidgetManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Version { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string EntryPoint { get; set; } = "index.html";
        public int? Width { get; set; }
        public int? Height { get; set; }
    }
}
