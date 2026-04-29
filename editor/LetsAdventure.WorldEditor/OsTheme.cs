using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LetsAdventure.WorldEditor;

/// <summary>Windows light/dark preference for apps and optional immersive dark title bar.</summary>
internal static class OsTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>True when Windows is set to use dark mode for applications (Settings → Personalization → Colors).</summary>
    public static bool IsAppsDarkMode()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = k?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void TryApplyImmersiveDarkTitleBar(Window window)
    {
        if (!IsAppsDarkMode() || !OperatingSystem.IsWindowsVersionAtLeast(10))
            return;

        var helper = new WindowInteropHelper(window);
        var hwnd = helper.EnsureHandle();
        var useDark = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
    }
}
