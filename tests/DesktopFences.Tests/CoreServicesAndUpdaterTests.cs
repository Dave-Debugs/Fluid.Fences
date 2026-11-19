using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using DesktopFences;
using DesktopFences.Core.Services;
using FluentAssertions;
using Xunit;

namespace DesktopFences.Tests
{
    public class CoreServicesAndUpdaterTests
    {
        [Fact]
        public void Updater_ParseExpectedHash_ParsesCoreutilsFormat()
        {
            string content = """
                e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  FluidFences_Setup.exe
                d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2d2  OtherFile.zip
                """;

            string hash = Updater.ParseExpectedHash(content, "FluidFences_Setup.exe");
            hash.Should().Be("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
        }

        [Fact]
        public void Updater_ParseExpectedHash_ParsesOpenSslFormat()
        {
            string content = "SHA256 (FluidFences_Setup.exe) = aaaaaaaa98fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852bbbb";

            string hash = Updater.ParseExpectedHash(content, "FluidFences_Setup.exe");
            hash.Should().Be("AAAAAAAA98FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852BBBB");
        }

        [Fact]
        public void Updater_ParseExpectedHash_RejectsPrefixSpoofedFilename()
        {
            string content = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  evil_FluidFences_Setup.exe";

            string hash = Updater.ParseExpectedHash(content, "FluidFences_Setup.exe");
            hash.Should().BeEmpty();
        }

        [Theory]
        [InlineData(".pdf", ".pdf, .docx, .xlsx", true)]
        [InlineData(".PDF", ".pdf, .docx", true)]
        [InlineData(".png", "png; jpg; gif", true)]
        [InlineData(".exe", ".txt, .md", false)]
        [InlineData("", ".pdf", false)]
        [InlineData(".pdf", "", false)]
        public void AutoOrganizeService_MatchesExtensionFilter_MatchesCorrectly(
            string ext, string filter, bool expected)
        {
            bool result = AutoOrganizeService.MatchesExtensionFilter(ext, filter);
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData(".png", "Images")]
        [InlineData(".docx", "Documents")]
        [InlineData(".mp4", "Media")]
        [InlineData(".lnk", "Shortcuts & Apps")]
        [InlineData(".zip", "Archives")]
        [InlineData(".7z", "Archives")]
        [InlineData(".cs", "Code & Scripts")]
        [InlineData(".py", "Code & Scripts")]
        [InlineData(".json", "Code & Scripts")]
        [InlineData(".xyzUnknown", "Other Files")]
        public void AutoOrganizeService_ResolveCategoryForExtension_ResolvesExpectedCategory(
            string ext, string expectedCategory)
        {
            var (category, _) = AutoOrganizeService.ResolveCategoryForExtension(ext);
            category.Should().Be(expectedCategory);
        }

        [Theory]
        [InlineData("Fluid Fence", true)]
        [InlineData("My First Fence", true)]
        [InlineData("Main", true)]
        [InlineData("New Tab", true)]
        [InlineData("Development Work", false)]
        public void AutoOrganizeService_IsDefaultFenceTitle_RecognizesDefaultTitles(
            string title, bool expected)
        {
            bool isDefault = AutoOrganizeService.IsDefaultFenceTitle(title);
            isDefault.Should().Be(expected);
        }

        [Theory]
        [InlineData("valid-fence-id-123", "valid-fence-id-123")]
        [InlineData("fence/with/slashes", "fencewithslashes")]
        [InlineData("fence\\with\\backslashes", "fencewithbackslashes")]
        public void FenceVaultService_SanitizeFenceId_StripsSlashesAndSpecialChars(
            string input, string expected)
        {
            string sanitized = FenceVaultService.SanitizeFenceId(input);
            sanitized.Should().Be(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("CON")]
        [InlineData("nul")]
        [InlineData("AUX")]
        [InlineData("PRN")]
        [InlineData("COM1")]
        [InlineData("LPT9")]
        [InlineData("con.txt")]
        public void FenceVaultService_SanitizeFenceId_GeneratesGuidForInvalidOrDangerousIds(string? input)
        {
            string sanitized = FenceVaultService.SanitizeFenceId(input);
            sanitized.Should().NotBeNullOrWhiteSpace();
            sanitized.Should().NotBe(".");
            sanitized.Should().NotBe("..");
            Guid.TryParse(sanitized, out _).Should().BeTrue();
        }

        [Fact]
        public void FenceVaultService_ResolveVaultCollision_AppendsNumericSuffixes()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"ff_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string firstFile = Path.Combine(tempDir, "sample.txt");
                File.WriteAllText(firstFile, "hello");

                string candidate1 = FenceVaultService.ResolveVaultCollision(tempDir, "sample.txt");
                candidate1.Should().Be(Path.Combine(tempDir, "sample (1).txt"));

                File.WriteAllText(candidate1, "world");
                string candidate2 = FenceVaultService.ResolveVaultCollision(tempDir, "sample.txt");
                candidate2.Should().Be(Path.Combine(tempDir, "sample (2).txt"));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task ThemeUtility_GetDominantWallpaperColor_IsThreadSafe()
        {
            ThemeUtility.InvalidateWallpaperCache();

            var tasks = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => ThemeUtility.GetDominantWallpaperColor()))
                .ToArray();

            Color[] results = await Task.WhenAll(tasks);
            results.Should().HaveCount(4);
            foreach (var col in results)
            {
                col.A.Should().Be(255);
            }
        }

        [Fact]
        public void TestEnvironment_ConfigFolder_IsIsolatedFromLiveLocalAppData()
        {
            string liveDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopFences");

            Path.GetFullPath(App.ConfigFolder).Should().NotBeEquivalentTo(Path.GetFullPath(liveDir));
        }

        [Theory]
        [InlineData("https://github.com/Dave-Debugs/Fluid.Fences/releases/download/v2.4.0/FluidFences-2.4.0-Setup.exe", true)]
        [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123", true)]
        [InlineData("https://release-assets.githubusercontent.com/abc/file.exe", true)]
        [InlineData("http://github.com/Dave-Debugs/Fluid.Fences/releases/download/v2.4.0/setup.exe", false)]
        [InlineData("https://evilgithub.com/setup.exe", false)]
        [InlineData("https://github.com.evil.com/setup.exe", false)]
        [InlineData("https://example.com/setup.exe", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Updater_IsTrustedDownloadUri_EnforcesHttpsAndGitHubHosts(string? url, bool expected)
        {
            Updater.IsTrustedDownloadUri(url).Should().Be(expected);
        }

        [Fact]
        public void FenceVaultService_ResolveVaultCollision_PreservesDotsInDirectoryNames()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"ff_dir_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "Project.v1"));

                string resolved = FenceVaultService.ResolveVaultCollision(tempDir, "Project.v1", isDirectory: true);
                resolved.Should().Be(Path.Combine(tempDir, "Project.v1 (1)"));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void FenceVaultService_DirectoryContainmentChecks_RejectSiblingPrefixPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), $"ff_vault_{Guid.NewGuid():N}");
            string sibling = root + "2";
            string directFile = Path.Combine(root, "doc.txt");
            string nestedFile = Path.Combine(root, "sub", "doc.txt");
            string siblingFile = Path.Combine(sibling, "doc.txt");

            FenceVaultService.IsInsideDirectory(directFile, root).Should().BeTrue();
            FenceVaultService.IsInsideDirectory(nestedFile, root).Should().BeTrue();
            FenceVaultService.IsInsideDirectory(siblingFile, root).Should().BeFalse();

            FenceVaultService.IsDirectChildOfDirectory(directFile, root).Should().BeTrue();
            FenceVaultService.IsDirectChildOfDirectory(nestedFile, root).Should().BeFalse();
            FenceVaultService.IsDirectChildOfDirectory(siblingFile, root).Should().BeFalse();
        }

        [Theory]
        [InlineData(@"\\attacker\share\payload.ico", true)]
        [InlineData("//attacker/share/file.exe", true)]
        [InlineData(@"\??\UNC\attacker\share\file.exe", true)]
        [InlineData("\"\\\\attacker\\share\\file.exe\"", true)]
        [InlineData(@"C:\Windows\System32\notepad.exe", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ShellLinkParser_IsUncPath_DetectsAllUncVariants(string? path, bool expected)
        {
            ShellLinkParser.IsUncPath(path).Should().Be(expected);
        }

        [Theory]
        [InlineData(".exe", true)]
        [InlineData(".ps1", true)]
        [InlineData(".docm", true)]
        [InlineData(".iso", true)]
        [InlineData(".lnk", true)]
        [InlineData(".url", true)]
        [InlineData(@"C:\Folder\run.BAT", true)]
        [InlineData(".txt", false)]
        [InlineData(".png", false)]
        [InlineData(".pdf", false)]
        [InlineData("", false)]
        public void ShellLinkParser_IsDangerousExtension_FlagsExecutableAndScriptTypes(string pathOrExt, bool expected)
        {
            ShellLinkParser.IsDangerousExtension(pathOrExt).Should().Be(expected);
        }

        [Fact]
        public void ShellLinkParser_ParseLnkBytes_ExtractsLocalTargetArgumentsWorkDirAndIcon()
        {
            byte[] payload = BuildSyntheticLnk(
                localBasePath: @"C:\Tools\demo.exe",
                workingDir: @"C:\Tools",
                arguments: "--quiet --test",
                iconLocation: @"C:\Tools\demo.ico");

            ShellLinkInfo parsed = ShellLinkParser.ParseLnkBytes(payload);
            parsed.TargetPath.Should().Be(@"C:\Tools\demo.exe");
            parsed.WorkingDirectory.Should().Be(@"C:\Tools");
            parsed.Arguments.Should().Be("--quiet --test");
            parsed.IconLocation.Should().Be(@"C:\Tools\demo.ico");
        }

        [Fact]
        public void ShellLinkParser_ParseLnkBytes_RejectsMalformedHeader()
        {
            ShellLinkInfo parsed = ShellLinkParser.ParseLnkBytes(new byte[128]);
            parsed.TargetPath.Should().BeEmpty();
            parsed.Arguments.Should().BeEmpty();
        }

        [Fact]
        public void ShellLinkParser_ParseUrl_ExtractsUrlAndIconMetadata()
        {
            string tempUrl = Path.Combine(Path.GetTempPath(), $"ff_shortcut_{Guid.NewGuid():N}.url");
            try
            {
                File.WriteAllText(tempUrl, """
                    [InternetShortcut]
                    URL=https://github.com/Dave-Debugs/Fluid.Fences
                    IconFile=C:\Icons\app.ico
                    IconIndex=2
                    """);

                InternetShortcutInfo parsed = ShellLinkParser.ParseUrlFile(tempUrl);
                parsed.Url.Should().Be("https://github.com/Dave-Debugs/Fluid.Fences");
                parsed.IconFile.Should().Be(@"C:\Icons\app.ico");
                parsed.IconIndex.Should().Be(2);
            }
            finally
            {
                if (File.Exists(tempUrl)) File.Delete(tempUrl);
            }
        }

        private static byte[] BuildSyntheticLnk(
            string localBasePath,
            string workingDir,
            string arguments,
            string iconLocation)
        {
            const uint shellLinkHeaderSize = 0x4Cu;
            const uint hasLinkInfoFlag = 0x02u;
            const uint hasWorkingDirFlag = 0x10u;
            const uint hasArgumentsFlag = 0x20u;
            const uint hasIconLocationFlag = 0x40u;
            const uint isUnicodeFlag = 0x80u;
            const uint linkFlags = hasLinkInfoFlag | hasWorkingDirFlag | hasArgumentsFlag | hasIconLocationFlag | isUnicodeFlag;
            const uint volumeIdAndLocalBasePathFlag = 1u;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

            bw.Write(shellLinkHeaderSize);
            bw.Write(new byte[]
            {
                0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
            });
            bw.Write(linkFlags);
            bw.Write(new byte[shellLinkHeaderSize - 24]);

            byte[] pathBytes = Encoding.Latin1.GetBytes(localBasePath + "\0");
            const uint linkInfoHeaderSize = 28;
            uint localBasePathOffset = linkInfoHeaderSize;
            uint commonPathSuffixOffset = linkInfoHeaderSize + (uint)pathBytes.Length;
            uint linkInfoSize = commonPathSuffixOffset + 1;

            bw.Write(linkInfoSize);
            bw.Write(linkInfoHeaderSize);
            bw.Write(volumeIdAndLocalBasePathFlag);
            bw.Write(0u);
            bw.Write(localBasePathOffset);
            bw.Write(0u);
            bw.Write(commonPathSuffixOffset);
            bw.Write(pathBytes);
            bw.Write((byte)0);

            WriteCountedUnicodeString(bw, workingDir);
            WriteCountedUnicodeString(bw, arguments);
            WriteCountedUnicodeString(bw, iconLocation);

            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteCountedUnicodeString(BinaryWriter bw, string value)
        {
            bw.Write((ushort)value.Length);
            bw.Write(Encoding.Unicode.GetBytes(value));
        }
    }
}
