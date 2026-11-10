using System;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace m4xmudovv.Services;

/// <summary>What Windows itself currently says about this app's startup entry.</summary>
/// <param name="Registered">A "m4xmudovv" value exists under HKCU\...\Run.</param>
/// <param name="PathMatches">That value launches the copy of the app that is running now.</param>
/// <param name="Approved">Windows has not disabled the entry (Task Manager / Settings > Startup apps).</param>
/// <param name="BlockedByPolicy">A Windows policy turns per-user startup programs off.</param>
public readonly record struct StartupState(bool Registered, bool PathMatches, bool Approved, bool BlockedByPolicy)
{
    /// <summary>True only if Windows will really launch the app at sign-in.</summary>
    public bool IsEnabled => Registered && Approved && !BlockedByPolicy;
}

public sealed record StartupResult(bool Success, string Message, StartupState State);

/// <summary>
/// "Start with Windows" for the current user, without administrator rights.
/// Windows decides whether a Run entry launches from TWO places, and both are handled here:
///  - HKCU\Software\Microsoft\Windows\CurrentVersion\Run                           (the entry itself)
///  - HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run  (enabled / disabled flag set by
///    Task Manager and Settings > Apps > Startup; first byte even = enabled, odd = disabled)
/// The state shown to the user is always read back from Windows, never assumed.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string PolicyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string ValueName = "m4xmudovv";

    // What Windows writes itself when the user switches a startup app on.
    private static readonly byte[] ApprovedEnabled = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The command line stored in the Run key, or null if the app's own path cannot be determined.</summary>
    public static string? GetLaunchCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;

        // Normal case: the app's own m4xmudovv.exe.
        if (!Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return $"\"{exe}\" --startup";

        // Started through "dotnet m4xmudovv.dll": the host executable alone would not start the app.
        var dll = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(dll) || !File.Exists(dll) ? null : $"\"{exe}\" \"{dll}\" --startup";
    }

    /// <summary>Reads the real startup state from Windows.</summary>
    public static StartupState GetState()
    {
        string? command = null;
        var approved = true;   // no flag at all means "enabled"

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            command = run?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        try
        {
            using var flags = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            if (flags?.GetValue(ValueName) is byte[] data && data.Length > 0)
                approved = (data[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }

        var expected = GetLaunchCommand();
        var matches = command is not null && expected is not null &&
                      string.Equals(command.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        return new StartupState(command is not null, matches, approved, IsBlockedByPolicy());
    }

    /// <summary>
    /// Turns startup on or off and then reads the state back from Windows. Success is only reported when Windows
    /// really shows the requested state, so the toggle can never claim "on" after a failed registration.
    /// </summary>
    public static StartupResult SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (IsBlockedByPolicy())
                    return Fail("Windows startup programs are turned off by a policy on this PC.");
                var command = GetLaunchCommand();
                if (command is null)
                    return Fail("The location of m4xmudovv.exe could not be determined.");

                using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
                {
                    if (run is null) return Fail("Windows did not allow access to your startup list.");
                    run.SetValue(ValueName, command, RegistryValueKind.String);
                }
                EnableInWindowsStartupList();
            }
            else
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                    run?.DeleteValue(ValueName, throwOnMissingValue: false);
                using (var flags = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
                    flags?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (UnauthorizedAccessException) { return Fail("Windows denied access to the current user's startup list."); }
        catch (SecurityException) { return Fail("Windows denied access to the current user's startup list."); }
        catch (IOException ex) { return Fail(ex.Message); }
        catch (Exception ex) { return Fail(ex.Message); }

        // Read back what Windows now says.
        var state = GetState();
        var confirmed = enabled ? state.IsEnabled && state.PathMatches : !state.Registered;
        if (!confirmed)
        {
            return Fail(enabled
                ? "Windows did not keep the startup entry (it may be blocked by security software or policy)."
                : "The startup entry could not be removed.");
        }

        AppLog.Info(enabled
            ? $"Start with Windows enabled: {GetLaunchCommand()}"
            : "Start with Windows disabled.");
        return new StartupResult(true, enabled ? "m4xmudovv will start when you sign in." : "m4xmudovv will no longer start automatically.", state);
    }

    /// <summary>
    /// Called at launch and when the Settings page opens: returns the real state, and repairs the stored path when
    /// the user has startup enabled but the app was moved or reinstalled (the entry would otherwise point nowhere).
    /// It never re-enables an entry the user disabled outside the app.
    /// </summary>
    public static StartupState Synchronize()
    {
        var state = GetState();
        if (state.Registered && state.Approved && !state.BlockedByPolicy && !state.PathMatches)
        {
            var command = GetLaunchCommand();
            if (command is not null)
            {
                try
                {
                    using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                    run?.SetValue(ValueName, command, RegistryValueKind.String);
                    AppLog.Info($"Start with Windows: entry path repaired to {command}");
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
                state = GetState();
            }
        }
        return state;
    }

    // If the user had disabled the entry in Task Manager / Settings, switching the toggle on must undo that,
    // otherwise Windows keeps ignoring the Run value. Writes exactly what Windows writes when it enables an item.
    private static void EnableInWindowsStartupList()
    {
        using var flags = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        if (flags?.GetValue(ValueName) is byte[] data && data.Length > 0 && (data[0] & 1) != 0)
            flags.SetValue(ValueName, ApprovedEnabled, RegistryValueKind.Binary);
    }

    private static bool IsBlockedByPolicy()
    {
        // DisableCurrentUserRun = 1 makes Windows ignore every per-user Run entry at sign-in.
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var policy = hive.OpenSubKey(PolicyKeyPath);
                if (policy?.GetValue("DisableCurrentUserRun") is int value && value == 1) return true;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return false;
    }

    private static StartupResult Fail(string message)
    {
        AppLog.Warn("Start with Windows failed: " + message);
        return new StartupResult(false, message, GetState());
    }
}
