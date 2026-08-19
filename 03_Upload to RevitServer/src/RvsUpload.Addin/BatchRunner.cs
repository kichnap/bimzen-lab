using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.ApplicationServices;

namespace RvsUpload.Addin
{
    /// <summary>
    /// Выполнение пакета заданий внутри сессии Revit.
    /// Вызывается из UploadApplication по событию ApplicationInitialized.
    /// </summary>
    internal static class BatchRunner
    {
        public static UploadBatch ReadBatch(string batchFile) => Json.ReadFile<UploadBatch>(batchFile);

        public static void Run(UploadBatch batch, Application app, SimpleLog log, DialogSuppressor dialogs = null)
        {
            log.Write($"Аддин стартовал. Сессия {batch.SessionId}. Заданий: {batch.Tasks.Count}.");
            log.Write($"Revit: {app.VersionNumber} build {app.VersionBuild}");

            var batchResult = new UploadBatchResult { SessionId = batch.SessionId };
            var uploader = new Uploader(app, log, dialogs);

            foreach (var task in batch.Tasks)
            {
                dialogs?.ResetPerTaskState();

                var sw = Stopwatch.StartNew();
                var r = new UploadResult
                {
                    SourceFile = task.SourceFile,
                    DestinationPath = task.DestinationPath
                };

                try
                {
                    uploader.Upload(task);
                    r.Success = true;
                    log.Write($"УСПЕХ: {task.SourceFile} -> {task.DestinationPath}");
                }
                catch (Exception ex)
                {
                    r.Success = false;
                    r.Error = Flatten(ex);
                    log.Write($"ОШИБКА: {task.SourceFile} -> {task.DestinationPath}: {r.Error}");
                }

                r.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                batchResult.Results.Add(r);

                // Пишем результат после каждой модели — если Revit упадёт
                // на пятой из десяти, CLI всё равно увидит первые четыре.
                WriteResult(batch.ResultFile, batchResult);
            }

            batchResult.FinishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            WriteResult(batch.ResultFile, batchResult);
            log.Write("Аддин завершил работу.");
        }

        /// <summary>
        /// Последний рубеж: упасть молча нельзя. Если batch прочитать удалось —
        /// пишем результат с ошибкой, чтобы CLI отличил «аддин отработал и не смог»
        /// от «аддин вообще не стартовал».
        /// </summary>
        public static void WriteFatal(string batchFile, Exception ex)
        {
            try
            {
                var batch = ReadBatch(batchFile);
                var result = new UploadBatchResult
                {
                    SessionId = batch.SessionId,
                    FinishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                };

                foreach (var t in batch.Tasks)
                {
                    result.Results.Add(new UploadResult
                    {
                        SourceFile = t.SourceFile,
                        DestinationPath = t.DestinationPath,
                        Success = false,
                        Error = "Фатальная ошибка аддина: " + Flatten(ex),
                    });
                }

                WriteResult(batch.ResultFile, result);
            }
            catch
            {
                // Даже это не вышло — CLI увидит отсутствие result.json
                // и сообщит об этом с путём к журналу Revit.
            }
        }

        private static void WriteResult(string path, UploadBatchResult result) => Json.WriteFile(path, result);

        internal static string Flatten(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
                parts.Add($"{e.GetType().Name}: {e.Message}");
            return string.Join(" <- ", parts);
        }
    }

    internal class SimpleLog : IDisposable
    {
        private readonly StreamWriter _w;

        public SimpleLog(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _w = new StreamWriter(path, append: true) { AutoFlush = true };
        }

        public void Write(string msg) => _w.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

        public void Dispose() => _w?.Dispose();
    }
}
