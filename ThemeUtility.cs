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
        private static readonly object _sampleLock = new();

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

            lock (_sampleLock)
            {
                lock (_cacheLock)
                {
                    if (_cachedWallpaperColor.HasValue && (DateTime.UtcNow - _cacheTimestamp).TotalSeconds < 2.0)
                        return _cachedWallpaperColor.Value;
                }

                Color? sampled = null;
                var sample = Core.Theming.WallpaperSampler.CaptureSingle(allowScreenCapture: true);
                if (sample is not null)
                {
                    var palette = Core.Theming.PaletteExtractor.Extract(sample.Bgra, sample.Width, sample.Height);
                    sampled = Core.Theming.PaletteExtractor.SelectSeed(palette)
                              ?? Core.Theming.WallpaperSampler.AverageColorFromSample(sample);
                }

                Color result = sampled ?? Color.FromRgb(36, 61, 111);

                if (sampled is null)
                {
                    Core.Theming.ThemeLog.Warn("AutoMatch", "Wallpaper unreadable; auto-match falling back to default.");
                }
                else
                {
                    lock (_cacheLock)
                    {
                        _cachedWallpaperColor = result;
                        _cacheTimestamp = DateTime.UtcNow;
                    }
                }

                return result;
            }
        }

        public static Color GetWallpaperMediaColor() => GetDominantWallpaperColor();
    }
}
