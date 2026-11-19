using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopFences
{
    public sealed class HexToBrushConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<uint, SolidColorBrush> BrushCache = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!Core.Theming.ThemeEngine.TryParseColor(value as string, out Color color))
                return Brushes.Transparent;

            if (color.A < 255 &&
                parameter is string backdropHex &&
                Core.Theming.ThemeEngine.TryParseColor(backdropHex, out Color backdrop))
            {
                color = Core.Theming.ColorSpace.CompositeOver(color, backdrop);
            }

            uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            if (BrushCache.TryGetValue(key, out var cached))
                return cached;

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            if (BrushCache.Count < 512)
                BrushCache.TryAdd(key, brush);
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
