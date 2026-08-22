using System;
using System.Collections.Generic;
using System.Linq;

namespace RvsUpload
{
    /// <summary>
    /// Разбор причин отказа для итоговой сводки.
    ///
    /// Тексты ошибок содержат имена моделей и пути, поэтому сгруппировать их
    /// как есть нельзя: двадцать отказов по одной причине дадут двадцать разных
    /// строк. Здесь текст сводится к причине — к тому, что человек будет чинить.
    ///
    /// Часть текстов приходит от Revit через аддин и нашими руками не написана,
    /// поэтому разбор идёт по ключевым словам, а не по кодам. Неопознанное
    /// не выбрасывается и не подменяется: попадает в «Прочее» вместе с текстом,
    /// чтобы причина не потерялась.
    /// </summary>
    public static class FailureReasons
    {
        /// <summary>
        /// Пары «что искать в тексте ошибки» → «как назвать причину».
        /// Порядок важен: проверяется сверху вниз, первое совпадение выигрывает.
        /// </summary>
        private static readonly (string Образец, string Причина)[] Правила =
        {
            ("Имя модели —",                "Слишком длинное имя модели"),
            ("Путь папки на сервере —",     "Слишком длинный путь папки на сервере"),
            ("Локальный путь —",            "Слишком длинный путь к модели на диске"),
            ("Модель уже существует",       "Модель уже есть на сервере (нужен --overwrite)"),
            ("заблокирована",               "Модель заблокирована на сервере"),
            ("не существует на сервере",    "Нет папки назначения (нужен --create-folders)"),
            ("не дошёл до аддина",          "Revit не дошёл до надстройки"),
            ("не найден",                   "Файл модели не найден"),
            ("не является файлом Revit",    "Файл не является моделью Revit"),
            ("младшей версии",              "Модель младшей версии, обновление запрещено"),
            ("новее",                       "Модель новее, чем Revit на этой машине"),
            ("не workshared",               "Модель без совместной работы"),
            ("нашлась в нескольких",        "Модель нашлась на сервере в нескольких папках"),
            ("не найдена на сервере",       "Модели нет на сервере (раскладка server)"),
            ("Saving failed",               "Revit не смог сохранить модель на сервер"),
            ("не было обработано",          "Задание осталось без результата"),
        };

        /// <summary>Сводит текст ошибки к короткой причине.</summary>
        public static string Classify(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "Причина не указана";

            foreach (var правило in Правила)
                if (error.IndexOf(правило.Образец, StringComparison.OrdinalIgnoreCase) >= 0)
                    return правило.Причина;

            return "Прочее";
        }

        /// <summary>
        /// Группирует отказы по причинам. Группы идут от больших к меньшим:
        /// начинать разбор надо с того, что задело больше всего моделей.
        /// «Прочее» уходит в конец, даже если группа крупная: там причина
        /// у каждой модели своя и одним действием их не починить.
        /// </summary>
        public static List<FailureGroup> Group(IEnumerable<UploadResult> failures)
        {
            var группы = new Dictionary<string, FailureGroup>(StringComparer.Ordinal);

            foreach (var f in failures ?? Enumerable.Empty<UploadResult>())
            {
                var причина = Classify(f.Error);
                if (!группы.TryGetValue(причина, out var группа))
                {
                    группа = new FailureGroup { Reason = причина };
                    группы[причина] = группа;
                }
                группа.Failures.Add(f);
            }

            return группы.Values
                .OrderBy(g => g.Reason == "Прочее" ? 1 : 0)
                .ThenByDescending(g => g.Failures.Count)
                .ThenBy(g => g.Reason, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Отказы, у которых одна причина.</summary>
    public class FailureGroup
    {
        public string Reason { get; set; }
        public List<UploadResult> Failures { get; } = new List<UploadResult>();
    }
}
