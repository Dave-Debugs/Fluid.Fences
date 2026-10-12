using System;
using System.Text.Json.Serialization;

namespace DesktopFences.Core
{
    public enum AnimationStyle
    {
        Smooth,
        Bounce,
        Linear,
        None
    }

    public class ThemeSettings : ObservableObject
    {
        private string _themeName = "Fluid Glass (Default)";
        public string ThemeName
        {
            get => _themeName;
            set => SetProperty(ref _themeName, value);
        }

        private string _primaryColor = "#0078D7";
        public string PrimaryColor
        {
            get => _primaryColor;
            set => SetProperty(ref _primaryColor, value);
        }

        private string _secondaryColor = "#2B88D8";
        public string SecondaryColor
        {
            get => _secondaryColor;
            set => SetProperty(ref _secondaryColor, value);
        }

        private string _accentColor = "#0078D7";
        public string AccentColor
        {
            get => _accentColor;
            set => SetProperty(ref _accentColor, value);
        }

        private string _backgroundColor = "#01000000";
        public string BackgroundColor
        {
            get => _backgroundColor;
            set => SetProperty(ref _backgroundColor, value);
        }

        private string _surfaceColor = "#151515";
        public string SurfaceColor
        {
            get => _surfaceColor;
            set => SetProperty(ref _surfaceColor, value);
        }

        private string _headerColor = "#22000000";
        public string HeaderColor
        {
            get => _headerColor;
            set => SetProperty(ref _headerColor, value);
        }

        private string _fontColor = "#FFFFFFFF";
        public string FontColor
        {
            get => _fontColor;
            set => SetProperty(ref _fontColor, value);
        }

        private string _secondaryFontColor = "#FFAAAAAA";
        public string SecondaryFontColor
        {
            get => _secondaryFontColor;
            set => SetProperty(ref _secondaryFontColor, value);
        }

        private string _borderColor = "#333333";
        public string BorderColor
        {
            get => _borderColor;
            set => SetProperty(ref _borderColor, value);
        }

        private string _successColor = "#107C10";
        public string SuccessColor
        {
            get => _successColor;
            set => SetProperty(ref _successColor, value);
        }

        private string _errorColor = "#E81123";
        public string ErrorColor
        {
            get => _errorColor;
            set => SetProperty(ref _errorColor, value);
        }

        private string _warningColor = "#FFB900";
        public string WarningColor
        {
            get => _warningColor;
            set => SetProperty(ref _warningColor, value);
        }

        private double _cornerRadius = 8.0;
        public double CornerRadius
        {
            get => _cornerRadius;
            set => SetProperty(ref _cornerRadius, value);
        }

        private AnimationStyle _rollUpAnimation = AnimationStyle.Smooth;
        public AnimationStyle RollUpAnimation
        {
            get => _rollUpAnimation;
            set => SetProperty(ref _rollUpAnimation, value);
        }

        private string _backgroundMediaPath = "";
        public string BackgroundMediaPath
        {
            get => _backgroundMediaPath;
            set => SetProperty(ref _backgroundMediaPath, value);
        }

        private double _mediaOpacity = 1.0;
        public double MediaOpacity
        {
            get => _mediaOpacity;
            set => SetProperty(ref _mediaOpacity, value);
        }
    }
}
