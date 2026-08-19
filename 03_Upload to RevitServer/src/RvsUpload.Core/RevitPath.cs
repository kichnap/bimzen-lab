using System.Globalization;
using System.Text.RegularExpressions;

namespace RvsUpload
{
    /// <summary>
    /// Разбор путей установки Revit. Лежит в Core, а не в CLI, ради тестов:
    /// ошибка здесь не падает, а тихо уводит запросы Admin REST API на сервис
    /// другой версии (или на безгодовую ветку 2012) и даёт 404 вместо
    /// внятного сообщения.
    /// </summary>
    public static class RevitPath
    {
        // 'Revit 2025', 'Revit2025', 'Revit_2025' — все три написания встречаются
        // в путях установки и в задаваемых вручную --revit-exe.
        private static readonly Regex VersionRegex =
            new Regex(@"Revit[ _]?(20\d{2})", RegexOptions.IgnoreCase);

        /// <summary>
        /// '...\Autodesk\Revit 2025\Revit.exe' -> 2025. Возвращает 0, если год не читается.
        /// Если год встречается несколько раз, берётся последний: в пути вида
        /// 'D:\Revit 2021\backup\Revit 2025\Revit.exe' значение имеет ближайший к exe.
        /// </summary>
        public static int VersionFromExePath(string revitExe)
        {
            if (string.IsNullOrWhiteSpace(revitExe)) return 0;

            var matches = VersionRegex.Matches(revitExe);
            if (matches.Count == 0) return 0;

            var last = matches[matches.Count - 1];
            return int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
