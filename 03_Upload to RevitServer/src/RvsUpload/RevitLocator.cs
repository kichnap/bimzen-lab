using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RvsUpload
{
    public static class RevitLocator
    {
        /// <summary>
        /// Ищет Revit.exe: сначала реестр, потом стандартный Program Files.
        /// </summary>
        public static string FindRevitExe(int version)
        {
            // 1. Реестр: HKLM\SOFTWARE\Autodesk\Revit\<год>\<продукт>\InstallationLocation
            var candidates = new[]
            {
                $@"SOFTWARE\Autodesk\Revit\{version}",
                $@"SOFTWARE\Autodesk\Revit\Autodesk Revit {version}",
            };

            foreach (var keyPath in candidates)
            {
                using (var key = RegistryKey
                           .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                           .OpenSubKey(keyPath))
                {
                    if (key == null) continue;

                    var direct = key.GetValue("InstallationLocation") as string;
                    var exe = TryExe(direct);
                    if (exe != null) return exe;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using (var subKey = key.OpenSubKey(sub))
                        {
                            exe = TryExe(subKey?.GetValue("InstallationLocation") as string);
                            if (exe != null) return exe;
                        }
                    }
                }
            }

            // 2. Стандартные пути
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         @"C:\Program Files",
                     }.Distinct())
            {
                var exe = TryExe(Path.Combine(root, "Autodesk", $"Revit {version}"));
                if (exe != null) return exe;
            }

            throw new FileNotFoundException(
                $"Не найден Revit {version}. Укажите путь явно через --revit-exe.");
        }

        private static string TryExe(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return null;
            var exe = Path.Combine(installDir, "Revit.exe");
            return File.Exists(exe) ? exe : null;
        }

        /// <summary>Все установленные версии Revit (по годам).</summary>
        public static IEnumerable<int> InstalledVersions()
        {
            for (int y = 2015; y <= DateTime.Now.Year + 2; y++)
            {
                string exe = null;
                try { exe = FindRevitExe(y); } catch { /* нет такой */ }
                if (exe != null) yield return y;
            }
        }
    }
}
