using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RvsUpload
{
    /// <summary>Как раскладывать найденные модели по папкам сервера.</summary>
    public enum FolderStructure
    {
        /// <summary>Все модели в одну папку назначения. Значение по умолчанию.</summary>
        Flat,

        /// <summary>
        /// Повторить структуру папок с диска: путь модели относительно корня
        /// сканирования достраивается к папке назначения.
        /// </summary>
        Disk,

        /// <summary>
        /// Положить каждую модель туда, где она уже лежит на сервере: имя
        /// ищется в дереве сервера. Обратная дорога для моделей, скачанных
        /// с сервера, обработанных и заливаемых назад.
        /// </summary>
        Server,
    }

    /// <summary>
    /// Поиск моделей в папке на диске.
    ///
    /// Отдельный класс, а не пара строк в Program: отбор файлов здесь
    /// не сводится к маске <c>*.rvt</c>. Рядом с рабочей моделью Revit
    /// оставляет резервные копии и служебные папки, и залить их на сервер —
    /// значит засорить его мусором под видом моделей.
    /// </summary>
    public static class ModelScanner
    {
        /// <summary>
        /// Резервная копия Revit: <c>Модель.0001.rvt</c>, <c>Модель.0023.rvt</c>.
        /// Ровно четыре цифры между двумя точками — так их именует сам Revit.
        /// </summary>
        private static readonly Regex BackupFile =
            new Regex(@"\.\d{4}\.rvt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Служебные папки, которые Revit создаёт рядом с моделями.
        /// <c>&lt;Имя&gt;_backup</c> — резервные копии центральной модели,
        /// внутри лежат полноценные .rvt, и без этого правила они уехали бы
        /// на сервер наравне с рабочими.
        /// </summary>
        private static bool IsServiceFolder(string name)
            => name.EndsWith("_backup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Revit_temp", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".", StringComparison.Ordinal);

        public class FoundModel
        {
            /// <summary>Полный путь к файлу на диске.</summary>
            public string FullPath;

            /// <summary>
            /// Путь папки относительно корня сканирования, разделители — '/'.
            /// Пусто, если модель лежит в самом корне.
            /// </summary>
            public string RelativeFolder;

            public string FileName => Path.GetFileName(FullPath);
        }

        /// <summary>
        /// Причина, по которой файл или папка пропущены. Возвращается вместе
        /// с находками: молча пропущенная модель выглядит как потерянная,
        /// и разбираться с этим будут дольше, чем читать строку в логе.
        /// </summary>
        public class Skipped
        {
            public string Path;
            public string Reason;
        }

        public class ScanResult
        {
            public List<FoundModel> Models = new List<FoundModel>();
            public List<Skipped> SkippedItems = new List<Skipped>();
        }

        /// <param name="root">Папка, с которой начинаем.</param>
        /// <param name="recurse">Заходить ли во вложенные папки.</param>
        public static ScanResult Scan(string root, bool recurse)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("Не задана папка для поиска моделей.");

            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException($"Папка не найдена: {full}");

            var result = new ScanResult();
            Walk(full, full, recurse, result);

            // Порядок обхода файловой системы не гарантирован, а от порядка
            // зависит вид лога и отчёта. Сортируем, чтобы два одинаковых
            // запуска давали одинаковый результат построчно.
            result.Models = result.Models
                .OrderBy(m => m.RelativeFolder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return result;
        }

        private static void Walk(string dir, string root, bool recurse, ScanResult result)
        {
            foreach (var file in Directory.GetFiles(dir, "*.rvt"))
            {
                var name = Path.GetFileName(file);

                if (BackupFile.IsMatch(name))
                {
                    result.SkippedItems.Add(new Skipped
                    {
                        Path = file,
                        Reason = "резервная копия Revit"
                    });
                    continue;
                }

                result.Models.Add(new FoundModel
                {
                    FullPath = file,
                    RelativeFolder = RelativeFolderOf(dir, root)
                });
            }

            if (!recurse) return;

            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (IsServiceFolder(name))
                {
                    result.SkippedItems.Add(new Skipped
                    {
                        Path = sub,
                        Reason = "служебная папка Revit"
                    });
                    continue;
                }
                Walk(sub, root, recurse, result);
            }
        }

        /// <summary>
        /// Путь папки относительно корня, разделители — '/'. Именно '/',
        /// а не '\': дальше эта строка идёт в путь Revit Server, где
        /// разделитель другой.
        /// </summary>
        public static string RelativeFolderOf(string dir, string root)
        {
            var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(d, r, StringComparison.OrdinalIgnoreCase)) return "";

            if (!d.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Папка '{d}' не находится внутри '{r}'.");

            return d.Substring(r.Length + 1).Replace('\\', '/');
        }

        public static FolderStructure ParseStructure(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "flat": return FolderStructure.Flat;
                case "disk": return FolderStructure.Disk;
                case "server": return FolderStructure.Server;
                default:
                    throw new ArgumentException(
                        $"Недопустимое значение --structure: '{value}'. " +
                        "Допустимо: flat (всё в одну папку), disk (повторить папки с диска), " +
                        "server (положить туда, где модель уже лежит на сервере).");
            }
        }
    }
}
