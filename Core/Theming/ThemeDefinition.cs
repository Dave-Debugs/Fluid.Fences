using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopFences.Core.Theming
{

    public sealed class ThemeDefinition
    {

        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;

        [JsonPropertyName("id")] public string Id { get; set; } = "";

        [JsonPropertyName("name")] public string Name { get; set; } = "Untitled Theme";

        [JsonPropertyName("author")] public string Author { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";

        [JsonPropertyName("category")] public string Category { get; set; } = "Dark";

        [JsonPropertyName("baseTheme")] public string BaseTheme { get; set; } = "Dark";

        [JsonPropertyName("pattern")] public string? Pattern { get; set; }

        [JsonPropertyName("loopFile")] public string? LoopFile { get; set; }

        [JsonIgnore] public bool IsBuiltIn { get; set; }

        [JsonIgnore] public string? SourcePath { get; set; }

        [JsonPropertyName("colors")] public ThemeColors Colors { get; set; } = new();
        [JsonPropertyName("shape")] public ThemeShape Shape { get; set; } = new();
        [JsonPropertyName("typography")] public ThemeTypography Typography { get; set; } = new();
        [JsonPropertyName("motion")] public ThemeMotion Motion { get; set; } = new();

        [JsonPropertyName("customTokens")]
        public Dictionary<string, string> CustomTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public ThemeDefinition Clone() => ThemeCatalog.Clone(this);

        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? (Id ?? "Theme") : Name;

        public static readonly ThemeDefinition Fallback = new()
        {
            Id = "obsidian-prism",
            Name = "Fluid Glass (Default)",
            BaseTheme = "Dark",
            Colors = new ThemeColors
            {
                Primary = "#FF0078D4",
                PrimaryHover = "#FF1084DF",
                PrimaryPressed = "#FF006CBE",
                Secondary = "#FF2B88D8",
                Accent = "#FF0078D4",
                Background = "#991E1E24",
                Surface = "#2EFFFFFF",
                SurfaceSubtle = "#19FFFFFF",
                SurfaceHover = "#1CFFFFFF",
                SurfaceSelected = "#38FFFFFF",
                Header = "#CC141418",
                Border = "#4DFFFFFF",
                BorderSubtle = "#24FFFFFF",
                FocusRing = "#FF0078D4",
                TextPrimary = "#FFFFFFFF",
                TextSecondary = "#FFB0B0B8",
                TextDisabled = "#FF6D6D75",
                TextOnAccent = "#FFFFFFFF",
                Glyph = "#FFB0B0B8",
                GlyphHover = "#FFFFFFFF",
                Success = "#FF107C10",
                Error = "#FFE81123",
                Warning = "#FFFFB900",
                Info = "#FF0078D4",
                SelectionFill = "#330078D4",
                SelectionStroke = "#FF0078D4",
                DropTarget = "#550078D4",
                ScrollThumb = "#4DFFFFFF",
                ScrollThumbHover = "#8CFFFFFF",
                ScrollThumbActive = "#C4FFFFFF"
            },
            Shape = new ThemeShape
            {
                CornerRadius = 8,
                CornerRadiusSmall = 4,
                BorderThickness = 1,
                ShadowOpacity = 0.35,
                ShadowBlurRadius = 16,
                ShadowDepth = 2
            },
            Typography = new ThemeTypography
            {
                FontFamily = "Segoe UI Variable Text, Segoe UI Variable, Segoe UI, Arial",
                MonospaceFontFamily = "Cascadia Mono, Consolas, Courier New",
                Scale = 1.0,
                TitleWeight = "SemiBold"
            },
            Motion = new ThemeMotion
            {
                SpeedMultiplier = 1.0,
                RollUpStyle = "Glass",
                ThemeTransitionMs = 180
            }
        };
    }

    public sealed class ThemeColors
    {

        [JsonPropertyName("primary")] public string? Primary { get; set; }
        [JsonPropertyName("primaryHover")] public string? PrimaryHover { get; set; }
        [JsonPropertyName("primaryPressed")] public string? PrimaryPressed { get; set; }
        [JsonPropertyName("secondary")] public string? Secondary { get; set; }
        [JsonPropertyName("accent")] public string? Accent { get; set; }

        [JsonPropertyName("background")] public string? Background { get; set; }

        [JsonPropertyName("surface")] public string? Surface { get; set; }

        [JsonPropertyName("surfaceSubtle")] public string? SurfaceSubtle { get; set; }

        [JsonPropertyName("surfaceHover")] public string? SurfaceHover { get; set; }

        [JsonPropertyName("surfaceSelected")] public string? SurfaceSelected { get; set; }

        [JsonPropertyName("header")] public string? Header { get; set; }

        [JsonPropertyName("border")] public string? Border { get; set; }
        [JsonPropertyName("borderSubtle")] public string? BorderSubtle { get; set; }

        [JsonPropertyName("focusRing")] public string? FocusRing { get; set; }

        [JsonPropertyName("textPrimary")] public string? TextPrimary { get; set; }
        [JsonPropertyName("textSecondary")] public string? TextSecondary { get; set; }
        [JsonPropertyName("textDisabled")] public string? TextDisabled { get; set; }

        [JsonPropertyName("textOnAccent")] public string? TextOnAccent { get; set; }

        [JsonPropertyName("glyph")] public string? Glyph { get; set; }
        [JsonPropertyName("glyphHover")] public string? GlyphHover { get; set; }

        [JsonPropertyName("success")] public string? Success { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("warning")] public string? Warning { get; set; }
        [JsonPropertyName("info")] public string? Info { get; set; }

        [JsonPropertyName("selectionFill")] public string? SelectionFill { get; set; }

        [JsonPropertyName("selectionStroke")] public string? SelectionStroke { get; set; }

        [JsonPropertyName("dropTarget")] public string? DropTarget { get; set; }
        [JsonPropertyName("scrollThumb")] public string? ScrollThumb { get; set; }
        [JsonPropertyName("scrollThumbHover")] public string? ScrollThumbHover { get; set; }
        [JsonPropertyName("scrollThumbActive")] public string? ScrollThumbActive { get; set; }
    }

    public sealed class ThemeShape
    {

        [JsonPropertyName("cornerRadius")] public double? CornerRadius { get; set; }

        [JsonPropertyName("cornerRadiusSmall")] public double? CornerRadiusSmall { get; set; }
        [JsonPropertyName("borderThickness")] public double? BorderThickness { get; set; }

        [JsonPropertyName("shadowOpacity")] public double? ShadowOpacity { get; set; }
        [JsonPropertyName("shadowBlurRadius")] public double? ShadowBlurRadius { get; set; }
        [JsonPropertyName("shadowDepth")] public double? ShadowDepth { get; set; }
    }

    public sealed class ThemeTypography
    {
        [JsonPropertyName("fontFamily")] public string? FontFamily { get; set; }
        [JsonPropertyName("monospaceFontFamily")] public string? MonospaceFontFamily { get; set; }
        [JsonPropertyName("scale")] public double? Scale { get; set; }
        [JsonPropertyName("titleWeight")] public string? TitleWeight { get; set; }
    }

    public sealed class ThemeMotion
    {

        [JsonPropertyName("speedMultiplier")] public double? SpeedMultiplier { get; set; }

        [JsonPropertyName("rollUpStyle")] public string? RollUpStyle { get; set; }
        [JsonPropertyName("themeTransitionMs")] public int? ThemeTransitionMs { get; set; }
    }
}
