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
    /// Значения сервера при этом можно переопределить настройкой. Причина
    /// практическая: сам Revit сохраняет на тот же сервер модели с именами
    /// длиннее объявленного лимита, то есть объявленный лимит строже
    /// фактического. Пока это так, отбраковывать по нему такие модели —
    /// значит запрещать то, что на деле работает.
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

        /// <summary>Значение настройки «не задано»: брать лимит с сервера.</summary>
        public const int LimitNotSet = -1;

        /// <summary>Лимит, при котором проверка не выполняется вовсе.</summary>
        public const int LimitDisabled = 0;

        /// <summary>
        /// Какой лимит применять на самом деле.
        ///
        /// Порядок: настройка важнее сервера, сервер важнее запасного значения.
        /// Настройка, равная нулю, отключает проверку — это осознанный выбор
        /// человека, а не «сервер промолчал», поэтому ноль от «не задано»
        /// отличается и молча в запасное значение не превращается.
        /// </summary>
        public static int EffectiveLimit(int fromSettings, int fromServer, int fallback)
        {
            if (fromSettings >= 0) return fromSettings;
            if (fromServer > 0) return fromServer;
            return fallback;
        }

        /// <summary>
        /// Откуда взялся применённый лимит — для лога. Человек, увидевший отказ
        /// по длине, должен понимать, чьё это ограничение: сервера или настройки.
        /// </summary>
        public static string LimitSource(int fromSettings, int fromServer)
        {
            if (fromSettings >= 0) return "задано настройкой";
            if (fromServer > 0) return "сообщает сервер";
            return "запасное значение, сервер лимит не сообщил";
        }

        /// <summary>
        /// Проверяет путь папки на сервере. На вход — путь относительно корня
        /// сервера, разделители любые. Возвращает текст ошибки или null.
        /// Лимит 0 означает «не проверять».
        /// </summary>
        public static string ValidateServerFolder(string serverRelativeFolder,
                                                  int limit = MaxServerFolderPath,
                                                  string limitSource = null)
        {
            if (string.IsNullOrEmpty(serverRelativeFolder)) return null;
            if (limit <= 0) return null;

            // Считаем по одному разделителю на уровень — так путь показывает
            // и сам Revit Server в своём сообщении.
            var display = serverRelativeFolder.Replace('|', '\\');
            if (display.Length <= limit) return null;

            return $"Путь папки на сервере — {display.Length} символов, " +
                   $"а допустимо не длиннее {limit} ({limitSource ?? "сообщает сервер"}). " +
                   $"Укоротите путь назначения: {display}";
        }

        /// <summary>
        /// Проверяет имя модели. Имя считается вместе с расширением .rvt — именно
        /// так модель называется на сервере. Лимит 0 означает «не проверять».
        /// </summary>
        public static string ValidateServerModelName(string modelName,
                                                     int limit = MaxServerModelName,
                                                     string limitSource = null)
        {
            if (string.IsNullOrEmpty(modelName)) return null;
            if (limit <= 0) return null;
            if (modelName.Length <= limit) return null;

            return $"Имя модели — {modelName.Length} символов, " +
                   $"а допустимо не длиннее {limit} ({limitSource ?? "сообщает сервер"}): {modelName}";
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
