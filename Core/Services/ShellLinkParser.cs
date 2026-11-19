using System;
using System.Collections.Frozen;
using System.IO;
using System.Text;

namespace DesktopFences.Core.Services
{
    public sealed record ShellLinkInfo(
        string TargetPath,
        string Arguments,
        string WorkingDirectory,
        string IconLocation);

    public sealed record InternetShortcutInfo(
        string Url,
        string IconFile,
        int IconIndex);

    public static class ShellLinkParser
    {
        private const uint ShellLinkHeaderSize = 0x4C;
        private static readonly byte[] ShellLinkClsid =
        [
            0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
        ];

        private const uint HasLinkTargetIDList = 0x00000001;
        private const uint HasLinkInfo = 0x00000002;
        private const uint HasName = 0x00000004;
        private const uint HasRelativePath = 0x00000008;
        private const uint HasWorkingDir = 0x00000010;
        private const uint HasArguments = 0x00000020;
        private const uint HasIconLocation = 0x00000040;
        private const uint IsUnicode = 0x00000080;

        private const uint VolumeIDAndLocalBasePath = 0x00000001;
        private const uint CommonNetworkRelativeLinkAndPathSuffix = 0x00000002;

        private const uint EnvironmentVariableDataBlockSig = 0xA0000001;

        public static readonly FrozenSet<string> DangerousExtensions = new[]
        {
            ".exe", ".msi", ".msp", ".msu", ".mst", ".com", ".bat", ".cmd",
            ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".vb", ".js", ".jse",
            ".wsf", ".wsh", ".scr", ".pif", ".hta", ".cpl", ".reg", ".inf",
            ".jar", ".jnlp", ".sct", ".application", ".appref-ms", ".website",
            ".docm", ".xlsm", ".pptm", ".xlsb", ".xlam", ".dotm", ".potm",
            ".iso", ".img", ".vhd", ".vhdx", ".shs", ".shb", ".lnk", ".url"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static bool IsUncPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path.Trim().Trim('"');
            return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
                   trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDangerousExtension(string? pathOrExt)
        {
            if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
            string ext = pathOrExt.StartsWith('.') && pathOrExt.IndexOfAny(['\\', '/']) < 0
                ? pathOrExt
                : Path.GetExtension(pathOrExt);
            return !string.IsNullOrEmpty(ext) && DangerousExtensions.Contains(ext);
        }

        public static ShellLinkInfo ParseLnk(string lnkPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lnkPath) || IsUncPath(lnkPath) || !File.Exists(lnkPath))
                    return new ShellLinkInfo("", "", "", "");

                var info = new FileInfo(lnkPath);
                if (info.Length < ShellLinkHeaderSize || info.Length > 2 * 1024 * 1024)
                    return new ShellLinkInfo("", "", "", "");

                byte[] data = File.ReadAllBytes(lnkPath);
                return ParseLnkBytes(data, Path.GetDirectoryName(lnkPath));
            }
            catch
            {
                return new ShellLinkInfo("", "", "", "");
            }
        }

        internal static ShellLinkInfo ParseLnkBytes(byte[] data, string? lnkDirectory = null)
        {
            if (data.Length < ShellLinkHeaderSize)
                return new ShellLinkInfo("", "", "", "");

            uint headerSize = BitConverter.ToUInt32(data, 0);
            if (headerSize != ShellLinkHeaderSize)
                return new ShellLinkInfo("", "", "", "");

            for (int i = 0; i < 16; i++)
            {
                if (data[4 + i] != ShellLinkClsid[i])
                    return new ShellLinkInfo("", "", "", "");
            }

            uint flags = BitConverter.ToUInt32(data, 20);
            bool unicode = (flags & IsUnicode) != 0;
            int offset = (int)ShellLinkHeaderSize;

            if ((flags & HasLinkTargetIDList) != 0)
            {
                if (offset + 2 > data.Length) return new ShellLinkInfo("", "", "", "");
                ushort idListSize = BitConverter.ToUInt16(data, offset);
                offset += 2 + idListSize;
                if (offset > data.Length) return new ShellLinkInfo("", "", "", "");
            }

            string targetPath = "";
            if ((flags & HasLinkInfo) != 0)
            {
                if (offset + 4 > data.Length) return new ShellLinkInfo("", "", "", "");
                uint linkInfoSize = BitConverter.ToUInt32(data, offset);
                if (linkInfoSize >= 28 && offset + linkInfoSize <= data.Length)
                {
                    targetPath = ParseLinkInfo(data, offset, (int)linkInfoSize);
                }
                offset += (int)linkInfoSize;
                if (offset > data.Length) return new ShellLinkInfo("", "", "", "");
            }

            if ((flags & HasName) != 0)
                _ = ReadCountedString(data, ref offset, unicode);

            string relativePath = "";
            if ((flags & HasRelativePath) != 0)
                relativePath = ReadCountedString(data, ref offset, unicode);

            string workingDir = "";
            if ((flags & HasWorkingDir) != 0)
                workingDir = ReadCountedString(data, ref offset, unicode);

            string arguments = "";
            if ((flags & HasArguments) != 0)
                arguments = ReadCountedString(data, ref offset, unicode);

            string iconLocation = "";
            if ((flags & HasIconLocation) != 0)
                iconLocation = ReadCountedString(data, ref offset, unicode);

            string envTarget = ParseEnvTargetExtraData(data, offset);

            if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(envTarget))
            {
                targetPath = Environment.ExpandEnvironmentVariables(envTarget);
            }
            else if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(relativePath) && !string.IsNullOrWhiteSpace(lnkDirectory))
            {
                try { targetPath = Path.GetFullPath(Path.Combine(lnkDirectory, relativePath)); }
                catch { targetPath = relativePath; }
            }

            if (!string.IsNullOrWhiteSpace(iconLocation))
                iconLocation = Environment.ExpandEnvironmentVariables(iconLocation);
            if (!string.IsNullOrWhiteSpace(workingDir))
                workingDir = Environment.ExpandEnvironmentVariables(workingDir);

            return new ShellLinkInfo(targetPath, arguments, workingDir, iconLocation);
        }

        private static string ParseLinkInfo(byte[] data, int start, int size)
        {
            uint headerSize = BitConverter.ToUInt32(data, start + 4);
            uint infoFlags = BitConverter.ToUInt32(data, start + 8);
            uint localBasePathOffset = BitConverter.ToUInt32(data, start + 16);
            uint networkRelLinkOffset = BitConverter.ToUInt32(data, start + 20);
            uint commonPathSuffixOffset = BitConverter.ToUInt32(data, start + 24);

            bool hasUnicodeOffsets = headerSize >= 0x24 && size >= 36;
            uint localBasePathUnicodeOffset = hasUnicodeOffsets ? BitConverter.ToUInt32(data, start + 28) : 0;
            uint commonPathSuffixUnicodeOffset = hasUnicodeOffsets ? BitConverter.ToUInt32(data, start + 32) : 0;

            if ((infoFlags & VolumeIDAndLocalBasePath) != 0)
            {
                string basePath = localBasePathUnicodeOffset > 0 && localBasePathUnicodeOffset < (uint)size
                    ? ReadNullTerminatedUnicode(data, start + (int)localBasePathUnicodeOffset, start + size)
                    : (localBasePathOffset > 0 && localBasePathOffset < (uint)size
                        ? ReadNullTerminatedAnsi(data, start + (int)localBasePathOffset, start + size)
                        : "");

                string suffix = commonPathSuffixUnicodeOffset > 0 && commonPathSuffixUnicodeOffset < (uint)size
                    ? ReadNullTerminatedUnicode(data, start + (int)commonPathSuffixUnicodeOffset, start + size)
                    : (commonPathSuffixOffset > 0 && commonPathSuffixOffset < (uint)size
                        ? ReadNullTerminatedAnsi(data, start + (int)commonPathSuffixOffset, start + size)
                        : "");

                if (string.IsNullOrEmpty(suffix)) return basePath;
                if (string.IsNullOrEmpty(basePath)) return suffix;
                return basePath.EndsWith('\\') ? basePath + suffix : basePath + "\\" + suffix;
            }

            if ((infoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0 &&
                networkRelLinkOffset > 0 && networkRelLinkOffset + 20 <= (uint)size)
            {
                int netStart = start + (int)networkRelLinkOffset;
                uint netSize = BitConverter.ToUInt32(data, netStart);
                if (netSize >= 20 && netStart + netSize <= start + size)
                {
                    uint netNameOffset = BitConverter.ToUInt32(data, netStart + 8);
                    string netName = netNameOffset > 0 && netNameOffset < netSize
                        ? ReadNullTerminatedAnsi(data, netStart + (int)netNameOffset, netStart + (int)netSize)
                        : "";
                    string suffix = commonPathSuffixOffset > 0 && commonPathSuffixOffset < (uint)size
                        ? ReadNullTerminatedAnsi(data, start + (int)commonPathSuffixOffset, start + size)
                        : "";
                    if (!string.IsNullOrEmpty(netName))
                    {
                        return string.IsNullOrEmpty(suffix)
                            ? netName
                            : (netName.EndsWith('\\') ? netName + suffix : netName + "\\" + suffix);
                    }
                }
            }

            return "";
        }

        private static string ParseEnvTargetExtraData(byte[] data, int offset)
        {
            while (offset + 4 <= data.Length)
            {
                uint blockSize = BitConverter.ToUInt32(data, offset);
                if (blockSize < 4 || offset + blockSize > data.Length) break;

                if (blockSize >= 788 && BitConverter.ToUInt32(data, offset + 4) == EnvironmentVariableDataBlockSig)
                {
                    string unicode = ReadNullTerminatedUnicode(data, offset + 268, offset + 788);
                    if (!string.IsNullOrWhiteSpace(unicode)) return unicode;
                    string ansi = ReadNullTerminatedAnsi(data, offset + 8, offset + 268);
                    if (!string.IsNullOrWhiteSpace(ansi)) return ansi;
                }

                offset += (int)blockSize;
            }
            return "";
        }

        private static string ReadCountedString(byte[] data, ref int offset, bool unicode)
        {
            if (offset + 2 > data.Length) { offset = data.Length; return ""; }
            ushort charCount = BitConverter.ToUInt16(data, offset);
            offset += 2;
            int byteCount = unicode ? charCount * 2 : charCount;
            if (offset + byteCount > data.Length) { offset = data.Length; return ""; }
            string value = unicode
                ? Encoding.Unicode.GetString(data, offset, byteCount)
                : Encoding.Default.GetString(data, offset, byteCount);
            offset += byteCount;
            return value;
        }

        private static string ReadNullTerminatedAnsi(byte[] data, int start, int limit)
        {
            int end = start;
            while (end < limit && data[end] != 0) end++;
            return Encoding.Default.GetString(data, start, end - start);
        }

        private static string ReadNullTerminatedUnicode(byte[] data, int start, int limit)
        {
            int end = start;
            while (end + 1 < limit && !(data[end] == 0 && data[end + 1] == 0)) end += 2;
            return Encoding.Unicode.GetString(data, start, end - start);
        }

        public static InternetShortcutInfo ParseUrlFile(string urlFilePath)
        {
            string url = "";
            string iconFile = "";
            int iconIndex = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(urlFilePath) || IsUncPath(urlFilePath) || !File.Exists(urlFilePath))
                    return new InternetShortcutInfo("", "", 0);

                var info = new FileInfo(urlFilePath);
                if (info.Length > 256 * 1024)
                    return new InternetShortcutInfo("", "", 0);

                foreach (string rawLine in File.ReadLines(urlFilePath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        url = line[4..].Trim();
                    }
                    else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        iconFile = line[9..].Trim().Trim('"');
                    }
                    else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(line[10..].Trim(), out iconIndex);
                    }
                }
            }
            catch { }

            return new InternetShortcutInfo(url, iconFile, iconIndex);
        }
    }
}
