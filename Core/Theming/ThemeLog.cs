using System;
using System.Diagnostics;

namespace DesktopFences.Core.Theming
{
    internal static class ThemeLog
    {
        internal static void Warn(string context, string message)
        {
            Debug.WriteLine($"[Theming] {context}: {message}");
            App.WriteLogLine("app_error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Theming_{context}: {message}");
        }

        internal static void Error(string context, Exception ex)
        {
            Debug.WriteLine($"[Theming] {context}: {ex}");
            App.WriteLogLine("app_error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Theming_{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        internal static void Info(string context, string message)
        {
            Debug.WriteLine($"[Theming] {context}: {message}");
            App.WriteLogLine("app_error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Theming_{context}: {message}");
        }
    }
}
