namespace DesktopFences.Core
{
    public enum AnimationStyle
    {
        Smooth,
        Bounce,
        Linear,
        None
    }

    public sealed class ThemeSettings
    {
        public string ThemeName { get; set; } = "Fluid Glass (Default)";
        public string PrimaryColor { get; set; } = "#0078D7";
        public string SecondaryColor { get; set; } = "#2B88D8";
        public string AccentColor { get; set; } = "#0078D7";
        public string BackgroundColor { get; set; } = "#01000000";
        public string SurfaceColor { get; set; } = "#151515";
        public string HeaderColor { get; set; } = "#22000000";
        public string FontColor { get; set; } = "#FFFFFFFF";
        public string SecondaryFontColor { get; set; } = "#FFAAAAAA";
        public string BorderColor { get; set; } = "#333333";
        public string SuccessColor { get; set; } = "#107C10";
        public string ErrorColor { get; set; } = "#E81123";
        public string WarningColor { get; set; } = "#FFB900";
        public double CornerRadius { get; set; } = 8.0;
        public AnimationStyle RollUpAnimation { get; set; } = AnimationStyle.Smooth;
        public string BackgroundMediaPath { get; set; } = "";
        public double MediaOpacity { get; set; } = 1.0;
    }
}
