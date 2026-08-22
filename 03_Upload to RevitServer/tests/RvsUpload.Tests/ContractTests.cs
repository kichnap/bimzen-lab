using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace RvsUpload.Tests
{
    public class RsnPathTests
    {
        [Theory]
        [InlineData("RSN://RVTSRV01/Projects/2026/Model.rvt", "RVTSRV01", "Projects|2026|Model.rvt")]
        [InlineData("rsn://srv/Model.rvt", "srv", "Model.rvt")]
        [InlineData("RSN://srv//A//B/Model.rvt", "srv", "A|B|Model.rvt")]
        [InlineData(@"RSN://srv\A\Model.rvt", "srv", "A|Model.rvt")]
        public void ParseRsn_ValidPaths(string input, string expectedHost, string expectedRelative)
        {
            RevitServerRestClient.ParseRsn(input, out var host, out var relative);
            Assert.Equal(expectedHost, host);
            Assert.Equal(expectedRelative, relative);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"C:\local\Model.rvt")]
        [InlineData("http://srv/Model.rvt")]
        [InlineData("RSN://srv")]          // нет модели после имени сервера
        public void ParseRsn_InvalidPaths_Throw(string input)
        {
            Assert.Throws<ArgumentException>(() =>
                RevitServerRestClient.ParseRsn(input, out _, out _));
        }

        [Theory]
        // Корень сервера — одиночный '|'. Проверено на живом сервере 2021:
        // '/|/contents' отвечает, '/contents' и '/|contents' дают 405.
        [InlineData("", "|")]
        [InlineData(null, "|")]
        [InlineData("|", "|")]
        [InlineData("/", "|")]
        [InlineData("Projects/2026", "Projects|2026")]
        [InlineData(@"Projects\2026\M.rvt", "Projects|2026|M.rvt")]
        [InlineData("|Projects||2026|", "Projects|2026")]
        public void ToUrlPath(string input, string expected)
            => Assert.Equal(expected, RevitServerRestClient.ToUrlPath(input));

        /// <summary>
        /// Требование заказчика: модель ложится на сервер ПОД ТЕМ ЖЕ ИМЕНЕМ,
        /// что и на диске. Никаких суффиксов вроде «_БезРН» — в том числе для
        /// моделей без рабочих наборов, которым утилита включает worksharing.
        /// </summary>
        [Theory]
        [InlineData("RSN://srv/Folder", "Модель_ОВ.rvt", "RSN://srv/Folder/Модель_ОВ.rvt")]
        [InlineData("RSN://srv/Folder/", "Модель_ОВ.rvt", "RSN://srv/Folder/Модель_ОВ.rvt")]
        [InlineData(@"RSN://srv/Folder\", "Модель_ОВ.rvt", "RSN://srv/Folder/Модель_ОВ.rvt")]
        // Пробелы и кириллица в имени сохраняются дословно.
        [InlineData("RSN://srv/F", "Модель_ОВ_Без рабочих наборов.rvt",
                    "RSN://srv/F/Модель_ОВ_Без рабочих наборов.rvt")]
        public void CombineRsn_KeepsFileNameVerbatim(string folder, string name, string expected)
            => Assert.Equal(expected, RevitServerRestClient.CombineRsn(folder, name));

        [Fact]
        public void CombineRsn_AddsNoSuffixToNonWorksharedName()
        {
            // Имя модели без рабочих наборов — 33 символа, в лимит сервера (40)
            // укладывается, поэтому укорачивать его не требуется и нельзя.
            const string name = "Модель_ОВ_Без рабочих наборов.rvt";
            Assert.Equal(33, name.Length);

            var dest = RevitServerRestClient.CombineRsn("RSN://RVTSRV-TEST/Папка", name);

            Assert.EndsWith("/" + name, dest);
            Assert.DoesNotContain("БезРН", dest);
            Assert.Null(PathLimits.ValidateServerModelName(name, 40));
        }

        [Theory]
        [InlineData("Projects|2026|Model.rvt", "Projects|2026")]
        [InlineData("Model.rvt", "")]
        [InlineData("A|B", "A")]
        public void ParentOf_ReturnsFolder(string input, string expected)
        {
            Assert.Equal(expected, RevitServerRestClient.ParentOf(input));
        }
    }

    public class OptionsTests
    {
        private static string[] Min(params string[] extra)
            => new[] { "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2024" }
                .Concat(extra).ToArray();

        [Fact]
        public void Parse_MinimalValidArgs()
        {
            var o = Options.Parse(Min());
            Assert.Equal(@"C:\a.rvt", o.Source);
            Assert.Equal("RSN://s/a.rvt", o.Destination);
            Assert.Equal(2024, o.RevitVersion);
            Assert.Equal(60, o.TimeoutMinutes);
            Assert.False(o.Overwrite);
        }

        [Fact]
        public void Parse_Flags()
        {
            var o = Options.Parse(Min("--overwrite", "--audit", "--create-folders",
                "--compact", "--unload-links", "--dry-run", "--close-worksets",
                "--max-backups", "3"));
            Assert.True(o.CloseWorksets);
            Assert.True(o.Overwrite);
            Assert.True(o.Audit);
            Assert.True(o.CreateFolders);
            Assert.True(o.Compact);
            Assert.True(o.UnloadLinks);
            Assert.True(o.DryRun);
            Assert.Equal(3, o.MaximumBackups);
        }

        [Fact]
        public void Parse_UnknownFlag_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Options.Parse(Min("--wat")));
            Assert.Contains("--wat", ex.Message);
        }

        [Fact]
        public void Parse_MissingValue_Throws()
            => Assert.Throws<ArgumentException>(() =>
                Options.Parse(new[] { "--source", @"C:\a.rvt", "--revit-version" }));

        [Fact]
        public void Parse_NoSourceAndNoList_Throws()
            => Assert.Throws<ArgumentException>(() =>
                Options.Parse(new[] { "--revit-version", "2024" }));

        [Fact]
        public void Parse_SourceAndListTogether_Throws()
            => Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--list", @"C:\l.txt", "--revit-version", "2024"
            }));

        [Fact]
        public void Parse_DestAndDestFolderTogether_Throws()
            => Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt",
                "--dest-folder", "RSN://s", "--revit-version", "2024"
            }));

        [Fact]
        public void Parse_NoRevitVersion_ButExplicitExe_IsOk()
        {
            var o = Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt",
                "--revit-exe", @"C:\Program Files\Autodesk\Revit 2024\Revit.exe"
            });
            Assert.Equal(0, o.RevitVersion);
            Assert.NotNull(o.RevitExe);
        }

        [Theory]
        [InlineData("1999")]
        [InlineData("2200")]
        public void Parse_AbsurdRevitVersion_Throws(string version)
            => Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", version
            }));

        [Fact]
        public void Parse_ZeroTimeout_Throws()
            => Assert.Throws<ArgumentException>(() => Options.Parse(Min("--timeout", "0")));

        [Fact]
        public void Parse_Retries_DefaultsToZero()
            => Assert.Equal(0, Options.Parse(Min()).Retries);

        [Theory]
        [InlineData("1", 1)]
        [InlineData("3", 3)]
        [InlineData("10", 10)]
        public void Parse_Retries(string value, int expected)
            => Assert.Equal(expected, Options.Parse(Min("--retries", value)).Retries);

        [Fact]
        public void Parse_NegativeRetries_Throws()
            => Assert.Throws<ArgumentException>(() => Options.Parse(Min("--retries", "-1")));

        [Fact]
        public void Parse_AbsurdRetries_Throws()
        {
            // Повтор — средство против плавающего сбоя, а не способ долбиться
            // в стену: каждый круг занимает лицензию Revit на минуты.
            var ex = Assert.Throws<ArgumentException>(() => Options.Parse(Min("--retries", "100")));
            Assert.Contains("10", ex.Message);
        }
    }


    /// <summary>
    /// Границы взяты из реальных сообщений: Revit Server про 98 символов на путь
    /// папки, Revit — про 224 на локальный путь.
    /// </summary>
    public class PathLimitsTests
    {
        // Боевая папка с тестового сервера: 59 символов — проходит.
        private const string RealTestFolder =
            @"TestTest123456789TestTest123456789TestTest\SET987654321SET!";

        [Fact]
        public void RealTestFolder_IsWithinServerLimit()
        {
            Assert.Equal(59, RealTestFolder.Length);
            Assert.Null(PathLimits.ValidateServerFolder(RealTestFolder));
        }

        [Fact]
        public void ServerFolder_AtExactLimit_IsAccepted()
            => Assert.Null(PathLimits.ValidateServerFolder(new string('a', PathLimits.MaxServerFolderPath)));

        [Fact]
        public void ServerFolder_OverLimit_IsRejectedWithLength()
        {
            var error = PathLimits.ValidateServerFolder(new string('a', PathLimits.MaxServerFolderPath + 1));
            Assert.NotNull(error);
            Assert.Contains("99", error);
            Assert.Contains("98", error);
        }

        [Fact]
        public void ServerFolder_SeparatorCountsAsOneChar()
        {
            // Внутри REST API разделитель '|', у пользователя в сообщении — '\'.
            // Считать надо одинаково, иначе граница «поедет» на глубоких путях.
            var withPipes = string.Join("|", Enumerable.Repeat("aaaaaaaaa", 10));
            var withSlashes = withPipes.Replace('|', '\\');
            Assert.Equal(PathLimits.ValidateServerFolder(withSlashes) == null,
                         PathLimits.ValidateServerFolder(withPipes) == null);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ServerFolder_EmptyIsRoot_Accepted(string input)
            => Assert.Null(PathLimits.ValidateServerFolder(input));

        [Fact]
        public void LocalPath_OverLimit_IsRejected()
        {
            var error = PathLimits.ValidateLocalPath(@"C:\" + new string('a', PathLimits.MaxLocalPath));
            Assert.NotNull(error);
            Assert.Contains("224", error);
        }

        [Fact]
        public void LocalPath_AtExactLimit_IsAccepted()
            => Assert.Null(PathLimits.ValidateLocalPath(new string('a', PathLimits.MaxLocalPath)));
    }

    /// <summary>
    /// Предел длины: настройка перекрывает сервер. Причина в том, что
    /// объявленный сервером предел строже фактического — Revit сохраняет
    /// на тот же сервер модели с именами длиннее.
    /// </summary>
    public class LimitOverrideTests
    {
        [Fact]
        public void Settings_WinOverServer()
            => Assert.Equal(60, PathLimits.EffectiveLimit(60, 40, PathLimits.MaxServerModelName));

        [Fact]
        public void Server_UsedWhenSettingNotSet()
            => Assert.Equal(40, PathLimits.EffectiveLimit(PathLimits.LimitNotSet, 40, PathLimits.MaxServerModelName));

        [Fact]
        public void Fallback_UsedWhenServerSilent()
            => Assert.Equal(PathLimits.MaxServerModelName,
                            PathLimits.EffectiveLimit(PathLimits.LimitNotSet, 0, PathLimits.MaxServerModelName));

        [Fact]
        public void Zero_FromSettings_DisablesCheck_AndIsNotMistakenForSilence()
        {
            // Ноль от человека — это «не проверять», а не «сервер промолчал».
            Assert.Equal(0, PathLimits.EffectiveLimit(0, 40, PathLimits.MaxServerModelName));
            Assert.Null(PathLimits.ValidateServerModelName(new string('a', 500), 0));
            Assert.Null(PathLimits.ValidateServerFolder(new string('a', 500), 0));
        }

        [Fact]
        public void LongName_PassesWithRaisedLimit_AndFailsWithServerLimit()
        {
            var name = new string('a', 45) + ".rvt";

            Assert.NotNull(PathLimits.ValidateServerModelName(name, 40));
            Assert.Null(PathLimits.ValidateServerModelName(name, 60));
        }

        [Fact]
        public void Error_NamesTheSourceOfLimit()
        {
            var error = PathLimits.ValidateServerModelName(new string('a', 50), 40, "задано настройкой");
            Assert.Contains("задано настройкой", error);
            Assert.Contains("40", error);
            Assert.Contains("50", error);
        }

        [Theory]
        [InlineData(60, 40, "задано настройкой")]
        [InlineData(PathLimits.LimitNotSet, 40, "сообщает сервер")]
        [InlineData(PathLimits.LimitNotSet, 0, "запасное значение, сервер лимит не сообщил")]
        public void LimitSource_TellsWhereTheNumberCameFrom(int fromSettings, int fromServer, string expected)
            => Assert.Equal(expected, PathLimits.LimitSource(fromSettings, fromServer));

        [Fact]
        public void CommandLine_SetsLimits()
        {
            var o = Options.Parse(new[]
            {
                "--source", @"C:.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2024",
                "--max-model-name", "60", "--max-folder-path", "0",
            });

            Assert.Equal(60, o.MaxModelNameLength);
            Assert.Equal(0, o.MaxFolderPathLength);
        }

        [Fact]
        public void NotSpecified_MeansTakeFromServer()
        {
            var o = Options.Parse(new[]
            {
                "--source", @"C:.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2024",
            });

            Assert.Equal(PathLimits.LimitNotSet, o.MaxModelNameLength);
            Assert.Equal(PathLimits.LimitNotSet, o.MaxFolderPathLength);
        }

        [Fact]
        public void NegativeLimit_IsRejected()
            => Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            {
                "--source", @"C:.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2024",
                "--max-model-name", "-5",
            }));

        [Fact]
        public void Config_SetsLimits_AndCommandLineWins()
        {
            var cfg = new SettingsFile { MaxModelNameLength = 60, MaxFolderPathLength = 120, RevitVersion = 2024 };
            var args = new[] { "--source", @"C:.rvt", "--dest", "RSN://s/a.rvt" };

            var изФайла = Options.Parse(args, cfg);
            Assert.Equal(60, изФайла.MaxModelNameLength);
            Assert.Equal(120, изФайла.MaxFolderPathLength);

            var изАргументов = Options.Parse(
                args.Concat(new[] { "--max-model-name", "80" }).ToArray(), cfg);
            Assert.Equal(80, изАргументов.MaxModelNameLength);
            Assert.Equal(120, изАргументов.MaxFolderPathLength);
        }
    }

    /// <summary>
    /// Группировка отказов для сводки: причина важнее текста, потому что текст
    /// у каждой модели свой и группировать по нему нечего.
    /// </summary>
    /// <summary>
    /// Проверка структуры на повторе. Повторяются те модели, с которыми уже
    /// что-то не так, — там Audit и нужен. На первой попытке он замедлил бы
    /// открытие всего пакета ради тех единиц, что упадут.
    /// </summary>
    public class AuditOnRetryTests
    {
        private static string[] Min(params string[] extra)
            => new[] { "--source", @"C:.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2024" }
                .Concat(extra).ToArray();

        [Fact]
        public void OnByDefault()
            => Assert.True(Options.Parse(Min()).AuditOnRetry);

        [Fact]
        public void CanBeTurnedOff()
            => Assert.False(Options.Parse(Min("--no-audit-on-retry")).AuditOnRetry);

        [Fact]
        public void FirstAttempt_IsNotAudited_UnlessAsked()
        {
            Assert.False(Options.Parse(Min()).Audit);
            Assert.True(Options.Parse(Min("--audit")).Audit);
        }

        [Fact]
        public void Config_CanTurnItOff_AndFlagWins()
        {
            var cfg = new SettingsFile { AuditOnRetry = false };

            Assert.False(Options.Parse(Min(), cfg).AuditOnRetry);

            // Ключ в командной строке — явное «не надо», файл его не перекрывает.
            var cfgOn = new SettingsFile { AuditOnRetry = true };
            Assert.False(Options.Parse(Min("--no-audit-on-retry"), cfgOn).AuditOnRetry);
        }
    }


    public class FailureReasonsTests
    {
        private static UploadResult Fail(string error, string source = @"C:\M.rvt")
            => new UploadResult { SourceFile = source, DestinationPath = "RSN://s/M.rvt", Error = error };

        [Theory]
        [InlineData("Имя модели — 45 символов, а допустимо не длиннее 40", "Слишком длинное имя модели")]
        [InlineData("Путь папки на сервере — 120 символов", "Слишком длинный путь папки на сервере")]
        [InlineData("Локальный путь — 240 символов", "Слишком длинный путь к модели на диске")]
        [InlineData("Модель уже существует: RSN://s/M.rvt", "Модель уже есть на сервере (нужен --overwrite)")]
        [InlineData("Модель заблокирована (кто-то работает)", "Модель заблокирована на сервере")]
        [InlineData("Revit не дошёл до аддина (модальное окно)", "Revit не дошёл до надстройки")]
        [InlineData("InvalidOperationException: Saving failed.", "Revit не смог сохранить модель на сервер")]
        public void Classify_KnownMessages(string error, string expected)
            => Assert.Equal(expected, FailureReasons.Classify(error));

        [Fact]
        public void Classify_UnknownMessage_GoesToOther()
            => Assert.Equal("Прочее", FailureReasons.Classify("Что-то совсем неожиданное от Revit"));

        [Fact]
        public void Classify_EmptyMessage_IsNotLost()
            => Assert.Equal("Причина не указана", FailureReasons.Classify(null));

        [Fact]
        public void Group_BiggestFirst_OtherLast()
        {
            var отказы = new[]
            {
                Fail("Что-то неожиданное"),
                Fail("Модель заблокирована (кто-то работает)"),
                Fail("Имя модели — 45 символов"),
                Fail("Имя модели — 50 символов"),
                Fail("Имя модели — 60 символов"),
            };

            var группы = FailureReasons.Group(отказы);

            Assert.Equal("Слишком длинное имя модели", группы[0].Reason);
            Assert.Equal(3, группы[0].Failures.Count);
            Assert.Equal("Модель заблокирована на сервере", группы[1].Reason);
            Assert.Equal("Прочее", группы[группы.Count - 1].Reason);
        }

        [Fact]
        public void Group_KeepsEveryFailure()
        {
            var отказы = Enumerable.Range(0, 7)
                .Select(i => Fail(i % 2 == 0 ? "Имя модели — 45 символов" : "Своя беда " + i))
                .ToArray();

            Assert.Equal(7, FailureReasons.Group(отказы).Sum(g => g.Failures.Count));
        }

        [Fact]
        public void Group_EmptyList_GivesNoGroups()
            => Assert.Empty(FailureReasons.Group(new UploadResult[0]));
    }


    /// <summary>
    /// Чтение лога аддина по ходу работы. Пока Revit заливает пакет, его лог —
    /// единственный источник сведений о том, что происходит, поэтому строки
    /// должны доходить до консоли сразу и ровно по одному разу.
    /// </summary>
    public class AddinLogTailTests
    {
        private static string TempFile()
            => Path.Combine(Path.GetTempPath(), "rvsupload_tail_" + Guid.NewGuid().ToString("N") + ".log");

        /// <summary>Пишет так же, как аддин: файл остаётся открытым, строки сбрасываются сразу.</summary>
        private static StreamWriter OpenLikeAddin(string path)
            => new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
               { AutoFlush = true };

        [Fact]
        public void NewLines_AreForwardedOnce()
        {
            var path = TempFile();
            try
            {
                var видел = new List<string>();
                var tail = new AddinLogTail(path);

                using (var w = OpenLikeAddin(path))
                {
                    w.WriteLine("[03:29:45] Аддин стартовал.");
                    tail.Pump(видел.Add);

                    w.WriteLine(@"[03:30:01] УСПЕХ: C:\A.rvt -> RSN://s/A.rvt");
                    tail.Pump(видел.Add);

                    // Повторный вызов без новых строк ничего не добавляет.
                    tail.Pump(видел.Add);
                }

                Assert.Equal(2, видел.Count);
                Assert.Contains("Аддин стартовал.", видел[0]);
                Assert.Contains("УСПЕХ:", видел[1]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void OwnTimestamp_IsStripped_SoLogHasOnlyOne()
        {
            var path = TempFile();
            try
            {
                var видел = new List<string>();
                using (var w = OpenLikeAddin(path)) w.WriteLine("[03:29:45] Открываю модель ...");
                new AddinLogTail(path).Pump(видел.Add);

                Assert.Equal("аддин: Открываю модель ...", видел.Single());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LineWithoutTimestamp_IsKeptWhole()
        {
            var path = TempFile();
            try
            {
                var видел = new List<string>();
                using (var w = OpenLikeAddin(path)) w.WriteLine("продолжение без отметки времени");
                new AddinLogTail(path).Pump(видел.Add);

                Assert.Equal("аддин: продолжение без отметки времени", видел.Single());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void HalfWrittenLine_WaitsForItsEnd()
        {
            var path = TempFile();
            try
            {
                var видел = new List<string>();
                var tail = new AddinLogTail(path);

                using (var w = OpenLikeAddin(path))
                {
                    // Момент чтения попал в середину записи строки.
                    w.Write("[03:31:00] SaveAs (central) -> ");
                    tail.Pump(видел.Add);
                    Assert.Empty(видел);

                    w.WriteLine("RSN://s/A.rvt ...");
                    tail.Pump(видел.Add);
                }

                Assert.Equal("аддин: SaveAs (central) -> RSN://s/A.rvt ...", видел.Single());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void MissingFile_IsNotAnError()
        {
            var видел = new List<string>();
            new AddinLogTail(TempFile()).Pump(видел.Add);
            Assert.Empty(видел);
        }

        [Fact]
        public void RecreatedFile_IsReadFromStart()
        {
            var path = TempFile();
            try
            {
                var видел = new List<string>();
                var tail = new AddinLogTail(path);

                using (var w = OpenLikeAddin(path)) w.WriteLine("[03:29:45] Первая сессия, длинная строка.");
                tail.Pump(видел.Add);
                Assert.Single(видел);

                // Файл пересоздали — читаем сначала, иначе молчали бы до конца.
                File.Delete(path);
                using (var w = OpenLikeAddin(path)) w.WriteLine("[03:40:00] Вторая.");
                tail.Pump(видел.Add);

                Assert.Equal(2, видел.Count);
                Assert.Contains("Вторая.", видел[1]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void CyrillicSurvives()
        {
            var path = TempFile();
            try
            {
                var видел = new List<string>();
                using (var w = OpenLikeAddin(path))
                    w.WriteLine(@"[03:29:45] УСПЕХ: p:\Модели\ПП_МВЛВ_Театр_АР.rvt -> RSN://сервер/Папка/ПП.rvt");
                new AddinLogTail(path).Pump(видел.Add);

                Assert.Contains("ПП_МВЛВ_Театр_АР.rvt", видел.Single());
            }
            finally { File.Delete(path); }
        }
    }


    public class ServerPropertiesTests
    {
        // Дословный ответ живого сервера RVTSRV-TEST (Revit Server 2021).
        private const string RealResponse =
            "{\"AccessLevelTypes\":[],\"MachineName\":\"REVIT\",\"MaximumFolderPathLength\":98," +
            "\"MaximumModelNameLength\":40,\"ServerRoles\":[0,1,2],\"Servers\":[\"localhost\",\"revit\"]}";

        [Fact]
        public void Parses_RealServerResponse()
        {
            var p = Json.Deserialize<ServerProperties>(RealResponse);

            Assert.Equal("REVIT", p.MachineName);
            Assert.Equal(98, p.MaximumFolderPathLength);
            Assert.Equal(40, p.MaximumModelNameLength);
            Assert.Equal(new[] { "localhost", "revit" }, p.Servers);
        }

        [Fact]
        public void Parses_WhenMembersOutOfAlphabeticalOrder()
        {
            // DataContractJsonSerializer чувствителен к порядку членов. Ответ живого
            // сервера пришёл в алфавитном порядке, но полагаться на это нельзя —
            // другой сервер или версия могут отдать иначе.
            var shuffled =
                "{\"Servers\":[\"a\"],\"MaximumModelNameLength\":40,\"MachineName\":\"X\"," +
                "\"MaximumFolderPathLength\":98,\"ServerRoles\":[0],\"AccessLevelTypes\":[]}";

            var p = Json.Deserialize<ServerProperties>(shuffled);
            Assert.Equal("X", p.MachineName);
            Assert.Equal(98, p.MaximumFolderPathLength);
            Assert.Equal(40, p.MaximumModelNameLength);
        }

        [Fact]
        public void Parses_WhenUnknownMembersPresent()
        {
            // Новая версия сервера может добавить поля — они не должны ломать разбор.
            var extra =
                "{\"AccessLevelTypes\":[],\"MachineName\":\"X\",\"MaximumFolderPathLength\":98," +
                "\"MaximumModelNameLength\":40,\"NewFieldFromFutureVersion\":\"whatever\"," +
                "\"ServerRoles\":[0],\"Servers\":[]}";

            Assert.Equal(98, Json.Deserialize<ServerProperties>(extra).MaximumFolderPathLength);
        }

        [Fact]
        public void RealServerLimits_RejectTheLongTestModel()
        {
            var p = Json.Deserialize<ServerProperties>(RealResponse);

            // Длинная тестовая модель из docs/ — 115 символов в имени.
            const string longName =
                "project_2024_q3_client_bigcorp_render_pipeline_nuke_v12_source_assets_" +
                "final_delivery_package_with_color_grading.rvt";
            Assert.Equal(115, longName.Length);

            var error = PathLimits.ValidateServerModelName(longName, p.MaximumModelNameLength);
            Assert.NotNull(error);
            Assert.Contains("115", error);
            Assert.Contains("40", error);

            // Короткие имена из того же набора проходят.
            Assert.Null(PathLimits.ValidateServerModelName("Модель_ОВ.rvt", p.MaximumModelNameLength));
            Assert.Null(PathLimits.ValidateServerModelName(
                "Модель_ОВ_Без рабочих наборов.rvt", p.MaximumModelNameLength));
        }

        [Fact]
        public void ServerSilent_MeansFallback_NotUnlimited()
        {
            // Сервер не сообщил лимит — берём запасное значение, а не «без ограничений».
            // Подстановку делает EffectiveLimit: в саму проверку приходит уже готовое
            // число, а ноль в ней значит «не проверять» и приходит только от настройки.
            var имя = PathLimits.EffectiveLimit(PathLimits.LimitNotSet, 0, PathLimits.MaxServerModelName);
            var путь = PathLimits.EffectiveLimit(PathLimits.LimitNotSet, 0, PathLimits.MaxServerFolderPath);

            Assert.NotNull(PathLimits.ValidateServerModelName(new string('a', 41), имя));
            Assert.NotNull(PathLimits.ValidateServerFolder(new string('a', 99), путь));
        }
    }

    /// <summary>
    /// Имя модели на сервере: по умолчанию как у файла, но задаваемое явно.
    /// Требование заказчика — файлы приходят с лишними суффиксами и с именами,
    /// которые надо исправлять при заливке.
    /// </summary>
    public class DestNameTests
    {
        private static string[] WithDestFolder(params string[] extra)
            => new[] { "--source", @"C:\a.rvt", "--dest-folder", "RSN://s/F", "--revit-version", "2021" }
                .Concat(extra).ToArray();

        [Fact]
        public void DestName_IsParsed()
            => Assert.Equal("Чистое_имя.rvt",
                Options.Parse(WithDestFolder("--dest-name", "Чистое_имя.rvt")).DestName);

        [Fact]
        public void DestName_DefaultsToNull()
            => Assert.Null(Options.Parse(WithDestFolder()).DestName);

        [Fact]
        public void DestName_WithDest_Throws()
        {
            // --dest уже содержит имя, две команды об одном — повод для опечатки.
            var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--dest", "RSN://s/F/a.rvt",
                "--dest-name", "b.rvt", "--revit-version", "2021"
            }));
            Assert.Contains("--dest", ex.Message);
        }

        [Fact]
        public void DestName_WithList_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[]
            {
                "--list", @"C:\l.txt", "--dest-folder", "RSN://s/F",
                "--dest-name", "b.rvt", "--revit-version", "2021"
            }));
            Assert.Contains("--list", ex.Message);
        }

        [Theory]
        [InlineData(@"Папка\Имя.rvt")]
        [InlineData("Папка/Имя.rvt")]
        [InlineData("Папка|Имя.rvt")]
        public void DestName_WithPathSeparator_Throws(string name)
            => Assert.Throws<ArgumentException>(() => Options.Parse(WithDestFolder("--dest-name", name)));

        [Fact]
        public void DestName_WithoutRvtExtension_Throws()
        {
            // Расширение не дописывается автоматически: имя на сервере должно быть
            // ровно тем, что задал пользователь.
            var ex = Assert.Throws<ArgumentException>(() => Options.Parse(WithDestFolder("--dest-name", "Модель")));
            Assert.Contains(".rvt", ex.Message);
        }

        [Theory]
        [InlineData("Модель.rvt")]
        [InlineData("Модель.RVT")]
        [InlineData("Модель с пробелами.rvt")]
        public void ValidateModelName_AcceptsValid(string name)
            => Assert.Null(Options.ValidateModelName(name));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidateModelName_RejectsEmpty(string name)
            => Assert.NotNull(Options.ValidateModelName(name));
    }

    /// <summary>
    /// Ранняя проверка версии модели — до запуска Revit. Формат читается прямо
    /// из .rvt, где он лежит в UTF-16 открытым текстом.
    /// </summary>
    public class RvtFileInfoTests
    {
        /// <summary>Собирает буфер, похожий на кусок потока BasicFileInfo.</summary>
        private static byte[] Fake(string format, int padding = 0)
        {
            var text = "Worksharing: Central\r\nFormat: " + format + "\r\nBuild: 20230907_1515(x64)\r\n";
            var body = System.Text.Encoding.Unicode.GetBytes(text);
            if (padding == 0) return body;

            var buf = new byte[body.Length + padding];
            Array.Copy(body, 0, buf, padding, body.Length);
            return buf;
        }

        [Theory]
        [InlineData("2020")]
        [InlineData("2021")]
        [InlineData("2023")]
        public void ReadsFormat(string format)
            => Assert.Equal(format, RvtFileInfo.TryReadFormatFromBytes(Fake(format)));

        [Fact]
        public void ReadsFormat_AtOddByteOffset()
        {
            // Начало потока не обязано попадать на чётный байт, поэтому разбор
            // пробует оба выравнивания UTF-16.
            Assert.Equal("2021", RvtFileInfo.TryReadFormatFromBytes(Fake("2021", padding: 1)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        public void ReturnsNull_OnGarbage(byte[] bytes)
            => Assert.Null(RvtFileInfo.TryReadFormatFromBytes(bytes));

        [Fact]
        public void ReturnsNull_WhenNoFormatPresent()
            => Assert.Null(RvtFileInfo.TryReadFormatFromBytes(
                System.Text.Encoding.Unicode.GetBytes("тут нет ничего похожего")));

        [Fact]
        public void ValidateVersion_UnknownFormat_IsSilent()
        {
            // Прочитать не удалось — не наше дело падать, проверит аддин.
            Assert.Null(RvtFileInfo.ValidateVersion(@"C:\нет-такого-файла.rvt", 2021));
        }

        [Fact]
        public void ValidateVersion_NoTargetVersion_IsSilent()
            => Assert.Null(RvtFileInfo.ValidateVersion(@"C:\a.rvt", 0));
    }

    /// <summary>
    /// Политика по моделям младших версий.
    ///
    /// Ключевое требование заказчика: консоль работает БЕЗ ВОПРОСОВ. Утилита
    /// должна одинаково отрабатывать из рук и из Планировщика заданий, поэтому
    /// интерактивного режима нет вовсе — только явный ключ и умолчание.
    /// </summary>
    public class UpgradePolicyTests
    {
        private static string[] Min(params string[] extra)
            => new[] { "--source", @"C:.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2021" }
                .Concat(extra).ToArray();

        [Theory]
        [InlineData("upgrade", UpgradePolicy.Upgrade)]
        [InlineData("skip", UpgradePolicy.Skip)]
        [InlineData("abort", UpgradePolicy.Abort)]
        [InlineData("SKIP", UpgradePolicy.Skip)]
        [InlineData("  skip  ", UpgradePolicy.Skip)]
        public void Parses(string value, UpgradePolicy expected)
            => Assert.Equal(expected, Options.Parse(Min("--on-upgrade", value)).OnUpgrade);

        [Fact]
        public void DefaultsToUpgrade()
        {
            // Модель младшей версии Revit открывает штатно, просто дольше.
            // Умолчание не должно требовать вмешательства человека.
            Assert.Equal(UpgradePolicy.Upgrade, Options.Parse(Min()).OnUpgrade);
        }

        [Fact]
        public void UnknownValue_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => Options.Parse(Min("--on-upgrade", "может-быть")));
            Assert.Contains("upgrade, skip, abort", ex.Message);
        }

        [Fact]
        public void HasNoInteractiveMode()
        {
            // Сторож: значения 'ask' быть не должно. Вопрос в консоли повесил бы
            // запуск из Планировщика заданий, где отвечать некому.
            Assert.Throws<ArgumentException>(() => Options.Parse(Min("--on-upgrade", "ask")));
            Assert.DoesNotContain("Ask", Enum.GetNames(typeof(UpgradePolicy)));
        }
    }

    /// <summary>
    /// Разбор вывода lmstat. Проверка лицензии до старта работает только при
    /// СЕТЕВОМ лицензировании; при single-user узнать занятость нечем — там
    /// спасает сторож старта.
    /// </summary>
    public class LmStatTests
    {
        // Дословный формат вывода `lmutil lmstat -a`.
        private const string Sample = @"
lmutil - Copyright (c) 1989-2018 Flexera. All Rights Reserved.
Flexible License Manager status on Thu 8/14/2026 18:20

Users of 86717REVIT_2021_0F:  (Total of 5 licenses issued;  Total of 3 licenses in use)

  ""86717REVIT_2021_0F"" v1.000, vendor: adskflex
    floating license

    user1 host1 host1 (v1.000) (srv/2080 101), start Thu 8/14 9:00

Users of 86718REVIT_2025_0F:  (Total of 2 licenses issued;  Total of 2 licenses in use)

Users of 85853ACD_2021_0F:  (Total of 10 licenses issued;  Total of 1 license in use)
";

        [Fact]
        public void Parse_FindsAllFeatures()
            => Assert.Equal(3, LmStat.Parse(Sample).Count);

        [Fact]
        public void Parse_ReadsCounts()
        {
            var f = LmStat.FindRevit(LmStat.Parse(Sample), 2021);

            Assert.NotNull(f);
            Assert.Equal("86717REVIT_2021_0F", f.Feature);
            Assert.Equal(5, f.Total);
            Assert.Equal(3, f.InUse);
            Assert.Equal(2, f.Available);
        }

        [Fact]
        public void FindRevit_PicksRightYear()
        {
            var f = LmStat.FindRevit(LmStat.Parse(Sample), 2025);

            Assert.NotNull(f);
            Assert.Equal(0, f.Available);   // всё занято
        }

        [Fact]
        public void FindRevit_IgnoresOtherProducts()
        {
            // AutoCAD 2021 в том же ответе не должен приниматься за Revit.
            var f = LmStat.FindRevit(LmStat.Parse(Sample), 2021);
            Assert.DoesNotContain("ACD", f.Feature);
        }

        [Fact]
        public void FindRevit_MissingYear_ReturnsNull()
            => Assert.Null(LmStat.FindRevit(LmStat.Parse(Sample), 2023));

        [Fact]
        public void Parse_SingularLicenseWording()
        {
            // «1 license in use» — единственное число, регулярка обязана его брать.
            var f = LmStat.Parse("Users of X_2021_0F:  (Total of 1 license issued;  Total of 1 license in use)");
            Assert.Single(f);
            Assert.Equal(0, f[0].Available);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("lmgrd is not running: Cannot connect to license server system.")]
        public void Parse_GarbageIsEmpty(string output)
            => Assert.Empty(LmStat.Parse(output));

        [Fact]
        public void FindRevit_NullSafe()
            => Assert.Null(LmStat.FindRevit(null, 2021));
    }

    /// <summary>
    /// Два сторожа с разными задачами: старта (Revit не дошёл до аддина)
    /// и простоя (аддин работает, но замолчал).
    /// </summary>
    public class WatchdogTests
    {
        private static string[] Min(params string[] extra)
            => new[] { "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt", "--revit-version", "2021" }
                .Concat(extra).ToArray();

        [Fact]
        public void StartupTimeout_DefaultsToFiveMinutes()
            => Assert.Equal(5, Options.Parse(Min()).StartupTimeoutMinutes);

        [Fact]
        public void StartupTimeout_Parsed()
            => Assert.Equal(12, Options.Parse(Min("--startup-timeout", "12")).StartupTimeoutMinutes);

        [Fact]
        public void StartupTimeout_ZeroThrows()
            => Assert.Throws<ArgumentException>(() => Options.Parse(Min("--startup-timeout", "0")));

        [Fact]
        public void IdleTimeout_DefaultIsGenerous()
        {
            // Во время SaveAs лог молчит, и на большой модели это законные
            // минуты. Порог обязан быть заведомо больше самого долгого
            // нормального шага, иначе сторож начнёт убивать здоровые сессии.
            var o = Options.Parse(Min());

            Assert.Equal(20, o.IdleTimeoutMinutes);
            Assert.True(o.IdleTimeoutMinutes > o.StartupTimeoutMinutes,
                "простой должен терпеться дольше, чем старт: SaveAs идёт молча");
        }

        [Fact]
        public void IdleTimeout_Parsed()
            => Assert.Equal(35, Options.Parse(Min("--idle-timeout", "35")).IdleTimeoutMinutes);

        [Fact]
        public void IdleTimeout_ZeroThrows()
            => Assert.Throws<ArgumentException>(() => Options.Parse(Min("--idle-timeout", "0")));

        [Fact]
        public void IdleTimeout_FromConfig()
            => Assert.Equal(30, Options.Parse(Min(), new SettingsFile { IdleTimeoutMinutes = 30 }).IdleTimeoutMinutes);

        [Fact]
        public void IdleTimeout_CommandLineWinsOverConfig()
            => Assert.Equal(7,
                Options.Parse(Min("--idle-timeout", "7"), new SettingsFile { IdleTimeoutMinutes = 30 }).IdleTimeoutMinutes);
    }

    /// <summary>
    /// Файл настроек и его слияние с командной строкой.
    /// Главное правило: аргументы ПЕРЕКРЫВАЮТ файл — иначе разовое
    /// переопределение из командной строки было бы невозможно.
    /// </summary>
    public class SettingsFileTests
    {
        private static string[] Source()
            => new[] { "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt" };

        [Fact]
        public void Config_FillsWhatCommandLineOmits()
        {
            var cfg = new SettingsFile
            {
                RevitVersion = 2021,
                Retries = 3,
                Overwrite = true,
                OnUpgrade = "skip",
                TimeoutMinutes = 45,
            };

            var o = Options.Parse(Source(), cfg);

            Assert.Equal(2021, o.RevitVersion);
            Assert.Equal(3, o.Retries);
            Assert.True(o.Overwrite);
            Assert.Equal(UpgradePolicy.Skip, o.OnUpgrade);
            Assert.Equal(45, o.TimeoutMinutes);
        }

        [Fact]
        public void CommandLine_WinsOverConfig()
        {
            var cfg = new SettingsFile { RevitVersion = 2021, Retries = 3, TimeoutMinutes = 45 };

            var o = Options.Parse(
                Source().Concat(new[] { "--revit-version", "2025", "--retries", "1" }).ToArray(), cfg);

            Assert.Equal(2025, o.RevitVersion);   // из командной строки
            Assert.Equal(1, o.Retries);           // из командной строки
            Assert.Equal(45, o.TimeoutMinutes);   // из файла — в аргументах не задан
        }

        [Fact]
        public void CommandLineFlag_WinsOverConfigEvenWhenConfigSaysFalse()
        {
            // Флаг в командной строке — это явное «включить». Файл, где стоит
            // false, не должен его отменять: приоритет у того, кто ближе к рукам.
            var cfg = new SettingsFile { RevitVersion = 2021, Overwrite = false };

            var o = Options.Parse(Source().Concat(new[] { "--overwrite" }).ToArray(), cfg);

            Assert.True(o.Overwrite);
        }

        [Fact]
        public void NullInConfig_DoesNotTouchDefaults()
        {
            // Незаданное поле файла обязано оставить умолчание утилиты.
            // Ради этого поля в SettingsFile и сделаны nullable.
            var o = Options.Parse(
                Source().Concat(new[] { "--revit-version", "2021" }).ToArray(), new SettingsFile());

            Assert.Equal(60, o.TimeoutMinutes);
            Assert.Equal(5, o.StartupTimeoutMinutes);
            Assert.Equal(0, o.Retries);
            Assert.False(o.Overwrite);
            Assert.Equal(UpgradePolicy.Upgrade, o.OnUpgrade);
        }

        [Fact]
        public void Config_CanSupplyListAndDestFolder()
        {
            // Пайплайну достаточно одной короткой строки запуска.
            var cfg = new SettingsFile
            {
                RevitVersion = 2021,
                ListFile = @"C:\pipeline\models.txt",
                DestFolder = "RSN://RVTSRV-TEST/Проекты/2026",
            };

            var o = Options.Parse(new string[0], cfg);

            Assert.Equal(@"C:\pipeline\models.txt", o.ListFile);
            Assert.Equal("RSN://RVTSRV-TEST/Проекты/2026", o.DestFolder);
        }

        [Fact]
        public void ExplicitDest_SuppressesConfigDestFolder()
        {
            // --dest и --dest-folder взаимоисключающи. Если человек задал --dest
            // руками, папка из файла должна молчать, а не приводить к отказу
            // «взаимоисключающи» на ровном месте.
            var cfg = new SettingsFile { RevitVersion = 2021, DestFolder = "RSN://s/ИзФайла" };

            var o = Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--dest", "RSN://s/Явная/M.rvt"
            }, cfg);

            Assert.Equal("RSN://s/Явная/M.rvt", o.Destination);
            Assert.Null(o.DestFolder);
        }

        [Fact]
        public void ExplicitSource_SuppressesConfigListFile()
        {
            // Тот же случай для --source против списка из файла.
            var cfg = new SettingsFile { RevitVersion = 2021, ListFile = @"C:\из-файла.txt" };

            var o = Options.Parse(new[]
            {
                "--source", @"C:\a.rvt", "--dest", "RSN://s/a.rvt"
            }, cfg);

            Assert.Equal(@"C:\a.rvt", o.Source);
            Assert.Null(o.ListFile);
        }

        [Fact]
        public void Config_BadUpgradePolicy_Throws()
            => Assert.Throws<ArgumentException>(() => Options.Parse(
                Source().Concat(new[] { "--revit-version", "2021" }).ToArray(),
                new SettingsFile { OnUpgrade = "может-быть" }));

        [Fact]
        public void Config_IsValidatedLikeCommandLine()
        {
            // Кривое значение из файла должно ловиться той же проверкой,
            // а не проходить мимо неё.
            Assert.Throws<ArgumentException>(() => Options.Parse(
                Source().Concat(new[] { "--revit-version", "2021" }).ToArray(),
                new SettingsFile { Retries = 100 }));
        }

        [Fact]
        public void RoundTrips_ThroughJson()
        {
            var cfg = new SettingsFile
            {
                Комментарий = "Пайплайн ОВ, обновлено 14.08.2026",
                RevitVersion = 2021,
                DestFolder = "RSN://RVTSRV-TEST/Проекты/2026",
                Retries = 2,
                OnUpgrade = "upgrade",
                CloseWorksets = true,
            };

            var back = Json.Deserialize<SettingsFile>(Json.Serialize(cfg));

            Assert.Equal(cfg.Комментарий, back.Комментарий);
            Assert.Equal(2021, back.RevitVersion);
            Assert.Equal("RSN://RVTSRV-TEST/Проекты/2026", back.DestFolder);
            Assert.Equal(2, back.Retries);
            Assert.True(back.CloseWorksets);
            Assert.Null(back.Compact);   // не задано — осталось null
        }

        [Theory]
        [InlineData(new[] { "--config", @"C:\s.json" }, @"C:\s.json")]
        [InlineData(new[] { "--source", "a.rvt", "--config", "s.json" }, "s.json")]
        [InlineData(new[] { "--source", "a.rvt" }, null)]
        [InlineData(new[] { "--config" }, null)]   // нет значения — не падаем на предпросмотре
        public void PeekConfigPath(string[] args, string expected)
            => Assert.Equal(expected, Options.PeekConfigPath(args));
    }

    public class RevitPathTests
    {
        [Theory]
        [InlineData(@"C:\Program Files\Autodesk\Revit 2025\Revit.exe", 2025)]
        [InlineData(@"C:\Program Files\Autodesk\Revit 2021\Revit.exe", 2021)]
        [InlineData(@"D:\Revit2024\Revit.exe", 2024)]
        [InlineData(@"D:\Revit_2023\Revit.exe", 2023)]
        [InlineData(@"d:\autodesk\revit 2022\revit.exe", 2022)]
        // Ближайший к exe год важнее: папка-предок может называться по-другому.
        [InlineData(@"D:\Revit 2021\backup\Revit 2025\Revit.exe", 2025)]
        [InlineData(@"C:\tools\rvt.exe", 0)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void VersionFromExePath(string path, int expected)
            => Assert.Equal(expected, RevitPath.VersionFromExePath(path));
    }

    /// <summary>
    /// Контракт между CLI и аддином: CLI пишет batch.json, аддин его читает,
    /// аддин пишет result.json, CLI его читает. Стороны компилируются в разные
    /// сборки под разные TFM, поэтому молчаливое расхождение здесь выглядит как
    /// «Revit запустился и ничего не сделал» — самый дорогой в отладке отказ.
    /// </summary>
    public class JsonContractTests
    {
        [Fact]
        public void UploadBatch_RoundTrips()
        {
            var batch = new UploadBatch
            {
                SessionId = "20260813_235154_ec1562",
                LogFile = @"C:\Temp\RvsUpload\сессия\addin.log",
                ResultFile = @"C:\Temp\RvsUpload\сессия\result.json",
            };
            batch.Tasks.Add(new UploadTask
            {
                SourceFile = @"C:\Выгрузка\АР\Модель.rvt",
                DestinationPath = "RSN://RVTSRV01/Проекты/2026/Модель.rvt",
                Overwrite = true,
                Audit = true,
                MaximumBackups = 3,
                UnloadLinks = true,
                Compact = true,
                EnableWorksharingIfNeeded = false,
                CloseUserWorksets = true,
            });

            var back = Json.Deserialize<UploadBatch>(Json.Serialize(batch));

            Assert.Equal(batch.SessionId, back.SessionId);
            Assert.Equal(batch.LogFile, back.LogFile);
            Assert.Single(back.Tasks);

            var t = back.Tasks[0];
            var orig = batch.Tasks[0];
            Assert.Equal(orig.SourceFile, t.SourceFile);
            Assert.Equal(orig.DestinationPath, t.DestinationPath);
            Assert.True(t.Overwrite);
            Assert.True(t.Audit);
            Assert.Equal(3, t.MaximumBackups);
            Assert.True(t.UnloadLinks);
            Assert.True(t.Compact);
            Assert.False(t.EnableWorksharingIfNeeded);
            Assert.True(t.CloseUserWorksets);
        }

        [Fact]
        public void UploadBatchResult_RoundTrips()
        {
            var result = new UploadBatchResult
            {
                SessionId = "s1",
                FinishedUtc = new DateTime(2026, 8, 13, 20, 30, 0, DateTimeKind.Utc)
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            result.Results.Add(new UploadResult
            {
                SourceFile = @"C:\a\Модель.rvt",
                DestinationPath = "RSN://srv/F/Модель.rvt",
                Success = false,
                Error = "InvalidOperationException: версия не совпадает",
                ElapsedSeconds = 12.5,
            });

            var back = Json.Deserialize<UploadBatchResult>(Json.Serialize(result));

            Assert.Equal(result.FinishedUtc, back.FinishedUtc);
            Assert.Single(back.Results);
            Assert.False(back.Results[0].Success);
            Assert.Equal(12.5, back.Results[0].ElapsedSeconds);
            Assert.Equal(result.Results[0].Error, back.Results[0].Error);
        }

        [Fact]
        public void ElapsedSeconds_IsCultureInvariant()
        {
            // На русской локали разделитель дробной части — запятая. Если она
            // попадёт в JSON, разбор на другой стороне упадёт или потеряет точность.
            var json = Json.Serialize(new UploadResult { ElapsedSeconds = 12.5 });
            Assert.Contains("12.5", json);
            Assert.DoesNotContain("12,5", json);
        }

        [Fact]
        public void EnableWorksharingIfNeeded_SurvivesAsFalse()
        {
            // Поле по умолчанию true. Сериализатор обязан записать явное false,
            // иначе аддин прочитает значение по умолчанию и включит worksharing
            // вопреки --no-enable-worksharing.
            var json = Json.Serialize(new UploadTask { EnableWorksharingIfNeeded = false });
            Assert.False(Json.Deserialize<UploadTask>(json).EnableWorksharingIfNeeded);
        }

        [Fact]
        public void MissingMembers_KeepInitializerDefaults()
        {
            // DataContractJsonSerializer вызывает конструктор только для POCO без
            // атрибутов контракта. Стоит навесить на UploadTask [DataContract] или
            // [Serializable] — и объект начнёт создаваться в обход конструктора:
            // EnableWorksharingIfNeeded молча станет false (worksharing перестанет
            // включаться), а Tasks/Results — null (NullReferenceException в аддине).
            // Тест удерживает это свойство.
            var task = Json.Deserialize<UploadTask>("{\"SourceFile\":\"a.rvt\"}");
            Assert.True(task.EnableWorksharingIfNeeded);
            Assert.Equal("Shared Levels and Grids", task.LevelsGridsWorksetName);
            Assert.Equal("Workset1", task.DefaultWorksetName);

            Assert.NotNull(Json.Deserialize<UploadBatch>("{\"SessionId\":\"s\"}").Tasks);
            Assert.NotNull(Json.Deserialize<UploadBatchResult>("{\"SessionId\":\"s\"}").Results);
        }

        [Fact]
        public void Core_HasNoThirdPartyDependencies()
        {
            // Аддин грузится в процесс Revit, у которого своя Newtonsoft.Json.dll.
            // Вторая копия другой версии = TypeLoadException при загрузке аддина.
            var referenced = typeof(Json).Assembly.GetReferencedAssemblies();
            Assert.DoesNotContain(referenced, a => a.Name.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Самый важный тест во всём наборе. Если GUID в манифесте разойдётся
    /// с GUID в AddinContract, Revit тихо запустится, ничего не выполнит
    /// и закроется — без единой ошибки. Отлаживать это вручную мучительно.
    /// </summary>
    public class AddinManifestContractTests
    {
        [Fact]
        public void AddInId_MatchesManifest()
        {
            var manifest = XDocument.Load(FindRepoFile(
                Path.Combine("src", "RvsUpload.Addin", "RvsUpload.addin")));

            var addin = manifest.Root.Element("AddIn");
            var addInId = addin.Element("AddInId").Value.Trim();
            var clientId = addin.Element("ClientId").Value.Trim();
            var fullClass = addin.Element("FullClassName").Value.Trim();

            Assert.Equal(AddinContract.AddInId, addInId, ignoreCase: true);
            Assert.Equal(AddinContract.AddInId, clientId, ignoreCase: true);
            Assert.Equal(AddinContract.ApplicationClass, fullClass);
        }

        [Fact]
        public void Manifest_IsApplicationNotCommand()
        {
            // Type="Command" вызывается через ленту, а вызов по AddInId из journal
            // на живом Revit 2021 молча не срабатывает.
            // Внешнее приложение Revit запускает сам, лента не нужна.
            var manifest = XDocument.Load(FindRepoFile(
                Path.Combine("src", "RvsUpload.Addin", "RvsUpload.addin")));

            Assert.Equal("Application", manifest.Root.Element("AddIn").Attribute("Type").Value);
        }

        [Fact]
        public void AddInId_IsValidGuid()
            => Assert.True(Guid.TryParse(AddinContract.AddInId, out _));

        [Fact]
        public void AssemblyPath_DoesNotCollideWithManifestFileName()
        {
            // Раньше <Assembly> указывал в подпапку RvsUpload.Addin, а манифест
            // называется RvsUpload.addin — в Windows это одно и то же имя.
            // Установщик падал с "target file is a directory, not a file".
            var manifestPath = FindRepoFile(Path.Combine("src", "RvsUpload.Addin", "RvsUpload.addin"));
            var assembly = XDocument.Load(manifestPath)
                .Root.Element("AddIn").Element("Assembly").Value.Trim();

            var manifestName = Path.GetFileName(manifestPath);
            var firstSegment = assembly.Split('\\', '/')[0];

            Assert.False(
                assembly.Contains("\\") || assembly.Contains("/"),
                $"<Assembly> должен быть именем файла без подпапки, сейчас '{assembly}'");
            Assert.False(
                string.Equals(manifestName, firstSegment, StringComparison.OrdinalIgnoreCase),
                $"'{firstSegment}' совпадает с именем манифеста '{manifestName}' без учёта регистра");
        }

        [Fact]
        public void VisibilityMode_IsAbsent()
        {
            // Команда всегда вызывается без открытого документа: исходники
            // открываются на DB-уровне и в UI не попадают. VisibilityMode
            // NotVisibleWhenNoActiveDocument сделал бы её недоступной, и Revit
            // молча стартовал бы, ничего не делал и закрывался.
            var manifest = XDocument.Load(FindRepoFile(
                Path.Combine("src", "RvsUpload.Addin", "RvsUpload.addin")));

            Assert.Null(manifest.Root.Element("AddIn").Element("VisibilityMode"));
        }

        private static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException($"Не найден {relative} при обходе вверх от {AppContext.BaseDirectory}");
        }
    }
}
