using System;
using System.IO;
using System.Text;

namespace m4xmudovv.Services;

/// <summary>
/// Small thread-safe file logger: %LOCALAPPDATA%\m4xmudovv\logs\m4xmudovv.log (rotated at ~1 MB to .old).
/// Logging can never throw into the application.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "m4xmudovv",
        "logs",
        "m4xmudovv.log");

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? exception = null) => Write("WARN ", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                   .Append(" [").Append(level).Append("] [T").Append(Environment.CurrentManagedThreadId).Append("] ")
                   .AppendLine(message);
            if (exception is not null) AppendException(builder, exception);

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes) File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Never let logging break the app.
        }
    }

    private static void AppendException(StringBuilder builder, Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            builder.Append("    ").Append(current.GetType().FullName).Append(": ").Append(current.Message)
                   .Append(" (HRESULT 0x").Append(current.HResult.ToString("X8")).AppendLine(")");
            current = current.InnerException;
        }
        if (!string.IsNullOrEmpty(exception.StackTrace)) builder.AppendLine(exception.StackTrace);
    }
}
