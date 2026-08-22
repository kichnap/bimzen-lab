using System;
using System.Collections.Generic;
using System.Globalization;

namespace RvsUpload
{
    /// <summary>
    /// Что делать с моделями младших версий, которые Revit обновит при открытии.
    /// Обновление безопасно, но заметно дольше: на тестовой модели 2020 открытие
    /// заняло 61 секунду против 3–5 у моделей своей версии.
    /// </summary>
    public enum UpgradePolicy
    {
        /// <summary>Обновлять и заливать. Значение по умолчанию.</summary>
        Upgrade,

        /// <summary>Заливать только модели своей версии, остальные отбраковать.</summary>
        Skip,

        /// <summary>Если есть хоть одна такая модель — не заливать ничего.</summary>
        Abort,
    }

    public class Options
    {
        public string Source;
        public string Destination;
        public string DestFolder;

        /// <summary>
        /// Имя модели на сервере. По умолчанию берётся имя исходного файла как есть;
        /// этот параметр позволяет задать другое — например, срезать лишний суффикс
        /// или исправить некорректное имя, пришедшее от смежников.
        /// </summary>
        public string DestName;

        public string ListFile;

        /// <summary>
        /// Папка с моделями (--source-folder). Альтернатива списку в файле:
        /// список приходится поддерживать руками, а папка сама отражает то,
        /// что в ней лежит.
        /// </summary>
        public string SourceFolder;

        /// <summary>
        /// Заходить ли во вложенные папки при --source-folder.
        /// По умолчанию да: проекты хранят разделы по подпапкам, и плоский
        /// обход нашёл бы пустоту.
        /// </summary>
        public bool Recurse = true;

        /// <summary>Как раскладывать найденные модели по папкам сервера.</summary>
        public FolderStructure Structure = FolderStructure.Flat;

        /// <summary>Путь к файлу настроек (--config).</summary>
        public string ConfigFile;

        /// <summary>
        /// Ключи, заданные в командной строке явно. Нужен, чтобы файл настроек
        /// не перекрывал то, что человек написал руками: приоритет всегда
        /// у аргументов.
        /// </summary>
        public readonly HashSet<string> Specified = new HashSet<string>(StringComparer.Ordinal);

        public int RevitVersion;
        public string RevitExe;
        public string Language;

        public bool Overwrite;
        public bool CreateFolders;
        public bool IgnoreLocks;
        public bool Audit;
        public int MaximumBackups;
        public bool UnloadLinks;
        public bool Compact;
        public bool NoEnableWorksharing;
        public bool CloseWorksets;

        /// <summary>
        /// Что делать с моделями, которые придётся обновлять при открытии.
        /// По умолчанию — обновлять: Revit делает это штатно, просто дольше.
        /// </summary>
        public UpgradePolicy OnUpgrade = UpgradePolicy.Upgrade;

        /// <summary>
        /// Сколько раз перезапускать Revit с остатком незалитых моделей.
        /// Нужно против плавающих падений Revit: на повреждённой модели
        /// обновление версии роняло его примерно в одном запуске из трёх,
        /// причём повторный запуск той же модели проходил успешно.
        /// </summary>
        public int Retries;

        public int TimeoutMinutes = 60;

        /// <summary>
        /// Сколько ждать, пока Revit дойдёт до аддина. Если не дошёл — он застрял
        /// на модальном окне до загрузки аддинов: занятая лицензия, восстановление
        /// после падения, диалог доверия неподписанной сборке. Перехватить такое
        /// окно нечем — аддина в процессе ещё нет, поэтому единственная защита
        /// это срок ожидания.
        /// </summary>
        public int StartupTimeoutMinutes = 5;

        /// <summary>
        /// Сколько терпеть молчание лога аддина, пока он уже работает.
        /// Ловит зависание на середине пакета: неперехваченное модальное окно
        /// не даёт продолжить, а общий таймаут один на всю сессию и стоил бы
        /// десятков минут простоя.
        ///
        /// Порог должен быть ЗАВЕДОМО БОЛЬШЕ самого долгого нормального шага:
        /// во время SaveAs лог молчит, и на большой модели это законные минуты.
        /// Отсюда умолчание в 20 минут, а не в одну-две.
        /// </summary>
        public int IdleTimeoutMinutes = 20;
        /// <summary>
        /// Открывать модели с проверкой структуры (Audit) на повторных попытках.
        ///
        /// Повтор случается тогда, когда с моделью уже что-то не так, а Audit
        /// чинит мелкие повреждения — то самое, из-за чего Revit падает при
        /// открытии через раз. На первой попытке он не нужен: замедляет открытие
        /// каждой модели пакета ради тех единиц, что упадут.
        /// </summary>
        public bool AuditOnRetry = true;

        public string LogFile;
        public bool KeepTemp;
        public bool SkipPreflight;
        public bool DryRun;

        /// <summary>
        /// Свой предел длины имени модели вместо того, что сообщает сервер.
        ///
        /// Нужен потому, что объявленный сервером предел строже фактического:
        /// сам Revit сохраняет на тот же сервер модели с именами длиннее.
        /// Пока это так, отбраковка по объявленному пределу запрещает то,
        /// что на деле работает.
        ///
        /// -1 — не задано, брать с сервера. 0 — не проверять вовсе.
        /// </summary>
        public int MaxModelNameLength = PathLimits.LimitNotSet;

        /// <summary>
        /// Свой предел длины пути папки на сервере. Смысл тот же, что
        /// у MaxModelNameLength: -1 — с сервера, 0 — не проверять.
        /// </summary>
        public int MaxFolderPathLength = PathLimits.LimitNotSet;

        public static Options Parse(string[] args) => Parse(args, null);

        /// <summary>
        /// Разбор аргументов с наложением файла настроек.
        /// Аргументы командной строки ПЕРЕКРЫВАЮТ файл — см. SettingsFile.
        /// </summary>
        public static Options Parse(string[] args, SettingsFile config)
        {
            var o = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal)) o.Specified.Add(a);

                switch (a)
                {
                    case "--config": o.ConfigFile = Next(args, ref i, a); break;
                    case "--source": o.Source = Next(args, ref i, a); break;
                    case "--dest": o.Destination = Next(args, ref i, a); break;
                    case "--dest-folder": o.DestFolder = Next(args, ref i, a); break;
                    case "--dest-name": o.DestName = Next(args, ref i, a); break;
                    case "--list": o.ListFile = Next(args, ref i, a); break;
                    case "--source-folder": o.SourceFolder = Next(args, ref i, a); break;
                    case "--no-recurse": o.Recurse = false; break;
                    case "--structure":
                        o.Structure = ModelScanner.ParseStructure(Next(args, ref i, a));
                        break;

                    case "--revit-version":
                        o.RevitVersion = int.Parse(Next(args, ref i, a), CultureInfo.InvariantCulture);
                        break;
                    case "--revit-exe": o.RevitExe = Next(args, ref i, a); break;
                    case "--language": o.Language = Next(args, ref i, a); break;

                    case "--overwrite": o.Overwrite = true; break;
                    case "--create-folders": o.CreateFolders = true; break;
                    case "--ignore-locks": o.IgnoreLocks = true; break;
                    case "--audit": o.Audit = true; break;
                    case "--max-backups":
                        o.MaximumBackups = int.Parse(Next(args, ref i, a), CultureInfo.InvariantCulture);
                        break;
                    case "--unload-links": o.UnloadLinks = true; break;
                    case "--compact": o.Compact = true; break;
                    case "--no-enable-worksharing": o.NoEnableWorksharing = true; break;
                    case "--close-worksets": o.CloseWorksets = true; break;
                    case "--on-upgrade":
                        o.OnUpgrade = ParseUpgradePolicy(Next(args, ref i, a));
                        break;

                    case "--retries":
                        o.Retries = int.Parse(Next(args, ref i, a), CultureInfo.InvariantCulture);
                        break;

                    case "--startup-timeout":
                        o.StartupTimeoutMinutes = int.Parse(Next(args, ref i, a), CultureInfo.InvariantCulture);
                        break;

                    case "--idle-timeout":
                        o.IdleTimeoutMinutes = int.Parse(Next(args, ref i, a), CultureInfo.InvariantCulture);
                        break;

                    case "--timeout":
                        o.TimeoutMinutes = int.Parse(Next(args, ref i, a), CultureInfo.InvariantCulture);
                        break;
                    case "--log": o.LogFile = Next(args, ref i, a); break;
                    case "--keep-temp": o.KeepTemp = true; break;
                    case "--skip-preflight": o.SkipPreflight = true; break;
                    case "--no-audit-on-retry": o.AuditOnRetry = false; break;

                    case "--max-model-name":
                        o.MaxModelNameLength = ParseLimit(Next(args, ref i, a), a);
                        break;

                    case "--max-folder-path":
                        o.MaxFolderPathLength = ParseLimit(Next(args, ref i, a), a);
                        break;
                    case "--dry-run": o.DryRun = true; break;

                    default:
                        throw new ArgumentException($"Неизвестный параметр: {a}");
                }
            }

            if (config != null) ApplyConfig(o, config);

            Validate(o);
            return o;
        }

        /// <summary>
        /// Накладывает файл настроек на то, что не задано в командной строке.
        /// Значение null в файле означает «не задано» и умолчание не трогает.
        /// </summary>
        private static void ApplyConfig(Options o, SettingsFile c)
        {
            if (!o.Specified.Contains("--revit-version") && c.RevitVersion.HasValue)
                o.RevitVersion = c.RevitVersion.Value;
            if (!o.Specified.Contains("--revit-exe") && c.RevitExe != null)
                o.RevitExe = c.RevitExe;
            if (!o.Specified.Contains("--language") && c.Language != null)
                o.Language = c.Language;

            // Взаимоисключающие ключи: значение из файла молчит, если человек
            // задал в командной строке его альтернативу. Иначе получаем не то,
            // что просили, а отказ «--dest и --dest-folder взаимоисключающи»
            // на ровном месте.
            if (!o.Specified.Contains("--dest-folder") && !o.Specified.Contains("--dest")
                && c.DestFolder != null)
                o.DestFolder = c.DestFolder;

            // Источник моделей из файла настроек берём, только если ни один
            // источник не задан в командной строке: иначе файл добавил бы
            // второй источник к заданному руками, и запуск упал бы на проверке
            // взаимоисключающих ключей.
            var источникЗадан = o.Specified.Contains("--list")
                             || o.Specified.Contains("--source")
                             || o.Specified.Contains("--source-folder");

            if (!источникЗадан && c.ListFile != null)
                o.ListFile = c.ListFile;

            if (!источникЗадан && c.SourceFolder != null)
                o.SourceFolder = c.SourceFolder;

            if (!o.Specified.Contains("--no-recurse") && c.Recurse.HasValue)
                o.Recurse = c.Recurse.Value;

            if (!o.Specified.Contains("--structure") && !string.IsNullOrWhiteSpace(c.Structure))
                o.Structure = ModelScanner.ParseStructure(c.Structure);

            if (!o.Specified.Contains("--overwrite") && c.Overwrite.HasValue)
                o.Overwrite = c.Overwrite.Value;
            if (!o.Specified.Contains("--create-folders") && c.CreateFolders.HasValue)
                o.CreateFolders = c.CreateFolders.Value;
            if (!o.Specified.Contains("--ignore-locks") && c.IgnoreLocks.HasValue)
                o.IgnoreLocks = c.IgnoreLocks.Value;
            if (!o.Specified.Contains("--audit") && c.Audit.HasValue)
                o.Audit = c.Audit.Value;
            if (!o.Specified.Contains("--max-backups") && c.MaximumBackups.HasValue)
                o.MaximumBackups = c.MaximumBackups.Value;
            if (!o.Specified.Contains("--unload-links") && c.UnloadLinks.HasValue)
                o.UnloadLinks = c.UnloadLinks.Value;
            if (!o.Specified.Contains("--compact") && c.Compact.HasValue)
                o.Compact = c.Compact.Value;
            if (!o.Specified.Contains("--no-enable-worksharing") && c.NoEnableWorksharing.HasValue)
                o.NoEnableWorksharing = c.NoEnableWorksharing.Value;
            if (!o.Specified.Contains("--close-worksets") && c.CloseWorksets.HasValue)
                o.CloseWorksets = c.CloseWorksets.Value;

            if (!o.Specified.Contains("--on-upgrade") && !string.IsNullOrWhiteSpace(c.OnUpgrade))
                o.OnUpgrade = ParseUpgradePolicy(c.OnUpgrade);

            if (!o.Specified.Contains("--retries") && c.Retries.HasValue)
                o.Retries = c.Retries.Value;
            if (!o.Specified.Contains("--timeout") && c.TimeoutMinutes.HasValue)
                o.TimeoutMinutes = c.TimeoutMinutes.Value;
            if (!o.Specified.Contains("--startup-timeout") && c.StartupTimeoutMinutes.HasValue)
                o.StartupTimeoutMinutes = c.StartupTimeoutMinutes.Value;
            if (!o.Specified.Contains("--idle-timeout") && c.IdleTimeoutMinutes.HasValue)
                o.IdleTimeoutMinutes = c.IdleTimeoutMinutes.Value;

            if (!o.Specified.Contains("--log") && c.LogFile != null)
                o.LogFile = c.LogFile;
            if (!o.Specified.Contains("--keep-temp") && c.KeepTemp.HasValue)
                o.KeepTemp = c.KeepTemp.Value;
            if (!o.Specified.Contains("--skip-preflight") && c.SkipPreflight.HasValue)
                o.SkipPreflight = c.SkipPreflight.Value;

            if (!o.Specified.Contains("--no-audit-on-retry") && c.AuditOnRetry.HasValue)
                o.AuditOnRetry = c.AuditOnRetry.Value;

            if (!o.Specified.Contains("--max-model-name") && c.MaxModelNameLength.HasValue)
                o.MaxModelNameLength = c.MaxModelNameLength.Value;
            if (!o.Specified.Contains("--max-folder-path") && c.MaxFolderPathLength.HasValue)
                o.MaxFolderPathLength = c.MaxFolderPathLength.Value;
        }

        /// <summary>
        /// Разбирает значение предела длины. Отрицательное значение не молчим:
        /// «-1» в командной строке выглядит как «взять с сервера», но человек,
        /// написавший его руками, скорее ошибся, чем имел это в виду.
        /// </summary>
        private static int ParseLimit(string value, string key)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
                throw new ArgumentException($"{key}: ожидается число, получено '{value}'.");

            if (limit < 0)
                throw new ArgumentException(
                    $"{key}: предел не может быть отрицательным. " +
                    "0 отключает проверку, положительное число задаёт свой предел, " +
                    "а без этого ключа предел берётся с сервера.");

            return limit;
        }

        /// <summary>
        /// Достаёт путь к файлу настроек до полного разбора: конфиг нужен
        /// уже во время разбора, чтобы наложиться на незаданные ключи.
        /// </summary>
        public static string PeekConfigPath(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--config") return args[i + 1];

            return null;
        }

        private static void Validate(Options o)
        {
            var источники = new List<string>();
            if (o.Source != null) источники.Add("--source");
            if (o.ListFile != null) источники.Add("--list");
            if (o.SourceFolder != null) источники.Add("--source-folder");

            if (источники.Count == 0)
                throw new ArgumentException("Нужен --source, --list или --source-folder.");

            if (источники.Count > 1)
                throw new ArgumentException(
                    $"Взаимоисключающие источники моделей: {string.Join(", ", источники.ToArray())}. " +
                    "Оставьте один.");

            if (o.Structure != FolderStructure.Flat && o.SourceFolder == null)
                throw new ArgumentException(
                    "--structure имеет смысл только вместе с --source-folder: " +
                    "в списке и у одиночной модели назначение задаётся явно.");

            if (!o.Recurse && o.SourceFolder == null)
                throw new ArgumentException("--no-recurse имеет смысл только вместе с --source-folder.");

            if (o.SourceFolder != null && o.DestFolder == null)
                throw new ArgumentException(
                    o.Structure == FolderStructure.Server
                        ? "--source-folder со --structure server требует --dest-folder: " +
                          "с него начинается поиск моделей в дереве сервера."
                        : "--source-folder требует --dest-folder.");

            if (o.SourceFolder != null && o.DestName != null)
                throw new ArgumentException(
                    "--dest-name задаёт одно имя и с --source-folder несовместим: " +
                    "моделей в папке много.");

            if (o.RevitVersion == 0 && o.RevitExe == null)
                throw new ArgumentException("Укажите --revit-version (например 2024).");

            if (o.RevitVersion != 0 && (o.RevitVersion < 2012 || o.RevitVersion > 2100))
                throw new ArgumentException($"Некорректная версия Revit: {o.RevitVersion}");

            if (o.TimeoutMinutes < 1)
                throw new ArgumentException("--timeout должен быть >= 1.");

            if (o.StartupTimeoutMinutes < 1)
                throw new ArgumentException("--startup-timeout должен быть >= 1.");

            if (o.IdleTimeoutMinutes < 1)
                throw new ArgumentException("--idle-timeout должен быть >= 1.");

            if (o.Retries < 0)
                throw new ArgumentException("--retries не может быть отрицательным.");

            if (o.Retries > 10)
                throw new ArgumentException(
                    $"--retries {o.Retries} — это уже не повтор, а зацикливание. Максимум 10.");

            if (o.Destination != null && o.DestFolder != null)
                throw new ArgumentException("--dest и --dest-folder взаимоисключающи.");

            if (o.DestName != null)
            {
                if (o.Destination != null)
                    throw new ArgumentException(
                        "--dest-name и --dest взаимоисключающи: --dest уже содержит имя модели.");

                if (o.ListFile != null)
                    throw new ArgumentException(
                        "--dest-name задаёт одно имя и с --list несовместим. " +
                        "Указывайте имя во второй колонке списка: '<локальный.rvt>|Имя.rvt'.");

                var nameError = ValidateModelName(o.DestName);
                if (nameError != null) throw new ArgumentException(nameError);
            }
        }

        private static UpgradePolicy ParseUpgradePolicy(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "upgrade": return UpgradePolicy.Upgrade;
                case "skip": return UpgradePolicy.Skip;
                case "abort": return UpgradePolicy.Abort;
                default:
                    throw new ArgumentException(
                        $"Недопустимое значение --on-upgrade: '{value}'. " +
                        "Допустимо: upgrade, skip, abort.");
            }
        }

        /// <summary>
        /// Проверяет имя модели для сервера. Это ИМЯ, а не путь: разделители
        /// каталогов здесь означают опечатку, а молча их проглотить — значит
        /// положить модель не туда, куда рассчитывал человек.
        /// Возвращает текст ошибки или null.
        /// </summary>
        public static string ValidateModelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Имя модели не может быть пустым.";

            if (name.IndexOfAny(new[] { '/', '\\', '|' }) >= 0)
                return $"Имя модели не должно содержать разделителей пути: '{name}'. " +
                       "Папка задаётся через --dest-folder.";

            if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                return $"Имя модели должно оканчиваться на .rvt — получено '{name}'. " +
                       "Расширение не дописывается автоматически намеренно: " +
                       "имя модели на сервере должно быть ровно тем, что вы указали.";

            return null;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"У параметра {flag} отсутствует значение.");
            return args[++i];
        }
    }
}
