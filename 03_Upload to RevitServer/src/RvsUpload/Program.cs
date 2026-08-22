using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;

namespace RvsUpload
{
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitBadArgs = 1;
        private const int ExitNoRevit = 2;
        private const int ExitUploadFailed = 3;
        private const int ExitTimeout = 4;
        private const int ExitServerError = 5;

        private static StreamWriter _logWriter;

        private static int Main(string[] rawArgs)
        {
            try
            {
                return Run(rawArgs);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("ОШИБКА АРГУМЕНТОВ: " + ex.Message);
                return ExitBadArgs;
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine("ОШИБКА: " + ex.Message);
                return ExitNoRevit;
            }
            catch (RevitServerException ex)
            {
                Console.Error.WriteLine("ОШИБКА REVIT SERVER: " + ex.Message);
                return ExitServerError;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("НЕОЖИДАННАЯ ОШИБКА: " + ex);
                return ExitUploadFailed;
            }
            finally
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
        }

        private static int Run(string[] rawArgs)
        {
            if (rawArgs.Length == 0 || rawArgs.Contains("--help") || rawArgs.Contains("-h"))
            {
                PrintHelp();
                return rawArgs.Length == 0 ? ExitBadArgs : ExitOk;
            }

            // Файл настроек читаем до полного разбора: он должен наложиться
            // на ключи, не заданные в командной строке.
            SettingsFile config = null;
            var configPath = Options.PeekConfigPath(rawArgs);
            if (configPath != null)
            {
                if (!File.Exists(configPath))
                    throw new ArgumentException($"Файл настроек не найден: {configPath}");

                config = Json.ReadFile<SettingsFile>(configPath);
            }

            var opt = Options.Parse(rawArgs, config);

            if (opt.LogFile != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opt.LogFile)) ?? ".");
                _logWriter = new StreamWriter(opt.LogFile, append: true) { AutoFlush = true };
            }

            var sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var workDir = Path.Combine(Path.GetTempPath(), "RvsUpload", sessionId);
            Directory.CreateDirectory(workDir);

            Log($"=== RvsUpload session {sessionId} ===");
            if (configPath != null)
                Log($"Настройки из файла: {Path.GetFullPath(configPath)} " +
                    "(аргументы командной строки его перекрывают).");

            // --- 1. Собираем список заданий ---
            var tasks = BuildTasks(opt);
            if (tasks.Count == 0)
                throw new ArgumentException("Не задано ни одной модели для загрузки.");

            Log($"Заданий: {tasks.Count}");

            // --- 2. Pre-flight через Admin REST API ---
            //     Revit при SaveAs не создаёт отсутствующие папки и выдаёт
            //     невнятную ошибку — поэтому проверяем и создаём заранее.
            // Отбраковка идёт ПОЗАДАЧНО: непригодная модель не должна утаскивать
            // за собой весь пакет. Каждая такая модель попадает в итоговый отчёт
            // как проваленная, остальные заливаются штатно.
            // Политику по обновлению решаем ДО проверок: от неё зависит,
            // отбраковывать ли модели младших версий.
            var upgradePolicy = DecideUpgradePolicy(tasks, opt);
            if (upgradePolicy == UpgradePolicy.Abort)
            {
                Log("Есть модели, требующие обновления, а политика --on-upgrade abort " +
                    "это запрещает — ничего не залито.");
                return ExitBadArgs;
            }

            var rejected = new List<UploadResult>();
            var accepted = ValidateTasks(tasks, opt, rejected, upgradePolicy);

            if (accepted.Count == 0)
            {
                Log("Ни одного пригодного задания — Revit не запускается.");
                ReportSummary(new List<UploadResult>(), rejected);
                return ExitUploadFailed;
            }

            if (rejected.Count > 0)
                Log($"Отбраковано заданий: {rejected.Count}. Продолжаю с оставшимися {accepted.Count}.");

            // --- 3. Проверочный прогон ---
            if (opt.DryRun)
            {
                var dryBatch = WriteBatch(workDir, "attempt1", sessionId, accepted);
                Log($"Рабочая папка: {workDir}");
                Log("DRY RUN — Revit не запускается. Batch-файл: " + dryBatch.BatchFile);

                // Отбраковку нельзя терять и в dry-run: проверочный прогон
                // пайплайна обязан отличать «всё пригодно» от «часть моделей
                // залить нельзя».
                if (rejected.Count > 0)
                {
                    Log($"Итого при проверке: пригодно {accepted.Count}, отбраковано {rejected.Count}.");
                    ReportFailureGroups(rejected);
                    return ExitUploadFailed;
                }

                return ExitOk;
            }

            Log($"Рабочая папка: {workDir}");

            // --- 4. Запуски Revit с повторами ---
            //
            // Повтор нужен не «на всякий случай»: на повреждённой модели Revit
            // падал при обновлении версии примерно в одном запуске из трёх,
            // причём тот же файл в следующем запуске проходил успешно
            // (CODE_REVIEW.md, B-24). Без повторов остаток пакета терялся.
            var revitExe = opt.RevitExe ?? RevitLocator.FindRevitExe(opt.RevitVersion);

            // Проверка лицензии до запуска. Работает только при сетевом
            // лицензировании; при single-user честно скажет, что не смогла.
            var license = LicenseProbe.Probe(
                opt.RevitVersion != 0 ? opt.RevitVersion : RevitPath.VersionFromExePath(revitExe), Log);

            Log("Лицензия: " + license.Message);
            if (license.Determined && !license.Available)
            {
                Log("Свободных лицензий Revit нет — запускать бессмысленно: " +
                    "Revit покажет модальное окно и будет ждать до таймаута.");
                return ExitNoRevit;
            }

            var runner = new RevitRunner(revitExe, Log);

            var succeeded = new List<UploadResult>();
            var pending = accepted;
            RevitRunResult run;
            var attempt = 0;

            while (true)
            {
                attempt++;

                var b = WriteBatch(workDir, "attempt" + attempt, sessionId, pending);
                Log($"Лог аддина: {b.Batch.LogFile}");
                if (attempt > 1)
                    Log($"=== Повтор {attempt - 1} из {opt.Retries}: осталось моделей {pending.Count} ===");

                run = runner.Run(TimeSpan.FromMinutes(opt.TimeoutMinutes), opt.Language,
                                 b.BatchFile, b.Batch.ResultFile, b.Batch.LogFile,
                                 TimeSpan.FromMinutes(opt.StartupTimeoutMinutes),
                                 TimeSpan.FromMinutes(opt.IdleTimeoutMinutes));

                if (run.ForcedAfterFinish)
                    Log("ВНИМАНИЕ: заливка отработала штатно, но Revit не закрылся сам и был " +
                        "завершён принудительно. На результат это не влияет.");

                var attemptResults = ReadAttemptResults(b.Batch, run, opt);

                // Успехи забираем насовсем, неудачи — кандидаты на повтор.
                var stillFailing = new List<UploadTask>();
                foreach (var t in pending)
                {
                    var r = FindResult(attemptResults, t);
                    if (r != null && r.Success) succeeded.Add(r);
                    else stillFailing.Add(t);
                }

                if (stillFailing.Count == 0) break;

                // Застревание на старте повтором не лечится: занятая лицензия
                // или неподтверждённая надстройка сами собой не рассосутся,
                // а каждый круг — это ещё один таймаут впустую.
                if (run.StuckOnStartup)
                {
                    Log("Revit не дошёл до аддина — повторы отменены, причина не в модели.");
                    foreach (var t in stillFailing)
                    {
                        rejected.Add(FindResult(attemptResults, t) ?? new UploadResult
                        {
                            SourceFile = t.SourceFile,
                            DestinationPath = t.DestinationPath,
                            Success = false,
                            Error = "Revit не дошёл до аддина (модальное окно до загрузки надстроек: " +
                                    "лицензия, восстановление после падения, диалог доверия).",
                        });
                    }
                    break;
                }

                if (attempt > opt.Retries)
                {
                    // Повторы исчерпаны — фиксируем последние причины отказа.
                    foreach (var t in stillFailing)
                    {
                        rejected.Add(FindResult(attemptResults, t) ?? new UploadResult
                        {
                            SourceFile = t.SourceFile,
                            DestinationPath = t.DestinationPath,
                            Success = false,
                            Error = "Задание не было обработано и не получило результата.",
                        });
                    }
                    break;
                }

                if (run.StuckIdle)
                    Log("Сессия была завершена по простою — повтор осмыслен: " +
                        "зависание могло быть связано с конкретной моделью.");

                Log($"Не удалось моделей: {stillFailing.Count}. Осталось повторов: {opt.Retries - attempt + 1}.");
                pending = stillFailing;
            }

            // --- 5. Итог ---
            foreach (var r in succeeded)
                Log($"OK   {r.SourceFile} -> {r.DestinationPath} ({r.ElapsedSeconds:F1} c)");

            var final = new UploadBatchResult
            {
                SessionId = sessionId,
                FinishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            final.Results.AddRange(succeeded);
            final.Results.AddRange(rejected);

            var finalResultFile = Path.Combine(workDir, "result.json");
            Json.WriteFile(finalResultFile, final);
            Log($"Итоговый результат: {finalResultFile}");

            var failed = rejected.Count;
            if (attempt > 1)
                Log($"Запусков Revit: {attempt} (повторов: {attempt - 1}).");

            ReportSummary(succeeded, rejected);

            if (!opt.KeepTemp && failed == 0)
                TryDelete(workDir);

            if (run.TimedOut) return ExitTimeout;
            return failed == 0 ? ExitOk : ExitUploadFailed;
        }

        /// <summary>
        /// Проверяет задания и делит их на пригодные и отбракованные.
        ///
        /// Отбраковка позадачная, а не «падаем всем запуском»: в пакете из двадцати
        /// моделей одна кривая не должна лишать заливки остальные девятнадцать.
        /// Каждая отбракованная попадает в отчёт и в result.json как проваленная,
        /// и итоговый код возврата это учитывает.
        ///
        /// Глобальными остаются только беды, которые делают бессмысленным весь
        /// запуск: сервер недоступен или не удалось определить версию для REST API.
        /// </summary>

        private class BatchFiles
        {
            public UploadBatch Batch;
            public string BatchFile;
        }

        /// <summary>
        /// Каждая попытка получает свою подпапку: свой batch.json, свой addin.log
        /// и свой result.json. Иначе повтор затирал бы улики предыдущего запуска,
        /// а именно они и нужны для разбора падения.
        /// </summary>
        private static BatchFiles WriteBatch(string workDir, string name, string sessionId,
                                             List<UploadTask> tasks)
        {
            var dir = Path.Combine(workDir, name);
            Directory.CreateDirectory(dir);

            var batch = new UploadBatch
            {
                SessionId = sessionId,
                LogFile = Path.Combine(dir, "addin.log"),
                ResultFile = Path.Combine(dir, "result.json"),
                Tasks = tasks,
            };

            var file = Path.Combine(dir, "batch.json");
            Json.WriteFile(file, batch);
            return new BatchFiles { Batch = batch, BatchFile = file };
        }

        private static UploadResult FindResult(List<UploadResult> results, UploadTask task)
            => results.FirstOrDefault(
                x => string.Equals(x.SourceFile, task.SourceFile, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(x.DestinationPath, task.DestinationPath, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Результаты одной попытки. Отсутствие файла — это не «ничего не вышло»,
        /// а отдельная беда: аддин не отработал вовсе, и о ней надо сказать прямо.
        /// </summary>
        private static List<UploadResult> ReadAttemptResults(UploadBatch batch, RevitRunResult run, Options opt)
        {
            if (File.Exists(batch.ResultFile))
                return Json.ReadFile<UploadBatchResult>(batch.ResultFile).Results;

            Log($"Аддин не записал файл результата (Revit вернул {run.ExitCode}). Возможные причины: " +
                $"аддин не установлен в %ProgramData%\\Autodesk\\Revit\\Addins\\{opt.RevitVersion}, " +
                "GUID в .addin не совпадает с AddinContract.AddInId, аддин не подтверждён " +
                "в диалоге безопасности Revit (загрузка неподписанной сборки), " +
                "или Revit упал до выполнения. " +
                "Смотрите журнал Revit: %LOCALAPPDATA%\\Autodesk\\Revit\\Autodesk Revit " +
                $"{opt.RevitVersion}\\Journals");

            return new List<UploadResult>();
        }

        /// <summary>
        /// Определяет, что делать с моделями младших версий.
        ///
        /// Вопросов не задаёт НИКОГДА: утилита должна одинаково отрабатывать
        /// и из рук, и из Планировщика заданий, где отвечать некому. Поведение
        /// задаётся только ключом --on-upgrade, по умолчанию — обновлять.
        /// </summary>
        private static UpgradePolicy DecideUpgradePolicy(List<UploadTask> tasks, Options opt)
        {
            var targetVersion = opt.RevitVersion != 0
                ? opt.RevitVersion
                : RevitPath.VersionFromExePath(opt.RevitExe);

            var needUpgrade = tasks
                .Select(t => new { Task = t, From = RvtFileInfo.UpgradeNeededFrom(t.SourceFile, targetVersion) })
                .Where(x => x.From != 0)
                .ToList();

            if (needUpgrade.Count == 0) return opt.OnUpgrade;

            Log($"Моделей, требующих обновления: {needUpgrade.Count} (целевая версия {targetVersion}).");
            foreach (var x in needUpgrade)
                Log($"  {Path.GetFileName(x.Task.SourceFile)}: {x.From} -> {targetVersion}");

            Log($"Политика --on-upgrade: {opt.OnUpgrade.ToString().ToLowerInvariant()}.");
            return opt.OnUpgrade;
        }

        private static List<UploadTask> ValidateTasks(List<UploadTask> tasks, Options opt,
                                                      List<UploadResult> rejected,
                                                      UpgradePolicy upgradePolicy)
        {
            var accepted = new List<UploadTask>();

            var clients = new Dictionary<string, RevitServerRestClient>(StringComparer.OrdinalIgnoreCase);
            var limits = new Dictionary<string, ServerProperties>(StringComparer.OrdinalIgnoreCase);
            var restVersion = opt.SkipPreflight ? 0 : ResolveRestVersion(opt);

            foreach (var t in tasks)
            {
                var error = ValidateTask(t, opt, restVersion, clients, limits, upgradePolicy);
                if (error == null)
                {
                    accepted.Add(t);
                    continue;
                }

                Log($"ОТКАЗ {t.SourceFile} -> {t.DestinationPath}: {error}");
                rejected.Add(new UploadResult
                {
                    SourceFile = t.SourceFile,
                    DestinationPath = t.DestinationPath,
                    Success = false,
                    Error = error,
                });
            }

            return accepted;
        }

        /// <summary>Проверка одного задания. Возвращает причину отказа или null.</summary>
        private static string ValidateTask(UploadTask t, Options opt, int restVersion,
                                           Dictionary<string, RevitServerRestClient> clients,
                                           Dictionary<string, ServerProperties> limits,
                                           UpgradePolicy upgradePolicy)
        {
            if (!File.Exists(t.SourceFile))
                return $"Исходный файл не найден: {t.SourceFile}";

            var localError = PathLimits.ValidateLocalPath(t.SourceFile);
            if (localError != null) return localError;

            // Версию проверяем всегда, даже при --skip-preflight: сети это
            // не требует, а цена ошибки — впустую занятая лицензия Revit.
            var targetVersion = opt.RevitVersion != 0
                ? opt.RevitVersion
                : RevitPath.VersionFromExePath(opt.RevitExe);

            var versionError = RvtFileInfo.ValidateVersion(t.SourceFile, targetVersion);
            if (versionError != null) return versionError;

            var upgradeFrom = RvtFileInfo.UpgradeNeededFrom(t.SourceFile, targetVersion);
            if (upgradeFrom != 0)
            {
                if (upgradePolicy == UpgradePolicy.Skip)
                    return $"Модель формата {upgradeFrom} требует обновления до {targetVersion}, " +
                           "а обновление отключено (--on-upgrade skip). Модель не залита.";

                Log($"ВНИМАНИЕ: {Path.GetFileName(t.SourceFile)} формата {upgradeFrom} будет " +
                    $"обновлена до {targetVersion} при открытии — это займёт дополнительное время.");

                // Аудит включается только тем моделям, которым обновление
                // действительно предстоит: он дорогой по времени.
                //
                // ВАЖНО, что он НЕ является лекарством от падений Revit при
                // обновлении. Замеры на повреждённой модели: без аудита 3 успеха
                // из 4, с аудитом 1 из 2 — падения плавающие и от него не зависят.
                // Аудит здесь ради своей прямой задачи (починить мелкие
                // повреждения структуры), а не ради стабильности апгрейда.
                if (!t.Audit)
                {
                    t.Audit = true;
                    Log("  Для неё включён --audit автоматически (проверка структуры " +
                        "при открытии). От плавающих падений Revit при обновлении " +
                        "он не спасает — помогает повторный запуск.");
                }
            }

            return opt.SkipPreflight ? null : Preflight(t, opt, restVersion, clients, limits);
        }

        /// <summary>
        /// Итоговая сводка. Печатается последней и отвечает на два вопроса:
        /// сколько залилось и что чинить.
        ///
        /// Отказы сгруппированы по причинам, а не выданы плоским списком:
        /// в пакете из полусотни моделей два десятка строк «FAIL ...» ничего
        /// не сообщают, пока их не рассортируешь глазами. Сгруппированные —
        /// сразу показывают, что двадцать моделей уперлись в одно и то же
        /// и чинится это одним действием.
        /// </summary>
        private static void ReportSummary(List<UploadResult> succeeded, List<UploadResult> failed)
        {
            Log("");
            Log("==================== ИТОГ ====================");
            Log($"Успешно залито: {succeeded.Count}");
            Log($"С ошибкой:      {failed.Count}");

            ReportFailureGroups(failed);

            Log("==============================================");
        }

        /// <summary>Отказы по причинам: причина, счёт, поимённый список.</summary>
        private static void ReportFailureGroups(List<UploadResult> failed)
        {
            if (failed == null || failed.Count == 0) return;

            Log("");
            Log("Не залито, по причинам:");

            foreach (var группа in FailureReasons.Group(failed))
            {
                Log("");
                Log($"  {группа.Reason} — {группа.Failures.Count} {Моделей(группа.Failures.Count)}:");

                foreach (var f in группа.Failures)
                {
                    Log($"    {SafeFileName(f.SourceFile)}  ->  {f.DestinationPath}");
                    Log($"      {Truncate(OneLine(f.Error), 300)}");
                }
            }
        }

        private static string Моделей(int n)
        {
            var сотня = n % 100;
            var единицы = n % 10;
            if (сотня >= 11 && сотня <= 14) return "моделей";
            if (единицы == 1) return "модель";
            if (единицы >= 2 && единицы <= 4) return "модели";
            return "моделей";
        }

        /// <summary>Имя файла без падения на пустом или кривом пути.</summary>
        private static string SafeFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "(источник не указан)";
            try { return Path.GetFileName(path); } catch { return path; }
        }

        /// <summary>Ошибка в одну строку: многострочные тексты ломают вид сводки.</summary>
        private static string OneLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(причина не указана)";
            return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        /// <summary>
        /// Год для имени сервиса Admin REST API. Если задан только --revit-exe,
        /// берём год из пути к нему: иначе RevitVersion == 0 и клиент молча уходит
        /// на ветку "2012" (сервис без года в имени), получая 404 на живом сервере.
        /// </summary>
        private static int ResolveRestVersion(Options opt)
        {
            if (opt.RevitVersion != 0) return opt.RevitVersion;

            var fromExe = RevitPath.VersionFromExePath(opt.RevitExe);
            if (fromExe != 0)
            {
                Log($"Версия для Admin REST API определена по пути к Revit.exe: {fromExe}");
                return fromExe;
            }

            throw new ArgumentException(
                "Не удалось определить версию Revit для Admin REST API. " +
                "Укажите --revit-version явно либо добавьте --skip-preflight.");
        }

        /// <summary>
        /// Проверки через Admin REST API. Возвращает причину отказа или null.
        /// Недоступность самого сервера — по-прежнему исключение: это беда всего
        /// запуска, а не одного задания.
        /// </summary>
        private static string Preflight(UploadTask t, Options opt, int restVersion,
                                        Dictionary<string, RevitServerRestClient> clients,
                                        Dictionary<string, ServerProperties> limits)
        {
            RevitServerRestClient.ParseRsn(t.DestinationPath, out var host, out var relative);

            if (!clients.TryGetValue(host, out var client))
            {
                client = new RevitServerRestClient(host, restVersion);
                Log($"Проверка сервера {host} ...");

                var props = client.GetServerPropertiesParsed();
                Log($"Сервер {host} ({props.MachineName}): максимум {props.MaximumFolderPathLength} " +
                    $"символов на путь папки, {props.MaximumModelNameLength} на имя модели.");
                LogLimits(opt, props);

                // Модели передаются НЕ по этому каналу. REST — это порт 80,
                // а заливка идёт по net.tcp на 808, к службе ModelService<год>.
                // Проверять надо оба: сервер, отвечающий по REST, вполне может
                // быть недоступен для заливки, и без этой проверки выяснится
                // это через пять-шесть минут после запуска Revit (B-30).
                var probe = NetTcpProbe.ProbeWithRetries(host, restVersion);
                switch (probe.Outcome)
                {
                    case NetTcpProbe.Outcome.Ok:
                        Log($"Канал передачи моделей (net.tcp, ModelService{restVersion}): " +
                            $"отвечает за {probe.ElapsedMs} мс.");
                        break;

                    case NetTcpProbe.Outcome.ServiceUnavailable:
                        throw new RevitServerException(
                            $"На сервере {host} нет службы передачи моделей ModelService{restVersion} " +
                            $"({probe.Detail}). Администраторская служба отвечает, но заливать некуда: " +
                            $"проверьте, установлен ли на сервере Revit Server {restVersion} " +
                            "и запущены ли его службы.");

                    case NetTcpProbe.Outcome.NoConnection:
                        throw new RevitServerException(
                            $"Сервер {host} отвечает по HTTP, но канал передачи моделей недоступен: " +
                            $"{probe.Detail}. Заливка идёт по net.tcp на порт {NetTcpProbe.DefaultPort} — " +
                            "проверьте маршрут, VPN и брандмауэр именно для этого порта.");

                    default:
                        throw new RevitServerException(
                            $"Канал передачи моделей на {host} ведёт себя неожиданно: {probe.Detail}. " +
                            "Заливка почти наверняка не пройдёт.");
                }

                clients[host] = client;
                limits[host] = props;
            }

            // Лимиты берём с конкретного сервера, а не из констант: он сообщает
            // их сам, и на разных серверах они могут отличаться. Настройка,
            // если задана, перекрывает сервер — см. CheckServerLimits.
            var limitError = CheckServerLimits(relative, limits[host], opt);
            if (limitError != null) return limitError;

            var folder = RevitServerRestClient.ParentOf(relative);
            if (!string.IsNullOrEmpty(folder))
            {
                if (!client.FolderExists(folder))
                {
                    if (!opt.CreateFolders)
                        return $"Папка '{folder}' не существует на сервере {host}. " +
                               "Добавьте --create-folders, чтобы создать её автоматически.";

                    Log($"Создаю папку: {folder}");
                    client.EnsureFolderChain(folder);
                }
            }

            if (client.ModelExists(relative))
            {
                if (!t.Overwrite)
                    return $"Модель уже существует: {t.DestinationPath}. Используйте --overwrite.";

                if (!client.TryGetLockState(relative, out var locks))
                {
                    // Revit Server 2021 не отвечает на GET /lock (405). Это не
                    // «блокировки нет» — это «узнать нельзя», и делать вид,
                    // что всё чисто, мы не будем: пишем прямо.
                    Log("ВНИМАНИЕ: сервер не сообщает состояние блокировки (GET /lock -> 405). " +
                        "Проверить занятость модели заранее невозможно; если она занята, " +
                        "заливка упадёт на SaveAs.");
                }
                else if (!string.IsNullOrWhiteSpace(locks))
                {
                    Log($"ВНИМАНИЕ: на модели есть блокировка. Ответ сервера: {Truncate(locks, 400)}");
                    if (!opt.IgnoreLocks)
                        return "Модель заблокирована (кто-то работает / завис lock). " +
                               "Снимите блокировку или запустите с --ignore-locks.";
                }
            }

            return null;
        }

        /// <summary>
        /// Пишет в лог, какие пределы длины будут применяться и откуда они взяты.
        ///
        /// Без этой строки отказ «имя длиннее 40» не с чем сопоставить: непонятно,
        /// это предел сервера или чья-то настройка, и что править — имя модели
        /// или конфигурацию запуска.
        /// </summary>
        private static void LogLimits(Options opt, ServerProperties props)
        {
            LogLimit("имени модели", opt.MaxModelNameLength,
                     props.MaximumModelNameLength, PathLimits.MaxServerModelName);

            LogLimit("пути папки", opt.MaxFolderPathLength,
                     props.MaximumFolderPathLength, PathLimits.MaxServerFolderPath);
        }

        private static void LogLimit(string что, int fromSettings, int fromServer, int fallback)
        {
            var предел = PathLimits.EffectiveLimit(fromSettings, fromServer, fallback);
            var источник = PathLimits.LimitSource(fromSettings, fromServer);

            if (предел <= 0)
            {
                Log($"Предел длины {что}: проверка отключена настройкой " +
                    $"(сервер сообщает {fromServer}).");
                return;
            }

            var сноска = fromSettings >= 0 && fromServer > 0 && fromSettings != fromServer
                ? $", сервер сообщает {fromServer}"
                : string.Empty;

            Log($"Предел длины {что}: {предел} символов ({источник}{сноска}).");
        }

        /// <summary>
        /// Проверка пути назначения по применяемым пределам длины.
        ///
        /// По умолчанию пределы берутся с сервера — он сообщает их сам. Настройка
        /// их перекрывает: объявленный сервером предел строже фактического, и сам
        /// Revit сохраняет туда же модели с именами длиннее. Отбраковывать такие
        /// модели значит запрещать то, что работает.
        ///
        /// Ловится до запуска Revit: иначе SaveAs упадёт уже после открытия
        /// модели, потратив впустую сессию Revit и лицензию.
        /// </summary>
        private static string CheckServerLimits(string serverRelativePath, ServerProperties props, Options opt)
        {
            var modelName = serverRelativePath;
            var sep = serverRelativePath.LastIndexOf('|');
            if (sep >= 0) modelName = serverRelativePath.Substring(sep + 1);

            var nameLimit = PathLimits.EffectiveLimit(
                opt.MaxModelNameLength, props.MaximumModelNameLength, PathLimits.MaxServerModelName);

            var nameError = PathLimits.ValidateServerModelName(
                modelName, nameLimit, PathLimits.LimitSource(opt.MaxModelNameLength, props.MaximumModelNameLength));
            if (nameError != null)
                return nameError + " Задайте другое имя: --dest-name <Имя.rvt> " +
                       "(в пакетном режиме — во второй колонке списка), " +
                       "либо поднимите предел ключом --max-model-name.";

            // Имя прошло только потому, что предел ослаблен: это стоит отметить
            // поимённо. Если сервер такую модель всё-таки не примет, в логе будет
            // видно, какие именно имена шли за пределом объявленного.
            if (props.MaximumModelNameLength > 0 && modelName.Length > props.MaximumModelNameLength)
                Log($"ВНИМАНИЕ: имя '{modelName}' — {modelName.Length} символов, " +
                    $"сервер объявляет предел {props.MaximumModelNameLength}. " +
                    "Пропущено по настройке.");

            var folder = RevitServerRestClient.ParentOf(serverRelativePath);
            var folderLimit = PathLimits.EffectiveLimit(
                opt.MaxFolderPathLength, props.MaximumFolderPathLength, PathLimits.MaxServerFolderPath);

            var folderError = PathLimits.ValidateServerFolder(
                folder, folderLimit, PathLimits.LimitSource(opt.MaxFolderPathLength, props.MaximumFolderPathLength));
            if (folderError != null)
                return folderError + " Либо поднимите предел ключом --max-folder-path.";

            var folderДляПоказа = folder == null ? string.Empty : folder.Replace('|', '\\');
            if (props.MaximumFolderPathLength > 0 &&
                folderДляПоказа.Length > props.MaximumFolderPathLength)
                Log($"ВНИМАНИЕ: путь папки '{folderДляПоказа}' — {folderДляПоказа.Length} символов, " +
                    $"сервер объявляет предел {props.MaximumFolderPathLength}. Пропущено по настройке.");

            return null;
        }

        private static List<UploadTask> BuildTasks(Options opt)
        {
            var tasks = new List<UploadTask>();

            if (opt.ListFile != null)
            {
                foreach (var line in File.ReadAllLines(opt.ListFile))
                {
                    var s = line.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;

                    string src, dest;
                    var sep = s.IndexOf('|');
                    if (sep > 0)
                    {
                        src = s.Substring(0, sep).Trim();
                        var second = s.Substring(sep + 1).Trim();

                        if (second.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                        {
                            // Полный путь назначения — папка и имя заданы явно.
                            dest = second;
                        }
                        else
                        {
                            // Только имя модели: папку берём из --dest-folder.
                            // Нужно, когда файл приходит с лишним суффиксом или
                            // с некорректным именем и его надо положить под другим.
                            if (opt.DestFolder == null)
                                throw new ArgumentException(
                                    $"В строке '{s}' задано только имя модели ('{second}'), " +
                                    "но не указан --dest-folder. Либо добавьте --dest-folder, " +
                                    "либо пишите полный путь 'RSN://...'.");

                            var nameError = Options.ValidateModelName(second);
                            if (nameError != null)
                                throw new ArgumentException($"Строка '{s}': {nameError}");

                            dest = CombineRsn(opt.DestFolder, second);
                        }
                    }
                    else
                    {
                        if (opt.DestFolder == null)
                            throw new ArgumentException(
                                $"Строка списка без назначения: '{s}'. Укажите --dest-folder " +
                                "или формат '<локальный.rvt>|RSN://...'.");
                        src = s;
                        dest = CombineRsn(opt.DestFolder, Path.GetFileName(src));
                    }

                    tasks.Add(MakeTask(src, dest, opt));
                }
            }
            else if (opt.SourceFolder != null)
            {
                tasks.AddRange(BuildTasksFromFolder(opt));
            }
            else if (opt.Source != null)
            {
                // Имя на сервере: по умолчанию как у исходного файла,
                // либо заданное явно через --dest-name.
                var dest = opt.Destination ?? (opt.DestFolder != null
                    ? CombineRsn(opt.DestFolder, opt.DestName ?? Path.GetFileName(opt.Source))
                    : throw new ArgumentException("Не указан --dest или --dest-folder."));

                tasks.Add(MakeTask(opt.Source, dest, opt));
            }

            CheckDestinationCollisions(tasks);
            return tasks;
        }

        /// <summary>
        /// Задания из папки на диске. Три раскладки по серверу — см. --structure.
        /// </summary>
        private static List<UploadTask> BuildTasksFromFolder(Options opt)
        {
            var scan = ModelScanner.Scan(opt.SourceFolder, opt.Recurse);

            Log($"Папка: {Path.GetFullPath(opt.SourceFolder)}" +
                (opt.Recurse ? " (с вложенными)" : " (без вложенных)"));

            // Пропущенное показываем всегда. Модель, молча не попавшая
            // в пакет, выглядит как потерянная, и искать её будут дольше,
            // чем читать эти строки.
            foreach (var s in scan.SkippedItems)
                Log($"  пропущено: {s.Path} — {s.Reason}");

            if (scan.Models.Count == 0)
                throw new ArgumentException(
                    $"В папке {Path.GetFullPath(opt.SourceFolder)} не найдено ни одной модели" +
                    (opt.Recurse ? "." : " (вложенные папки не просматривались из-за --no-recurse)."));

            Log($"Найдено моделей: {scan.Models.Count}. Раскладка: {StructureName(opt.Structure)}.");

            switch (opt.Structure)
            {
                case FolderStructure.Flat:
                    return scan.Models
                        .Select(m => MakeTask(m.FullPath, CombineRsn(opt.DestFolder, m.FileName), opt))
                        .ToList();

                case FolderStructure.Disk:
                    return scan.Models.Select(m =>
                    {
                        var folder = m.RelativeFolder.Length == 0
                            ? opt.DestFolder
                            : opt.DestFolder.TrimEnd('/', '\\') + "/" + m.RelativeFolder;
                        return MakeTask(m.FullPath, CombineRsn(folder, m.FileName), opt);
                    }).ToList();

                case FolderStructure.Server:
                    return BuildTasksByServerStructure(scan.Models, opt);

                default:
                    throw new ArgumentException($"Неизвестная раскладка: {opt.Structure}");
            }
        }

        /// <summary>
        /// Каждая модель кладётся туда, где она УЖЕ лежит на сервере. Обратная
        /// дорога для моделей, скачанных с сервера и обработанных на диске:
        /// структура сервера при этом не повторяет структуру диска, и
        /// восстанавливать её вручную — работа на полдня.
        ///
        /// Что делаем с моделями, которых на сервере нет, и с одноимёнными
        /// в разных папках: отбраковываем поимённо, а не гадаем. Ошибиться
        /// папкой здесь означает залить раздел поверх чужого.
        /// </summary>
        private static List<UploadTask> BuildTasksByServerStructure(
            List<ModelScanner.FoundModel> models, Options opt)
        {
            RevitServerRestClient.ParseRsn(opt.DestFolder, out var host, out var rootPath);
            var client = new RevitServerRestClient(host, opt.RevitVersion);

            Log($"Ищу модели в дереве сервера {host}, начиная с '{rootPath}' ...");
            var found = client.FindModels(rootPath, models.Select(m => m.FileName));

            var tasks = new List<UploadTask>();
            var problems = new List<string>();

            foreach (var m in models)
            {
                if (!found.TryGetValue(m.FileName, out var folders) || folders.Count == 0)
                {
                    problems.Add($"  {m.FileName}: на сервере не найдена");
                    continue;
                }

                if (folders.Count > 1)
                {
                    problems.Add($"  {m.FileName}: найдена в нескольких папках — " +
                                 string.Join(", ", folders.Select(f => "'" + f + "'").ToArray()));
                    continue;
                }

                var rsnFolder = "RSN://" + host + "/" + folders[0].Replace('|', '/');
                tasks.Add(MakeTask(m.FullPath, CombineRsn(rsnFolder, m.FileName), opt));
            }

            if (problems.Count > 0)
            {
                Log($"Не удалось определить место на сервере для {problems.Count} " +
                    $"из {models.Count} моделей:");
                foreach (var p in problems) Log(p);
                Log("Такие модели в пакет не попали. Залейте их отдельно, указав папку явно.");
            }

            if (tasks.Count == 0)
                throw new ArgumentException(
                    "Ни одной модели не нашлось на сервере — заливать нечего. " +
                    "Проверьте --dest-folder: поиск идёт от него вглубь.");

            return tasks;
        }

        private static string StructureName(FolderStructure s)
        {
            switch (s)
            {
                case FolderStructure.Disk: return "как на диске";
                case FolderStructure.Server: return "как на сервере";
                default: return "всё в одну папку";
            }
        }

        /// <summary>
        /// Локальный лимит длины пути — ограничение Revit на стороне клиента,
        /// сети для проверки не нужно. Лимиты сервера проверяются отдельно,
        /// в Preflight, по значениям с самого сервера.
        /// </summary>

        /// <summary>
        /// Два разных исходника, попадающих в один путь назначения, — это почти
        /// всегда ошибка составления списка (одинаковые имена файлов из разных
        /// папок при --dest-folder). Без проверки вторая модель молча затирает
        /// первую прямо на сервере, и следов в result.json не остаётся:
        /// оба задания отчитываются как успешные.
        /// </summary>
        private static void CheckDestinationCollisions(List<UploadTask> tasks)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in tasks)
            {
                if (seen.TryGetValue(t.DestinationPath, out var first))
                    throw new ArgumentException(
                        $"Коллизия назначения: '{first}' и '{t.SourceFile}' оба ведут в " +
                        $"{t.DestinationPath}. Вторая модель затёрла бы первую. " +
                        "Задайте разные имена явно через формат '<локальный.rvt>|RSN://...'.");

                seen[t.DestinationPath] = t.SourceFile;
            }
        }

        private static UploadTask MakeTask(string src, string dest, Options opt) => new UploadTask
        {
            SourceFile = Path.GetFullPath(src),
            DestinationPath = dest,
            Overwrite = opt.Overwrite,
            Audit = opt.Audit,
            MaximumBackups = opt.MaximumBackups,
            UnloadLinks = opt.UnloadLinks,
            Compact = opt.Compact,
            EnableWorksharingIfNeeded = !opt.NoEnableWorksharing,
            CloseUserWorksets = opt.CloseWorksets,
        };

        private static string CombineRsn(string folder, string fileName)
            => RevitServerRestClient.CombineRsn(folder, fileName);

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { /* не критично */ }
        }

        private static string Truncate(string s, int n)
            => s.Length <= n ? s : s.Substring(0, n) + "...";

        private static void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
RvsUpload — загрузка моделей НА Revit Server из командной строки.
Дополняет RevitServerTool.exe, который умеет только скачивать.

ИСПОЛЬЗОВАНИЕ

  Одна модель:
    RvsUpload.exe --source C:\out\Model.rvt --dest RSN://RVTSRV01/Projects/2026/Model.rvt --revit-version 2024

  Пакет:
    RvsUpload.exe --list files.txt --revit-version 2024 --overwrite
    RvsUpload.exe --list files.txt --dest-folder RSN://RVTSRV01/Projects/2026 --revit-version 2024

  Формат --list: одна модель на строку, три варианта:
      C:\out\A.rvt                             имя на сервере = имя файла
      C:\out\A.rvt|Другое_имя.rvt              имя задано явно (нужен --dest-folder)
      C:\out\A.rvt|RSN://RVTSRV01/Projects/A.rvt   полный путь назначения
    '#' — комментарий.

  Папка целиком (список поддерживать не надо):
    RvsUpload.exe --source-folder C:\Выгрузка --dest-folder RSN://RVTSRV01/Projects/2026 ^
                  --structure disk --create-folders --revit-version 2024

  Раскладка по серверу (--structure):
      flat     всё в одну папку --dest-folder. По умолчанию
      disk     повторить структуру подпапок с диска. Нужен --create-folders
      server   положить каждую модель туда, где она УЖЕ лежит на сервере;
               поиск идёт от --dest-folder вглубь. Обратная дорога для
               моделей, скачанных с сервера и обработанных на диске

  Резервные копии Revit (Модель.0001.rvt) и служебные папки (*_backup,
  Revit_temp) пропускаются — каждая пропущенная строка видна в логе.

ПАРАМЕТРЫ

  --source <path>          Локальный .rvt для загрузки
  --dest <RSN://...>       Полный путь назначения
  --dest-folder <RSN://..> Папка назначения; имя берётся из исходного файла
  --dest-name <Имя.rvt>    Имя модели НА СЕРВЕРЕ, если оно должно отличаться
                           от имени файла на диске (лишний суффикс, некорректное
                           имя от смежников). По умолчанию имя не меняется.
                           Только вместе с --dest-folder; в пакетном режиме имя
                           указывается во второй колонке списка.
  --list <file>            Файл со списком моделей (пакетный режим)
  --source-folder <path>   Папка с моделями вместо списка. Нужен --dest-folder
  --structure <режим>      flat | disk | server — см. выше. Только
                           с --source-folder
  --no-recurse             Не заходить во вложенные папки
  --config <settings.json> Файл настроек. Аргументы командной строки его
                           перекрывают. Редактируется страницей Settings.html

  --revit-version <год>    Версия Revit (обязательно, напр. 2024)
  --revit-exe <path>       Явный путь к Revit.exe (в обход поиска)
  --language <ENU|RUS>     Язык запуска Revit

  --overwrite              Перезаписать существующую центральную модель
  --create-folders         Создать отсутствующие папки на сервере (REST API)
  --ignore-locks           Не падать, если на модели висит блокировка
  --audit                  Audit при открытии исходника. Включается сам для
                           моделей, которым предстоит обновление версии.
                           От плавающих падений Revit при обновлении не спасает —
                           там помогает повторный запуск
  --max-backups <n>        Число бэкапов (0 = не задавать)
  --unload-links           Выгрузить RVT-связи перед заливкой
  --compact                Сжать модель при сохранении
  --no-enable-worksharing  Не включать worksharing автоматически (тогда
                           не-workshared модель будет отклонена)
  --on-upgrade <режим>     Что делать с моделями младших версий, которые Revit
                           обновит при открытии (это дольше):
                             upgrade обновлять и заливать (по умолчанию)
                             skip    заливать только модели своей версии
                             abort   не заливать ничего, если есть такие модели
                           Вопросов утилита не задаёт: работает одинаково
                           из рук и из Планировщика заданий.

  --close-worksets         Закрыть пользовательские рабочие наборы при открытии,
                           чтобы не подгружались связанные модели из них.
                           В отличие от --unload-links связи не открываются вовсе.
                           На результат не влияет: закрытый набор остаётся
                           в файле целиком. Закрываются и ""Общие уровни и сетки"" —
                           это тоже пользовательский набор.

  --retries <n>            Сколько раз перезапустить Revit с остатком
                           незалитых моделей (по умолчанию 0, максимум 10).
                           Нужен против плавающих падений Revit: на повреждённой
                           модели обновление версии роняло его примерно раз
                           из трёх, а повтор той же модели проходил успешно

  --startup-timeout <мин>  Сколько ждать, пока Revit дойдёт до аддина
                           (по умолчанию 5). Если не дошёл — он ждёт ответа
                           в модальном окне: занята лицензия, предложено
                           восстановление после падения, не подтверждена
                           загрузка неподписанной надстройки. Перехватить такое
                           окно нечем — аддина в процессе ещё нет

  --idle-timeout <мин>     Сколько терпеть молчание лога аддина, пока он уже
                           работает (по умолчанию 20). Ловит зависание на
                           середине пакета. Порог должен быть ЗАВЕДОМО БОЛЬШЕ
                           самого долгого нормального шага: во время SaveAs
                           лог молчит, и на большой модели это законные минуты

  --timeout <минуты>       Таймаут сессии Revit (по умолчанию 60)
  --log <file>             Файл лога
  --keep-temp              Не удалять временную папку сессии
  --skip-preflight         Пропустить проверки через REST API

  --max-model-name <n>     Свой предел длины имени модели вместо того, что
                           сообщает сервер. Нужен потому, что объявленный
                           сервером предел строже фактического: сам Revit
                           сохраняет на тот же сервер модели с именами длиннее.
                           0 отключает проверку. Без ключа предел берётся
                           с сервера

  --max-folder-path <n>    То же для длины пути папки на сервере.
                           0 отключает проверку
  --dry-run                Сгенерировать journal и выйти, Revit не запускать

КОДЫ ВОЗВРАТА
  0 успех | 1 аргументы | 2 Revit не найден | 3 ошибка загрузки
  4 таймаут | 5 ошибка Revit Server

ТРЕБОВАНИЯ
  - Установленный Revit той же версии, что и модель, и что и Revit Server.
  - Установленный аддин RvsUpload.Addin для этой версии Revit.
  - Свободная лицензия Revit на машине (сессия занимает лицензию).
");
        }
    }
}
