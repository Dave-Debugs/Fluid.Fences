using System;
using System.Windows.Media;

namespace DesktopFences
{
    public static class ThemeUtility
    {
        private static readonly Color FallbackColor = Color.FromRgb(36, 61, 111);
        private static Color? _cachedWallpaperColor;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static double _cacheTtlSeconds = 2.0;
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
                if (_cachedWallpaperColor.HasValue && (DateTime.UtcNow - _cacheTimestamp).TotalSeconds < _cacheTtlSeconds)
                    return _cachedWallpaperColor.Value;
            }

            lock (_sampleLock)
            {
                lock (_cacheLock)
                {
                    if (_cachedWallpaperColor.HasValue && (DateTime.UtcNow - _cacheTimestamp).TotalSeconds < _cacheTtlSeconds)
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

                Color result = sampled ?? FallbackColor;

                if (sampled is null)
                {
                    Core.Theming.ThemeLog.Warn("AutoMatch", "Wallpaper unreadable; auto-match falling back to default.");
                }

                lock (_cacheLock)
                {
                    _cachedWallpaperColor = result;
                    _cacheTimestamp = DateTime.UtcNow;
                    _cacheTtlSeconds = sampled is null ? 5.0 : 2.0;
                }

                return result;
            }
        }
    }
}
