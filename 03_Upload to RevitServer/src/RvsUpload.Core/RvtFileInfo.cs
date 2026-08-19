using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RvsUpload
{
    /// <summary>
    /// Чтение версии .rvt без Revit API.
    ///
    /// Зачем: авторитетную проверку версии делает аддин через BasicFileInfo.Extract,
    /// но он работает уже ВНУТРИ запущенного Revit. Отказ приходит примерно через
    /// минуту после старта, и всё это время занята лицензия. Формат модели при этом
    /// лежит в самом файле открытым текстом, поэтому несовпадение версии можно
    /// поймать в pre-flight за доли секунды.
    ///
    /// Метод намеренно best-effort: если формат прочитать не удалось, pre-flight
    /// молча пропускает задание дальше, и версию проверит аддин. То есть худший
    /// исход — вернуться к прежнему поведению, а не ложный отказ.
    ///
    /// Устройство: .rvt — это OLE compound file, внутри которого лежит поток
    /// BasicFileInfo с текстом в UTF-16LE. Ищем строку "Format: &lt;год&gt;" в обоих
    /// вариантах выравнивания, потому что начало потока не обязано попадать
    /// на чётный байт.
    /// </summary>
    public static class RvtFileInfo
    {
        /// <summary>Дальше этого объёма не читаем: BasicFileInfo лежит в начале файла.</summary>
        private const int MaxScanBytes = 64 * 1024 * 1024;

        private static readonly Regex FormatRegex =
            new Regex(@"Format:\s*(\d{4})", RegexOptions.CultureInvariant);

        /// <summary>
        /// Возвращает год формата модели ('2021') или null, если прочитать не вышло.
        /// </summary>
        public static string TryReadFormat(string rvtPath)
        {
            try
            {
                if (string.IsNullOrEmpty(rvtPath) || !File.Exists(rvtPath)) return null;
                return TryReadFormatFromBytes(ReadHead(rvtPath));
            }
            catch
            {
                // Best-effort: любая неудача означает «проверит аддин».
                return null;
            }
        }

        /// <summary>Разбор уже прочитанных байтов. Вынесен отдельно ради тестов.</summary>
        public static string TryReadFormatFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return null;

            // Начало потока не обязано быть на чётном байте, поэтому пробуем оба
            // выравнивания UTF-16LE.
            for (int offset = 0; offset <= 1; offset++)
            {
                var text = Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset);
                var match = FormatRegex.Match(text);
                if (match.Success) return match.Groups[1].Value;
            }

            return null;
        }

        private static byte[] ReadHead(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = (int)Math.Min(fs.Length, MaxScanBytes);
                var buffer = new byte[length];

                var read = 0;
                while (read < length)
                {
                    var chunk = fs.Read(buffer, read, length - read);
                    if (chunk <= 0) break;
                    read += chunk;
                }

                if (read == length) return buffer;

                var trimmed = new byte[read];
                Array.Copy(buffer, trimmed, read);
                return trimmed;
            }
        }

        /// <summary>
        /// Проверяет, может ли Revit заданной версии открыть эту модель.
        ///
        /// Модель СТАРШЕ или РАВНА версии Revit — можно: Revit открывает модели
        /// младших версий и обновляет их сам. Обновление занимает время, зато
        /// не требует ручного шага, поэтому такие модели пропускаем.
        ///
        /// Модель НОВЕЕ версии Revit — нельзя ни при каких условиях: формат
        /// более новой версии старая программа прочитать не умеет.
        ///
        /// Возвращает текст ошибки или null (в том числе когда формат не прочитан
        /// или версия не задана — тогда проверку делает аддин).
        /// </summary>
        public static string ValidateVersion(string rvtPath, int revitVersion)
        {
            if (revitVersion == 0) return null;

            var modelVersion = ParseVersion(TryReadFormat(rvtPath));
            if (modelVersion == 0) return null;

            if (modelVersion <= revitVersion) return null;

            return $"Модель сохранена в Revit {modelVersion}, а заливка идёт через Revit " +
                   $"{revitVersion}: {Path.GetFileName(rvtPath)}. Более новую версию открыть " +
                   $"невозможно — запустите с --revit-version {modelVersion}.";
        }

        /// <summary>
        /// Нужно ли обновлять модель при открытии (её формат старше версии Revit).
        /// Возвращает год формата или 0, если обновление не требуется либо
        /// формат прочитать не удалось.
        /// </summary>
        public static int UpgradeNeededFrom(string rvtPath, int revitVersion)
        {
            if (revitVersion == 0) return 0;

            var modelVersion = ParseVersion(TryReadFormat(rvtPath));
            return modelVersion != 0 && modelVersion < revitVersion ? modelVersion : 0;
        }

        private static int ParseVersion(string format)
            => int.TryParse(format, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
