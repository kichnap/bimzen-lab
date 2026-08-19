namespace RvsUpload
{
    /// <summary>
    /// Файл настроек (`--config settings.json`). Редактируется страницей
    /// Settings.html, которая лежит рядом с утилитой.
    ///
    /// Приоритет: аргументы командной строки ПЕРЕКРЫВАЮТ файл. Так одна и та же
    /// настройка живёт в файле для пайплайна, но её можно разово переопределить
    /// из командной строки, не правя файл.
    ///
    /// Все поля — nullable намеренно. Иначе «false» и «0», проставленные
    /// сериализатором по умолчанию, были бы неотличимы от «в файле не задано»
    /// и молча перекрывали бы умолчания утилиты.
    ///
    /// Здесь только то, что осмысленно держать в настройках. Сами модели
    /// (`--source`, `--dest`) в файл не выносятся: они меняются от запуска
    /// к запуску, их место в списке `--list` или в аргументах.
    /// </summary>
    public class SettingsFile
    {
        /// <summary>Справочное поле: кто и когда сохранил файл. Утилитой не используется.</summary>
        public string Комментарий { get; set; }

        // --- Revit ---
        public int? RevitVersion { get; set; }
        public string RevitExe { get; set; }
        public string Language { get; set; }

        // --- Назначение ---
        public string DestFolder { get; set; }
        public string ListFile { get; set; }
        public string SourceFolder { get; set; }
        public bool? Recurse { get; set; }

        /// <summary>flat | disk | server — см. FolderStructure.</summary>
        public string Structure { get; set; }

        // --- Поведение заливки ---
        public bool? Overwrite { get; set; }
        public bool? CreateFolders { get; set; }
        public bool? IgnoreLocks { get; set; }
        public bool? Audit { get; set; }
        public int? MaximumBackups { get; set; }
        public bool? UnloadLinks { get; set; }
        public bool? Compact { get; set; }
        public bool? NoEnableWorksharing { get; set; }
        public bool? CloseWorksets { get; set; }

        /// <summary>upgrade | skip | abort</summary>
        public string OnUpgrade { get; set; }

        // --- Устойчивость ---
        public int? Retries { get; set; }
        public int? TimeoutMinutes { get; set; }
        public int? StartupTimeoutMinutes { get; set; }
        public int? IdleTimeoutMinutes { get; set; }

        // --- Диагностика ---
        public string LogFile { get; set; }
        public bool? KeepTemp { get; set; }
        public bool? SkipPreflight { get; set; }
    }
}
