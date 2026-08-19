namespace RvsUpload
{
    /// <summary>
    /// Ограничения на длину путей.
    ///
    /// Лимиты сервера НЕ зашиты намертво: Revit Server сообщает их сам в ответе
    /// GET /serverProperties (поля MaximumFolderPathLength и MaximumModelNameLength),
    /// и в pre-flight используются именно значения с конкретного сервера.
    /// Константы ниже — только запасные значения на случай, если сервер их не
    /// вернул (проверено на живом сервере 2021: 98 и 40).
    ///
    /// Локальный лимит 224 сервер не знает — это ограничение Revit на стороне
    /// клиента, полученное из его собственного сообщения.
    ///
    /// Все проверки делаются до запуска Revit. Иначе отказ приходит после того,
    /// как сессия уже поднялась и модель открылась — десятки минут на большой
    /// модели плюс занятая всё это время лицензия.
    /// </summary>
    public static class PathLimits
    {
        /// <summary>Запасной максимум для пути ПАПКИ на сервере (без имени модели).</summary>
        public const int MaxServerFolderPath = 98;

        /// <summary>Запасной максимум для ИМЕНИ модели на сервере.</summary>
        public const int MaxServerModelName = 40;

        /// <summary>Максимум для локального пути к файлу модели.</summary>
        public const int MaxLocalPath = 224;

        /// <summary>
        /// Проверяет путь папки на сервере. На вход — путь относительно корня
        /// сервера, разделители любые. Возвращает текст ошибки или null.
        /// </summary>
        public static string ValidateServerFolder(string serverRelativeFolder, int limit = MaxServerFolderPath)
        {
            if (string.IsNullOrEmpty(serverRelativeFolder)) return null;
            if (limit <= 0) limit = MaxServerFolderPath;

            // Считаем по одному разделителю на уровень — так путь показывает
            // и сам Revit Server в своём сообщении.
            var display = serverRelativeFolder.Replace('|', '\\');
            if (display.Length <= limit) return null;

            return $"Путь папки на сервере — {display.Length} символов, а сервер " +
                   $"не принимает длиннее {limit} " +
                   "(\"A folder path on this server cannot exceed 98 characters\"). " +
                   $"Укоротите путь назначения: {display}";
        }

        /// <summary>
        /// Проверяет имя модели. Имя считается вместе с расширением .rvt — именно
        /// так модель называется на сервере.
        /// </summary>
        public static string ValidateServerModelName(string modelName, int limit = MaxServerModelName)
        {
            if (string.IsNullOrEmpty(modelName)) return null;
            if (limit <= 0) limit = MaxServerModelName;
            if (modelName.Length <= limit) return null;

            return $"Имя модели — {modelName.Length} символов, а сервер не принимает длиннее " +
                   $"{limit} (MaximumModelNameLength): {modelName}";
        }

        /// <summary>Проверяет локальный путь к исходной модели. Возвращает текст ошибки или null.</summary>
        public static string ValidateLocalPath(string localPath)
        {
            if (string.IsNullOrEmpty(localPath)) return null;
            if (localPath.Length <= MaxLocalPath) return null;

            return $"Локальный путь — {localPath.Length} символов, а Revit не открывает " +
                   $"длиннее {MaxLocalPath}. Перенесите модель ближе к корню диска: {localPath}";
        }
    }
}
