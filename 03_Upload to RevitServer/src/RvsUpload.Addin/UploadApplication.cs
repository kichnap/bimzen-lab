using System;
using System.Diagnostics;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace RvsUpload.Addin
{
    /// <summary>
    /// Точка входа аддина.
    ///
    /// Почему IExternalApplication, а не IExternalCommand через journal:
    /// journal-файла в схеме нет вовсе. Revit, запущенный с journal, грузит
    /// только подписанные «внутренние» аддины из своей папки установки, и наш
    /// в процесс не попадает (подробности — AddinContract).
    /// Поэтому Revit запускается как обычная сессия, сам поднимает внешние
    /// приложения, а задание приходит через переменную окружения.
    ///
    /// ВАЖНО: аддин ставится в общую папку %ProgramData%\...\Addins\&lt;год&gt; и
    /// загружается в КАЖДОМ запуске Revit, включая обычные пользовательские
    /// сессии. Без переменной окружения он обязан не делать ничего и молча
    /// уходить — никаких кнопок, диалогов и фоновой активности.
    /// </summary>
    public class UploadApplication : IExternalApplication
    {
        private string _batchFile;
        private UploadBatch _batch;
        private SimpleLog _log;
        private DialogSuppressor _suppressor;

        public Result OnStartup(UIControlledApplication application)
        {
            _batchFile = Environment.GetEnvironmentVariable(AddinContract.BatchEnvVar);

            // Обычная пользовательская сессия Revit — нас не звали.
            if (string.IsNullOrEmpty(_batchFile))
                return Result.Succeeded;

            try
            {
                _batch = BatchRunner.ReadBatch(_batchFile);
                _log = new SimpleLog(_batch.LogFile);

                // Гасим диалоги с самого старта: помешать может и то, что
                // всплывёт до открытия первой модели.
                _suppressor = new DialogSuppressor(application, _log);

                // Работу делаем не здесь, а по ApplicationInitialized: на момент
                // OnStartup Revit ещё не достроен, открывать документы рано.
                application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
            }
            catch (Exception ex)
            {
                BatchRunner.WriteFatal(_batchFile, ex);
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            if (_batch != null)
                application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;

            _suppressor?.Dispose();
            _log?.Dispose();
            return Result.Succeeded;
        }

        /// <summary>
        /// Вся работа идёт здесь, синхронно и в главном потоке: на момент
        /// OnStartup Revit ещё не достроен и открывать документы рано.
        /// Закончив, аддин закрывает Revit сам — см. CloseRevit.
        /// </summary>
        private void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs e)
        {
            var app = sender as Autodesk.Revit.ApplicationServices.Application;

            try
            {
                if (app == null)
                    throw new InvalidOperationException(
                        "ApplicationInitialized не передал Application — работать не с чем.");

                BatchRunner.Run(_batch, app, _log, _suppressor);
            }
            catch (Exception ex)
            {
                // Исключение отсюда всплыло бы модальным окном и повесило сессию
                // до таймаута. Записываем провал и всё равно закрываем Revit.
                _log?.Write("ФАТАЛЬНАЯ ОШИБКА: " + ex);
                BatchRunner.WriteFatal(_batchFile, ex);
            }
            finally
            {
                CloseRevit(app);
            }
        }

        /// <summary>
        /// Закрывает Revit. Раньше это делала команда ID_APP_EXIT из journal-файла,
        /// но journal пришлось убрать: с ним Revit не грузит наш аддин вовсе
        /// (AddinContract). Теперь сессию завершает тот, кто знает, что работа
        /// закончена, — сам аддин.
        ///
        /// Закрыть надо в любом случае, включая провал: иначе Revit останется
        /// висеть до таймаута CLI, занимая лицензию.
        /// </summary>
        private void CloseRevit(Autodesk.Revit.ApplicationServices.Application app)
        {
            try
            {
                var uiapp = new UIApplication(app);
                var exitId = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
                uiapp.PostCommand(exitId);
                _log?.Write("Запрошено закрытие Revit (ExitRevit).");
            }
            catch (Exception ex)
            {
                // Штатно закрыться не вышло. Документы уже закрыты, result.json
                // записан на диск — терять нечего, и висеть до таймаута хуже.
                _log?.Write("Штатное закрытие не удалось, завершаю процесс: " + BatchRunner.Flatten(ex));
                _log?.Dispose();
                _log = null;
                Process.GetCurrentProcess().Kill();
            }
        }
    }
}
