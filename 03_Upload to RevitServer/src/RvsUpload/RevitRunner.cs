using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RvsUpload
{
    /// <summary>Итог сессии Revit. TimedOut отделён от ExitCode намеренно — см. Run.</summary>
    public class RevitRunResult
    {
        public int ExitCode;
        public bool TimedOut;

        /// <summary>
        /// Работа была выполнена, но Revit пришлось закрыть принудительно:
        /// PostCommand(ExitRevit) не сработал. Не ошибка заливки, но повод
        /// сказать об этом в логе.
        /// </summary>
        public bool ForcedAfterFinish;

        /// <summary>
        /// Revit не дошёл до аддина: застрял на модальном окне до загрузки
        /// аддинов. Повторять такой запуск бессмысленно — причина никуда
        /// не денется сама.
        /// </summary>
        public bool StuckOnStartup;

        /// <summary>
        /// Аддин работал, но перестал подавать признаки жизни: завис на
        /// неперехваченном модальном окне. В отличие от застревания на старте
        /// повтор здесь осмыслен — причина может быть в конкретной модели.
        /// </summary>
        public bool StuckIdle;
    }

    public class RevitRunner
    {
        private readonly string _revitExe;
        private readonly Action<string> _log;

        public RevitRunner(string revitExe, Action<string> log)
        {
            _revitExe = revitExe;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Запускает Revit с journal-файлом и ждёт завершения.
        ///
        /// Факт таймаута возвращается ОТДЕЛЬНЫМ полем, а не кодом -1: Revit сам
        /// завершается с кодом -1 в обычной ситуации (проверено на 2021 — при
        /// остановке воспроизведения journal). Со старой схемой такой запуск
        /// диагностировался как «таймаут» и CLI отдавал код 4, хотя таймаута
        /// не было и близко: Revit отработал 1 минуту 17 секунд.
        /// </summary>
        public RevitRunResult Run(TimeSpan timeout, string language = null, string batchFile = null,
                                  string resultFile = null, string addinLogFile = null,
                                  TimeSpan? startupTimeout = null, TimeSpan? idleTimeout = null)
        {
            // Revit запускается БЕЗ journal-файла — как обычная сессия.
            // С journal-файлом он грузит только «внутренние» подписанные аддины
            // из своей папки установки, и наш аддин из %ProgramData% не попадает
            // в процесс вовсе (AddinContract, CODE_REVIEW.md B-21).
            // Закрывает Revit сам аддин, закончив работу.
            var args = "";
            if (!string.IsNullOrWhiteSpace(language))
                args = $"/language {language}";

            var psi = new ProcessStartInfo(_revitExe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_revitExe) ?? Environment.CurrentDirectory,
            };

            // Задание передаётся переменной окружения, а не через journal:
            // Jrn.Data без выполняющейся команды Revit просто пропускает
            // (CODE_REVIEW.md, B-19/B-20). Переменная видна только этому процессу
            // Revit — пользовательские сессии её не увидят и аддин в них
            // не сделает ничего.
            if (!string.IsNullOrEmpty(batchFile))
            {
                psi.EnvironmentVariables[AddinContract.BatchEnvVar] = batchFile;
                _log($"{AddinContract.BatchEnvVar}={batchFile}");
            }

            _log($"Запуск: {_revitExe} {args}");

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                    throw new InvalidOperationException("Не удалось запустить Revit.exe");

                var sw = Stopwatch.StartNew();
                DateTime? finishedAt = null;
                var addinSeen = addinLogFile == null;
                var startupLimit = startupTimeout ?? TimeSpan.FromMinutes(5);
                var idleLimit = idleTimeout ?? TimeSpan.FromMinutes(20);
                var lastWrite = DateTime.MinValue;
                var lastChange = DateTime.UtcNow;

                while (!proc.WaitForExit(2000))
                {
                    if (sw.Elapsed > timeout)
                    {
                        _log($"ТАЙМАУТ {timeout.TotalMinutes:F0} мин — принудительное завершение Revit (PID {proc.Id}).");
                        TryKill(proc);
                        return new RevitRunResult { ExitCode = -1, TimedOut = true };
                    }

                    // Аддин так и не начал писать лог — значит, Revit застрял
                    // ДО его загрузки. Перехватить это окно нечем: аддина в
                    // процессе ещё нет. Типичные причины — занятая лицензия,
                    // восстановление после падения, диалог доверия неподписанной
                    // сборке. Ждать общий таймаут (десятки минут) бессмысленно.
                    if (!addinSeen)
                    {
                        if (File.Exists(addinLogFile))
                        {
                            addinSeen = true;
                            _log($"Аддин загрузился и начал работу ({sw.Elapsed:mm\\:ss} от старта).");
                        }
                        else if (sw.Elapsed > startupLimit)
                        {
                            _log($"Revit не дошёл до аддина за {startupLimit.TotalMinutes:F0} мин. " +
                                 "Скорее всего, он ждёт ответа в модальном окне, которое некому нажать: " +
                                 "занята лицензия, предложено восстановление после падения или " +
                                 "не подтверждена загрузка неподписанной надстройки. " +
                                 $"Завершаю принудительно (PID {proc.Id}).");
                            TryKill(proc);
                            return new RevitRunResult { ExitCode = -1, TimedOut = true, StuckOnStartup = true };
                        }
                    }

                    // Аддин работает, но молчит слишком долго — значит, застрял.
                    // Обычно это модальное окно, которое DialogSuppressor не узнал
                    // и не смог погасить. Общий таймаут один на всю сессию, поэтому
                    // без этой проверки простой стоил бы десятков минут.
                    if (addinSeen && finishedAt == null && addinLogFile != null)
                    {
                        var write = SafeLastWrite(addinLogFile);
                        if (write > lastWrite)
                        {
                            lastWrite = write;
                            lastChange = DateTime.UtcNow;
                        }
                        else if (DateTime.UtcNow - lastChange > idleLimit)
                        {
                            var doing = LastActivity(addinLogFile);
                            _log($"Лог аддина молчит {idleLimit.TotalMinutes:F0} мин — сессия зависла. " +
                                 (doing != null ? $"Последнее, что она делала: {doing}. " : "") +
                                 "Скорее всего, всплыло модальное окно, которое не удалось погасить. " +
                                 $"Завершаю принудительно (PID {proc.Id}).");
                            TryKill(proc);
                            return new RevitRunResult { ExitCode = -1, TimedOut = true, StuckIdle = true };
                        }
                    }

                    // Аддин отчитался — работа сделана, Revit должен закрыться сам
                    // по PostCommand(ExitRevit). Но эта команда доходит не всегда:
                    // наблюдалось, что Revit остаётся на стартовом экране «Главная»
                    // после успешной заливки. Ждать в такой ситуации общий таймаут
                    // (десятки минут) бессмысленно — лицензия занята впустую.
                    if (finishedAt == null && IsBatchFinished(resultFile))
                    {
                        finishedAt = DateTime.UtcNow;
                        _log("Аддин записал итоговый результат, жду штатного закрытия Revit " +
                             $"({GraceAfterFinish.TotalSeconds:F0} с).");
                    }
                    else if (finishedAt != null && DateTime.UtcNow - finishedAt > GraceAfterFinish)
                    {
                        _log($"Revit не закрылся сам за {GraceAfterFinish.TotalSeconds:F0} с после " +
                             $"завершения работы — закрываю принудительно (PID {proc.Id}).");
                        TryKill(proc);
                        return new RevitRunResult { ExitCode = 0, TimedOut = false, ForcedAfterFinish = true };
                    }
                }

                _log($"Revit завершился с кодом {proc.ExitCode} за {sw.Elapsed:hh\\:mm\\:ss}.");
                return new RevitRunResult { ExitCode = proc.ExitCode, TimedOut = false };
            }
        }

        /// <summary>
        /// Сколько ждём штатного закрытия после того, как аддин отчитался.
        /// Revit после SaveAs ещё сбрасывает кеши и закрывает сессию сервера,
        /// поэтому мгновенного выхода не ждём.
        /// </summary>
        private static readonly TimeSpan GraceAfterFinish = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Пакет завершён, если аддин проставил в result.json время окончания.
        /// Промежуточные снимки (после каждой модели) его не содержат — по нему
        /// и отличаем «дописал всё» от «ещё работает».
        /// </summary>
        private static bool IsBatchFinished(string resultFile)
        {
            try
            {
                if (string.IsNullOrEmpty(resultFile) || !File.Exists(resultFile)) return false;

                var result = Json.ReadFile<UploadBatchResult>(resultFile);
                return !string.IsNullOrEmpty(result?.FinishedUtc);
            }
            catch
            {
                // Файл могли читать в момент записи — не беда, проверим на следующем круге.
                return false;
            }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Последняя содержательная строка лога аддина — чтобы в отчёте было
        /// видно, на какой модели и на каком шаге всё встало.
        /// </summary>
        private static string LastActivity(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                string last = null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (!string.IsNullOrWhiteSpace(line)) last = line.Trim();
                }

                return last;
            }
            catch
            {
                return null;
            }
        }

        private void TryKill(Process proc)
        {
            // Revit порождает RevitWorker.exe и прочих детей. Убивать надо дерево:
            // одиночный Kill оставляет их сиротами (наблюдалось на прогонах).
            // Kill(entireProcessTree) появился только в .NET 5, здесь net48,
            // поэтому пользуемся taskkill.
            try
            {
                var kill = Process.Start(new ProcessStartInfo("taskkill",
                    $"/PID {proc.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (kill != null)
                {
                    kill.StandardOutput.ReadToEnd();
                    kill.StandardError.ReadToEnd();
                    kill.WaitForExit(30_000);
                }
            }
            catch (Exception ex)
            {
                _log($"taskkill не отработал ({ex.Message}), завершаю процесс напрямую.");
            }

            // Страховка на случай, если taskkill недоступен или не справился.
            // Трогаем ТОЛЬКО свой процесс: пользовательские сессии Revit других
            // версий могут идти рядом с боевыми моделями и несохранённой работой.
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(30_000);
                }
            }
            catch (Exception ex)
            {
                _log($"Не удалось убить процесс: {ex.Message}");
            }

            Thread.Sleep(2000);
        }
    }
}
