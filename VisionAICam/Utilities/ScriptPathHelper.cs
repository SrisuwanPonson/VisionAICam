using System;
using System.IO;
using VisionAICam.Core;

namespace VisionAICam.Utilities
{
    internal static class ScriptPathHelper
    {
        // Resolve python.exe or python DLL using AppSettings.ScriptPath then fallback to app Script/NewEnv
        public static string ResolvePythonRuntimePath()
        {
            try
            {
                var settings = MasterController.Instance?.GetService<AppSettings>() ?? SettingsManager.Load();
                if (!string.IsNullOrWhiteSpace(settings?.ScriptPath))
                {
                    var root = settings.ScriptPath;
                    var candExe = Path.Combine(root, "NewEnv", "Python313", "python.exe");
                    var candDll = Path.Combine(root, "NewEnv", "Python313", "python313.dll");
                    if (File.Exists(candExe)) return candExe;
                    if (File.Exists(candDll)) return candDll;
                }
            }
            catch { /* swallow and fall through to fallback */ }

            var fallbackExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script", "NewEnv", "Python313", "python.exe");
            var fallbackDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script", "NewEnv", "Python313", "python313.dll");

            if (File.Exists(fallbackExe)) return fallbackExe;
            if (File.Exists(fallbackDll)) return fallbackDll;

            throw new FileNotFoundException("Python runtime not found. Expected under configured ScriptPath (AppSettings.ScriptPath) or application Script/NewEnv folder.");
        }
    }
}