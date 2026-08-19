using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace RvsUpload.Addin
{
    /// <summary>
    /// Здесь происходит собственно заливка. Ключевые моменты:
    ///
    /// 1. Открываем исходник ЧЕРЕЗ Application.OpenDocumentFile (DB-уровень),
    ///    а не через UIApplication — документ не попадает в UI, поэтому можно
    ///    обработать несколько моделей в одной сессии Revit и закрывать их.
    ///
    /// 2. Если исходник workshared — открываем с Detach, иначе SaveAsCentral
    ///    упадёт: модель уже привязана к другой центральной.
    ///
    /// 3. Если исходник НЕ workshared — SaveAsCentral тоже невозможен,
    ///    поэтому включаем worksharing через doc.EnableWorksharing().
    ///
    /// 4. Путь назначения — ServerPath. RSN:// понимается
    ///    ModelPathUtils.ConvertUserVisiblePathToModelPath.
    /// </summary>
    internal class Uploader
    {
        private readonly Application _app;
        private readonly SimpleLog _log;
        private readonly DialogSuppressor _dialogs;
        private bool _upgradeExpected;

        public Uploader(Application app, SimpleLog log, DialogSuppressor dialogs = null)
        {
            _app = app;
            _log = log;
            _dialogs = dialogs;
        }

        public void Upload(UploadTask task)
        {
            if (!File.Exists(task.SourceFile))
                throw new FileNotFoundException("Исходный файл не найден", task.SourceFile);

            // --- Проверка версии до открытия ---
            var info = BasicFileInfo.Extract(task.SourceFile);
            _log.Write($"Исходник: формат={info.Format}, workshared={info.IsWorkshared}, " +
                       $"central={IsCentral(info)}, local={info.IsLocal}");

            // Модель младшей версии Revit открывает и обновляет сам — это дольше,
            // но не требует ручного шага, поэтому такие пропускаем. Непреодолимо
            // только обратное: формат более новой версии старая программа
            // прочитать не умеет.
            var modelVersion = ParseYear(info.Format);
            var revitVersion = ParseYear(_app.VersionNumber);

            if (modelVersion != 0 && revitVersion != 0)
            {
                if (modelVersion > revitVersion)
                    throw new InvalidOperationException(
                        $"Модель сохранена в Revit {modelVersion}, а заливка идёт через Revit " +
                        $"{revitVersion}. Более новую версию открыть невозможно — " +
                        $"запустите утилиту с --revit-version {modelVersion}.");

                _upgradeExpected = modelVersion < revitVersion;
                if (_upgradeExpected)
                    _log.Write($"Модель формата {modelVersion} будет обновлена до {revitVersion} " +
                               "при открытии — это займёт дополнительное время.");
            }

            var srcPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(task.SourceFile);

            var openOpts = new OpenOptions { Audit = task.Audit };

            if (info.IsWorkshared)
            {
                // Detach обязателен и для центральной, и для локальной копии:
                // без него модель остаётся привязанной к своей центральной, и
                // SaveAsCentral в другое место не пройдёт. Отдельный практический
                // случай — модель, скопированная из чужой рабочей папки: без
                // detach она вообще не откроется (Revit ищет свою центральную).
                openOpts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;

                var wsConfig = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);

                if (task.CloseUserWorksets)
                    CloseUserWorksets(srcPath, wsConfig);

                openOpts.SetOpenWorksetsConfiguration(wsConfig);
            }

            _log.Write($"Открываю {task.SourceFile} ...");

            Document doc;
            try
            {
                doc = _app.OpenDocumentFile(srcPath, openOpts);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(DescribeOpenFailure(ex, task), ex);
            }

            try
            {
                if (!doc.IsWorkshared)
                {
                    if (!task.EnableWorksharingIfNeeded)
                        throw new InvalidOperationException(
                            "Модель не workshared, а --no-enable-worksharing запрещает автоматическое включение. " +
                            "Центральную модель на Revit Server можно создать только из workshared-модели.");

                    _log.Write("Модель не workshared — включаю совместную работу.");
                    doc.EnableWorksharing(task.LevelsGridsWorksetName, task.DefaultWorksetName);
                }

                if (task.UnloadLinks)
                    UnloadRevitLinks(doc);

                var destPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(task.DestinationPath);

                var saveOpts = new SaveAsOptions
                {
                    OverwriteExistingFile = task.Overwrite,
                    Compact = task.Compact,
                };

                if (task.MaximumBackups > 0)
                    saveOpts.MaximumBackups = task.MaximumBackups;

                // ЭТО и есть заливка на Revit Server.
                var wsSaveOpts = new WorksharingSaveAsOptions
                {
                    SaveAsCentral = true,
                    // Детерминированное значение вместо AskUserToSpecify —
                    // иначе тот, кто потом откроет модель, получит диалог выбора.
                    OpenWorksetsDefault = SimpleWorksetConfiguration.AllWorksets,
                };
                saveOpts.SetWorksharingOptions(wsSaveOpts);

                _log.Write($"SaveAs (central) -> {task.DestinationPath} ...");
                doc.SaveAs(destPath, saveOpts);
                _log.Write("SaveAs завершён.");

                // Освобождаем все worksets, которые Revit взял в редактирование
                // при создании центральной модели. Иначе на сервере останется
                // "занято" за нашей учёткой.
                RelinquishAll(doc);
            }
            finally
            {
                try
                {
                    doc.Close(false);
                }
                catch (Exception ex)
                {
                    _log.Write("Предупреждение при закрытии документа: " + BatchRunner.Flatten(ex));
                }
            }
        }

        /// <summary>
        /// Закрывает пользовательские рабочие наборы.
        ///
        /// Смысл не в скорости самой по себе, а в том, чтобы НЕ ОТКРЫВАТЬ
        /// связанные модели, размещённые в этих наборах. На моделях без связей
        /// разницы во времени практически нет.
        ///
        /// Список берётся через WorksharingUtils.GetUserWorksetInfo — он читает
        /// заголовок файла и НЕ открывает документ, поэтому конфигурацию можно
        /// подготовить до OpenDocumentFile.
        ///
        /// Что именно закрывается: всё, что метод считает пользовательскими
        /// наборами. Сюда попадают и «Общие уровни и сетки» — вопреки
        /// распространённому мнению это обычный пользовательский набор, а не
        /// системный. Системные (виды, семейства, стандарты проекта) метод
        /// не возвращает, и они остаются открытыми.
        ///
        /// На содержимое результата это не влияет: закрытый набор остаётся
        /// в файле целиком. Проверено на живом Revit 2021 — та же модель с флагом
        /// и без отличается на 0,14 %.
        /// </summary>
        private void CloseUserWorksets(ModelPath srcPath, WorksetConfiguration config)
        {
            try
            {
                var userWorksets = WorksharingUtils.GetUserWorksetInfo(srcPath);
                if (userWorksets == null || userWorksets.Count == 0)
                {
                    _log.Write("Пользовательских рабочих наборов нет — закрывать нечего.");
                    return;
                }

                var ids = userWorksets.Select(w => w.Id).ToList();
                config.Close(ids);
                _log.Write($"Закрыто пользовательских рабочих наборов: {ids.Count} " +
                           $"({string.Join(", ", userWorksets.Take(5).Select(w => w.Name).ToArray())}" +
                           (userWorksets.Count > 5 ? ", ..." : "") + ")");
            }
            catch (Exception ex)
            {
                // Не критично: открываем со всеми наборами, просто медленнее.
                _log.Write("Не удалось закрыть рабочие наборы, открываю все: " + BatchRunner.Flatten(ex));
            }
        }

        private void RelinquishAll(Document doc)
        {
            try
            {
                var options = new RelinquishOptions(true)
                {
                    CheckedOutElements = true,
                    FamilyWorksets = true,
                    StandardWorksets = true,
                    UserWorksets = true,
                    ViewWorksets = true,
                };

                var twc = new TransactWithCentralOptions();
                WorksharingUtils.RelinquishOwnership(doc, options, twc);
                _log.Write("Права на worksets освобождены.");
            }
            catch (Exception ex)
            {
                _log.Write("Не удалось освободить worksets: " + BatchRunner.Flatten(ex));
            }
        }

        private void UnloadRevitLinks(Document doc)
        {
            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            foreach (var lt in linkTypes)
            {
                try
                {
                    lt.Unload(null);
                    _log.Write($"Связь выгружена: {lt.Name}");
                }
                catch (Exception ex)
                {
                    _log.Write($"Не удалось выгрузить связь {lt.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Внятная причина, почему не удалось открыть модель.
        ///
        /// Revit на любую внутреннюю беду отдаёт InternalException с текстом
        /// «A managed exception was thrown by Revit or by one of its external
        /// applications» — по нему причину не понять. Наблюдалось на живой
        /// модели: при обновлении с 2020 до 2021 Revit падал с нарушением
        /// доступа (0xC0000005 в собственном журнале) на модели, где до этого
        /// FileCheckDiagnostic удалил потерянные элементы.
        /// </summary>
        private string DescribeOpenFailure(Exception ex, UploadTask task)
        {
            // Человека спрашивали, останавливать ли операцию, — значит, он и
            // остановил. Диалог мы намеренно не перехватываем, см. DialogSuppressor.
            if (_dialogs != null && _dialogs.StopWasOfferedToUser)
                return "Открытие модели отменено пользователем (нажата «Отменить обновление»). " +
                       "Модель не залита.";

            var upgrade = "";
            if (_upgradeExpected)
            {
                // Совет должен быть честным: предлагать --audit, когда он уже
                // включён, — значит сбивать с толку. И сам по себе он не лечит:
                // на живой модели наблюдались падения Revit при обновлении
                // и с аудитом, и без него (примерно один раз из трёх).
                upgrade = task.Audit
                    ? " Модель открывалась с обновлением версии и уже с --audit. " +
                      "Обновление — самый хрупкий этап для повреждённой модели, " +
                      "и падения Revit здесь плавающие: повторный запуск часто проходит."
                    : " Модель открывалась с обновлением версии — самый хрупкий этап " +
                      "для повреждённой модели. Падения здесь плавающие, помогает " +
                      "повторный запуск; --audit вероятность не снижает.";
            }

            return "Revit не смог открыть модель: " + BatchRunner.Flatten(ex) +
                   "." + upgrade +
                   " Подробности только в собственном журнале Revit: " +
                   @"%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <год>\Journals — " +
                   "ищите captureTryCrash и FileCheckDiagnostic.";
        }

        /// <summary>
        /// Год из строки версии: '2021' -> 2021, '2021.1' -> 2021, мусор -> 0.
        /// Формат в BasicFileInfo и Application.VersionNumber записан по-разному
        /// в разных сборках, поэтому берём первые четыре цифры.
        /// </summary>
        private static int ParseYear(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;

            var match = System.Text.RegularExpressions.Regex.Match(version, @"\d{4}");
            return match.Success
                ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
        }

        /// <summary>BasicFileInfo.IsCentral отсутствует в старых версиях API.</summary>
        private static string IsCentral(BasicFileInfo info)
        {
            try
            {
                var prop = typeof(BasicFileInfo).GetProperty("IsCentral");
                return prop?.GetValue(info)?.ToString() ?? "n/a";
            }
            catch
            {
                return "n/a";
            }
        }
    }
}
