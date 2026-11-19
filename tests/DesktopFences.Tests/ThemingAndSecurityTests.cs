using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using DesktopFences;
using DesktopFences.Core;
using DesktopFences.Core.Theming;
using FluentAssertions;
using Xunit;
using Color = System.Windows.Media.Color;

namespace DesktopFences.Tests;

public class ThemingAndSecurityTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(124, 92, 255)]
    [InlineData(30, 144, 255)]
    [InlineData(220, 53, 69)]
    public void ColorSpace_OklchRoundTrip_PreservesRgbWithinOneStep(byte r, byte g, byte b)
    {
        var original = Color.FromRgb(r, g, b);
        var (l, c, h) = ColorSpace.RgbToLch(original);
        var roundTripped = ColorSpace.FromLch(l, c, h);

        Math.Abs(roundTripped.R - original.R).Should().BeLessThanOrEqualTo(1);
        Math.Abs(roundTripped.G - original.G).Should().BeLessThanOrEqualTo(1);
        Math.Abs(roundTripped.B - original.B).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void ColorSpace_Contrast_BlackAndWhiteIs21To1()
    {
        double ratio = ColorSpace.Contrast(Colors.Black, Colors.White);
        ratio.Should().BeApproximately(21.0, 0.05);

        double same = ColorSpace.Contrast(Colors.CornflowerBlue, Colors.CornflowerBlue);
        same.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void ColorSpace_CompositeOver_BlendsAlphaCorrectly()
    {
        var over = Color.FromArgb(128, 255, 0, 0);
        var under = Color.FromRgb(0, 0, 255);

        var blended = ColorSpace.CompositeOver(over, under);
        blended.A.Should().Be(255);
        blended.R.Should().BeInRange(127, 128);
        blended.G.Should().Be(0);
        blended.B.Should().BeInRange(127, 128);
    }

    [Fact]
    public void PaletteExtractor_Extract_IsDeterministicAndFindsDominantSwatches()
    {
        byte[] bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < 16 * 16; i++)
        {
            int offset = i * 4;
            if (i < 128)
            {
                bgra[offset + 0] = 45;
                bgra[offset + 1] = 25;
                bgra[offset + 2] = 15;
                bgra[offset + 3] = 255;
            }
            else
            {
                bgra[offset + 0] = 240;
                bgra[offset + 1] = 200;
                bgra[offset + 2] = 40;
                bgra[offset + 3] = 255;
            }
        }

        var swatches1 = PaletteExtractor.Extract(bgra, 16, 16, clusters: 2);
        var swatches2 = PaletteExtractor.Extract(bgra, 16, 16, clusters: 2);

        swatches1.Should().HaveCount(2);
        swatches2.Should().HaveCount(2);
        swatches1[0].Color.Should().Be(swatches2[0].Color);
        swatches1[1].Color.Should().Be(swatches2[1].Color);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThemeGenerator_Generate_ProducesTextAndGlyphContrastCompliantTheme(bool darkMode)
    {
        var seed = Color.FromRgb(94, 129, 244);
        var generated = ThemeGenerator.Generate(seed, darkMode, "Unit Test Generated");

        Color backdrop = ColorSpace.EffectiveBackdrop(
            ThemeEngine.TryParseColor(generated.Colors.Background, out var bg) ? bg : ColorSpace.NeutralBackdrop);

        ThemeEngine.TryParseColor(generated.Colors.TextPrimary, out var textPrimary).Should().BeTrue();
        ThemeEngine.TryParseColor(generated.Colors.TextSecondary, out var textSecondary).Should().BeTrue();
        ThemeEngine.TryParseColor(generated.Colors.Glyph, out var glyph).Should().BeTrue();

        ColorSpace.Contrast(textPrimary, backdrop).Should().BeGreaterThanOrEqualTo(4.5);
        ColorSpace.Contrast(textSecondary, backdrop).Should().BeGreaterThanOrEqualTo(4.5);
        ColorSpace.Contrast(glyph, backdrop).Should().BeGreaterThanOrEqualTo(3.0);
    }

    [Theory]
    [InlineData("#FF112233", 0xFF, 0x11, 0x22, 0x33)]
    [InlineData("#112233", 0xFF, 0x11, 0x22, 0x33)]
    [InlineData("#ABC", 0xFF, 0xAA, 0xBB, 0xCC)]
    [InlineData("#8ABC", 0x88, 0xAA, 0xBB, 0xCC)]
    public void ThemeEngine_TryParseColor_ParsesValidHexFormats(
        string hex, byte expectedA, byte expectedR, byte expectedG, byte expectedB)
    {
        bool ok = ThemeEngine.TryParseColor(hex, out Color parsed);
        ok.Should().BeTrue();
        parsed.A.Should().Be(expectedA);
        parsed.R.Should().Be(expectedR);
        parsed.G.Should().Be(expectedG);
        parsed.B.Should().Be(expectedB);
    }

    [Fact]
    public async Task ThemeCatalog_ImportJsonWithPathTraversalId_SanitizesIdAndStaysInsideUserThemeFolder()
    {
        ThemeCatalog.Reload();
        string tempJson = Path.Combine(Path.GetTempPath(), $"traversal_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempJson, """
                {
                  "id": "..\\..\\evil_escape",
                  "name": "..\\..\\Evil Escape",
                  "baseTheme": "Dark",
                  "colors": {
                    "background": "#CC10141E",
                    "header": "#E6141A26",
                    "textPrimary": "#FFF4F6FB",
                    "textSecondary": "#FFB0B8C8",
                    "accent": "#FF7C5CFF"
                  }
                }
                """);

            var imported = await ThemeCatalog.ImportAsync(tempJson);
            imported.Should().NotBeNull();
            imported!.Id.Should().NotContain("..");
            imported.Id.Should().NotContain("\\");
            imported.Id.Should().NotContain("/");

            string expectedPath = Path.Combine(ThemeCatalog.UserThemeFolder, $"{imported.Id}.json");
            File.Exists(expectedPath).Should().BeTrue();

            ThemeCatalog.Delete(imported.Id).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }

    [Fact]
    public async Task ThemeCatalog_ImportZipExceedingEntrySize_RejectsArchive()
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"zipbomb_{Guid.NewGuid():N}.fftheme");
        try
        {
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("theme.json", CompressionLevel.Fastest);
                using var stream = entry.Open();
                byte[] chunk = new byte[1024 * 1024];
                for (int i = 0; i < 26; i++)
                {
                    await stream.WriteAsync(chunk);
                }
            }

            var result = await ThemeCatalog.ImportAsync(tempZip);
            result.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    [Fact]
    public void ThemeMigration_DefaultFluidGlassConfig_ResolvesToDefaultWithoutCreatingForkedTheme()
    {
        ThemeCatalog.Reload();
        var defaultConfig = new GlobalConfig();

        string resolvedId = ThemeMigration.ResolveThemeId(defaultConfig);
        resolvedId.Should().Be(ThemeCatalog.Default.Id);
        ThemeCatalog.Themes.Should().NotContain(t => t.Name == "Fluid Glass (Default) (Imported)");
    }

    [Fact]
    public async Task ConfigStore_ConcurrentUpdates_AreSerializedSafely()
    {
        var original = await ConfigStore.ReadAsync();
        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(i => ConfigStore.UpdateAsync(c => c.PauseMediaOnRollup = (i % 2 == 0)))
                .ToArray();

            await Task.WhenAll(tasks);

            var current = await ConfigStore.ReadAsync();
            current.Should().NotBeNull();
        }
        finally
        {
            await ConfigStore.UpdateAsync(c =>
            {
                c.PauseMediaOnRollup = original.PauseMediaOnRollup;
                c.ShowTaskbarIcon = original.ShowTaskbarIcon;
                c.ThemeId = original.ThemeId;
            });
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PaletteExtractor_Extract_ReturnsEmptyWhenClustersNonPositive(int clusters)
    {
        byte[] bgra = new byte[4 * 4 * 4];
        var result = PaletteExtractor.Extract(bgra, 4, 4, clusters);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ThemeEngine_ContrastRatio_CompositesSemiTransparentForeground()
    {
        var faintWhite = Color.FromArgb(20, 255, 255, 255);
        double ratio = ThemeEngine.ContrastRatio(faintWhite, Colors.Black);
        ratio.Should().BeLessThan(2.0);
    }

    [Fact]
    public void ThemeDefinition_Fallback_ReturnsFreshIsolatedClone()
    {
        var first = ThemeDefinition.Fallback;
        string originalName = first.Name;
        first.Name = "Mutated";

        var second = ThemeDefinition.Fallback;
        second.Name.Should().Be(originalName);
    }

    [Fact]
    public void ThemeDefinition_Deserialization_CoalescesNullNestedSections()
    {
        var parsed = JsonSerializer.Deserialize<ThemeDefinition>(
            """{"id":"t1","name":"Test","colors":null,"shape":null,"typography":null,"motion":null,"customTokens":null}""");

        parsed.Should().NotBeNull();
        parsed!.Colors.Should().NotBeNull();
        parsed.Shape.Should().NotBeNull();
        parsed.Typography.Should().NotBeNull();
        parsed.Motion.Should().NotBeNull();
        parsed.CustomTokens.Should().NotBeNull();
    }

    [Fact]
    public async Task ThemeCatalog_ImportJson_DoesNotShadowBuiltinThemeId()
    {
        ThemeCatalog.Reload();
        string builtinId = ThemeCatalog.Default.Id;
        string tempJson = Path.Combine(Path.GetTempPath(), $"shadow_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempJson, $$"""
                {
                  "id": "{{builtinId}}",
                  "name": "Shadow Attempt",
                  "baseTheme": "Dark",
                  "colors": {
                    "background": "#CC10141E",
                    "header": "#E6141A26",
                    "textPrimary": "#FFF4F6FB",
                    "textSecondary": "#FFB0B8C8",
                    "accent": "#FF7C5CFF"
                  }
                }
                """);

            var imported = await ThemeCatalog.ImportAsync(tempJson);
            imported.Should().NotBeNull();
            imported!.Id.Should().NotBeEquivalentTo(builtinId);

            ThemeCatalog.Get(builtinId).IsBuiltIn.Should().BeTrue();
            ThemeCatalog.Delete(builtinId).Should().BeFalse();
            ThemeCatalog.Delete(imported.Id).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }

    [Fact]
    public async Task ThemeCatalog_ImportJson_RejectsOversizedJsonFile()
    {
        string tempJson = Path.Combine(Path.GetTempPath(), $"huge_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempJson, new string(' ', (1024 * 1024) + 64));
            var result = await ThemeCatalog.ImportAsync(tempJson);
            result.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }
}
