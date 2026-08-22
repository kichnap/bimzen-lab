using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RvsUpload
{
    /// <summary>Что удалось узнать про лицензию до запуска Revit.</summary>
    public class LicenseProbeResult
    {
        /// <summary>Удалось ли вообще определить занятость.</summary>
        public bool Determined;

        /// <summary>Свободные лицензии есть (осмысленно только при Determined).</summary>
        public bool Available;

        public string Message;
    }

    /// <summary>
    /// Best-effort проверка лицензии Revit до запуска.
    ///
    /// Работает только при СЕТЕВОМ лицензировании: опрашивает менеджер лицензий
    /// через `lmutil lmstat -a -c <сервер>`. При single-user (Autodesk Identity)
    /// штатного способа узнать занятость нет, и метод честно сообщает, что
    /// определить не смог — молча делать вид, что всё в порядке, нельзя.
    ///
    /// Настоящая защита от «лицензия занята» — сторож старта в RevitRunner:
    /// он ловит ситуацию независимо от типа лицензии.
    /// </summary>
    public static class LicenseProbe
    {
        public static LicenseProbeResult Probe(int revitVersion, Action<string> log)
        {
            try
            {
                var server = FindLicenseServer(revitVersion);
                if (server == null)
                    return NotDetermined(
                        "Сетевого лицензирования не найдено (нет ADSKFLEX_LICENSE_FILE и LicPath.lic). " +
                        "Похоже на single-user — занятость лицензии заранее проверить нечем.");

                var lmutil = FindLmutil();
                if (lmutil == null)
                    return NotDetermined(
                        $"Сервер лицензий задан ({server}), но lmutil.exe не найден — опросить его нечем.");

                log($"Опрашиваю менеджер лицензий: {lmutil} lmstat -a -c {server}");
                var output = RunLmutil(lmutil, server);
                if (output == null)
                    return NotDetermined($"lmutil не ответил на опрос {server}.");

                var feature = LmStat.FindRevit(LmStat.Parse(output), revitVersion);
                if (feature == null)
                    return NotDetermined(
                        $"В ответе менеджера лицензий нет функции Revit {revitVersion} — " +
                        "возможно, лицензия другого типа.");

                var available = feature.Available > 0;
                return new LicenseProbeResult
                {
                    Determined = true,
                    Available = available,
                    Message = $"Лицензии Revit {revitVersion} ({feature.Feature}): " +
                              $"всего {feature.Total}, занято {feature.InUse}, свободно {feature.Available}.",
                };
            }
            catch (Exception ex)
            {
                // Проверка — вспомогательная. Её собственная поломка не повод
                // отменять заливку, но и молчать о ней нельзя.
                return NotDetermined("Проверка лицензии не отработала: " + ex.Message);
            }
        }

        private static LicenseProbeResult NotDetermined(string message)
            => new LicenseProbeResult { Determined = false, Message = message };

        /// <summary>
        /// Адрес менеджера лицензий: переменная окружения либо LicPath.lic
        /// рядом с Revit. Формат — 'порт@хост' или просто 'хост'.
        /// </summary>
        private static string FindLicenseServer(int revitVersion)
        {
            foreach (var name in new[] { "ADSKFLEX_LICENSE_FILE", "LM_LICENSE_FILE" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            var licPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Autodesk", $"Revit {revitVersion}", "LicPath.lic");

            if (!File.Exists(licPath)) return null;

            // SERVER <host> ANY <port>  /  USE_SERVER
            foreach (var line in File.ReadAllLines(licPath))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                    return parts.Length >= 4 ? parts[3] + "@" + parts[1] : parts[1];
            }

            return null;
        }

        private static string FindLmutil()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Autodesk", "Autodesk Network License Manager"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Autodesk", "Autodesk Network License Manager"),
            };

            return roots.Select(r => Path.Combine(r, "lmutil.exe")).FirstOrDefault(File.Exists);
        }

        private static string RunLmutil(string lmutil, string server)
        {
            var psi = new ProcessStartInfo(lmutil, $"lmstat -a -c \"{server}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var proc = Process.Start(psi))
            {
                if (proc == null) return null;

                // Читаем оба потока ДО WaitForExit: обратный порядок вешает
                // процесс на выводе больше буфера канала.
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(30_000))
                {
                    try { proc.Kill(); } catch { /* уже мёртв */ }
                    return null;
                }

                return stdout;
            }
        }
    }
}
