using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace DesktopFences.Tests;

internal static class TestEnvironmentBootstrap
{
    private static string? _isolatedConfigDir;

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUIDFENCES_CONFIG_DIR")))
            return;

        _isolatedConfigDir = Path.Combine(
            Path.GetTempPath(),
            $"FluidFences_TestConfig_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_isolatedConfigDir);
        Environment.SetEnvironmentVariable("FLUIDFENCES_CONFIG_DIR", _isolatedConfigDir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (_isolatedConfigDir is null) return;
            try
            {
                if (Directory.Exists(_isolatedConfigDir))
                    Directory.Delete(_isolatedConfigDir, recursive: true);
            }
            catch { }
        };
    }
}
