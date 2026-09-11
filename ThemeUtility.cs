using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopFences
{
    public static class ThemeUtility
    {
        private static Color? _cachedWallpaperColor;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly object _cacheLock = new();

        public static void InvalidateWallpaperCache()
        {
            lock (_cacheLock) { _cachedWallpaperColor = null; }
        }

        public static Color GetDominantWallpaperColor()
        {
            lock (_cacheLock)
            {
                if (_cachedWallpaperColor.HasValue && (DateTime.UtcNow - _cacheTimestamp).TotalSeconds < 2.0)
                    return _cachedWallpaperColor.Value;
            }

            var sampled = Core.Theming.WallpaperSampler.AverageColor();
            Color result = sampled ?? Colors.Black;

            if (sampled is null)
                Core.Theming.ThemeLog.Warn("AutoMatch", "Wallpaper unreadable; auto-match falling back to black.");

            lock (_cacheLock)
            {
                _cachedWallpaperColor = result;
                _cacheTimestamp = DateTime.UtcNow;
            }

            return result;
        }
    }
}

