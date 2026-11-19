using System;
using System.Collections.Frozen;

namespace DesktopFences.Core.Services
{
    public static class AutoOrganizeService
    {
        public sealed record CategoryDefinition(string Title, string DefaultExtensions, FrozenSet<string> Extensions);

        public static readonly CategoryDefinition[] FileTypeCategories =
        [
            new("Shortcuts & Apps", ".lnk, .url, .appref-ms, .exe, .msi",
                new[] { ".lnk", ".url", ".appref-ms", ".exe", ".msi" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            new("Documents", ".pdf, .doc, .docx, .xls, .xlsx, .ppt, .pptx, .txt, .rtf, .csv, .md, .odt, .ods, .odp, .epub",
                new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv", ".md", ".odt", ".ods", ".odp", ".epub" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            new("Images", ".jpg, .jpeg, .png, .gif, .bmp, .webp, .svg, .ico, .tiff, .tif, .heic, .psd, .ai, .raw",
                new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff", ".tif", ".heic", ".psd", ".ai", ".raw" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            new("Media", ".mp4, .mkv, .avi, .mov, .wmv, .webm, .flv, .m4v, .mp3, .wav, .flac, .ogg, .m4a, .aac, .wma",
                new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            new("Archives", ".zip, .rar, .7z, .tar, .gz, .bz2, .xz, .tgz, .iso, .cab",
                new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".iso", ".cab" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
            new("Code & Scripts", ".cs, .py, .js, .ts, .jsx, .tsx, .html, .htm, .css, .scss, .json, .xml, .yaml, .yml, .toml, .ini, .cfg, .sql, .ps1, .bat, .cmd, .sh, .cpp, .c, .h, .hpp, .java, .go, .rs, .php, .rb, .lua, .sln, .csproj",
                new[] { ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".cpp", ".c", ".h", ".hpp", ".java", ".go", ".rs", ".php", ".rb", ".lua", ".sln", ".csproj" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase))
        ];

        public static bool MatchesExtensionFilter(string fileExtension, string? filterExtensions)
        {
            if (string.IsNullOrWhiteSpace(fileExtension) || string.IsNullOrWhiteSpace(filterExtensions)) return false;
            string[] targets = filterExtensions.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            foreach (string target in targets)
            {
                string normalized = target.StartsWith('.') ? target : "." + target;
                if (string.Equals(fileExtension, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static (string CategoryTitle, string DefaultExtensions) ResolveCategoryForExtension(string fileExtension)
        {
            if (!string.IsNullOrWhiteSpace(fileExtension))
            {
                foreach (var category in FileTypeCategories)
                {
                    if (category.Extensions.Contains(fileExtension))
                    {
                        return (category.Title, category.DefaultExtensions);
                    }
                }
            }
            return ("Other Files", string.Empty);
        }

        public static bool IsDefaultFenceTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            return title.Equals("Fluid Fence", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("My First Fence", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("Main", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("New Tab", StringComparison.OrdinalIgnoreCase);
        }
    }
}
