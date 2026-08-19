using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RvsUpload.Tests
{
    /// <summary>
    /// Поиск моделей в папке.
    ///
    /// Проверяем не столько «нашлись .rvt», сколько то, что НЕ должно попасть
    /// в пакет: резервные копии Revit и служебные папки лежат вперемешку
    /// с рабочими моделями и выглядят как обычные .rvt. Залить их на сервер —
    /// значит засорить его мусором под видом моделей.
    /// </summary>
    public class ModelScannerTests : IDisposable
    {
        private readonly string _root;

        public ModelScannerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "rvs_scan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* временная папка */ }
        }

        private void Make(string relative)
        {
            var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, "x");
        }

        [Fact]
        public void Scan_FindsModelsRecursively()
        {
            Make("АР.rvt");
            Make("Разделы/ОВ.rvt");
            Make("Разделы/Вложенно/ВК.rvt");

            var r = ModelScanner.Scan(_root, recurse: true);

            Assert.Equal(3, r.Models.Count);
            Assert.Contains(r.Models, m => m.FileName == "АР.rvt" && m.RelativeFolder == "");
            Assert.Contains(r.Models, m => m.FileName == "ОВ.rvt" && m.RelativeFolder == "Разделы");
            Assert.Contains(r.Models, m => m.FileName == "ВК.rvt" && m.RelativeFolder == "Разделы/Вложенно");
        }

        [Fact]
        public void Scan_NoRecurse_TakesOnlyTopLevel()
        {
            Make("АР.rvt");
            Make("Разделы/ОВ.rvt");

            var r = ModelScanner.Scan(_root, recurse: false);

            Assert.Single(r.Models);
            Assert.Equal("АР.rvt", r.Models[0].FileName);
        }

        [Fact]
        public void Scan_SkipsRevitBackupFiles()
        {
            Make("АР.rvt");
            Make("АР.0001.rvt");
            Make("АР.0023.rvt");

            var r = ModelScanner.Scan(_root, recurse: true);

            Assert.Single(r.Models);
            Assert.Equal("АР.rvt", r.Models[0].FileName);
            Assert.Equal(2, r.SkippedItems.Count);
        }

        [Fact]
        public void Scan_KeepsNamesThatOnlyLookLikeBackups()
        {
            // Цифры в имени — ещё не резервная копия. Под правило попадает
            // только шаблон «Имя.NNNN.rvt», который делает сам Revit;
            // «Корпус_2026.rvt» и «АР.001.rvt» под него не подходят.
            Make("Корпус_2026.rvt");
            Make("АР.001.rvt");

            var r = ModelScanner.Scan(_root, recurse: true);

            Assert.Equal(2, r.Models.Count);
            Assert.Empty(r.SkippedItems);
        }

        [Fact]
        public void Scan_SkipsServiceFolders()
        {
            Make("АР.rvt");
            Make("АР_backup/АР.rvt");
            Make("Revit_temp/Что-то.rvt");

            var r = ModelScanner.Scan(_root, recurse: true);

            Assert.Single(r.Models);
            Assert.Equal(2, r.SkippedItems.Count);
        }

        [Fact]
        public void Scan_ReportsReasonForEachSkip()
        {
            Make("АР.0001.rvt");
            Make("АР_backup/АР.rvt");

            var r = ModelScanner.Scan(_root, recurse: true);

            Assert.All(r.SkippedItems, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        }

        [Fact]
        public void Scan_EmptyFolder_ReturnsNothing()
        {
            var r = ModelScanner.Scan(_root, recurse: true);
            Assert.Empty(r.Models);
        }

        [Fact]
        public void Scan_MissingFolder_Throws()
            => Assert.Throws<DirectoryNotFoundException>(
                () => ModelScanner.Scan(Path.Combine(_root, "нет-такой"), true));

        [Fact]
        public void Scan_OrderIsStable()
        {
            // От порядка зависит вид лога и отчёта; обход файловой системы
            // порядка не гарантирует.
            Make("Я.rvt");
            Make("А.rvt");
            Make("Разделы/Б.rvt");

            var first = ModelScanner.Scan(_root, true).Models.Select(m => m.FileName).ToArray();
            var second = ModelScanner.Scan(_root, true).Models.Select(m => m.FileName).ToArray();

            Assert.Equal(first, second);
        }

        [Fact]
        public void RelativeFolderOf_UsesForwardSlashes()
        {
            var dir = Path.Combine(_root, "Разделы", "Вложенно");
            Directory.CreateDirectory(dir);

            Assert.Equal("Разделы/Вложенно", ModelScanner.RelativeFolderOf(dir, _root));
            Assert.Equal("", ModelScanner.RelativeFolderOf(_root, _root));
        }

        [Theory]
        [InlineData("flat", FolderStructure.Flat)]
        [InlineData("DISK", FolderStructure.Disk)]
        [InlineData(" server ", FolderStructure.Server)]
        public void ParseStructure_Valid(string input, FolderStructure expected)
            => Assert.Equal(expected, ModelScanner.ParseStructure(input));

        [Theory]
        [InlineData("")]
        [InlineData("mirror")]
        [InlineData(null)]
        public void ParseStructure_Invalid_Throws(string input)
            => Assert.Throws<ArgumentException>(() => ModelScanner.ParseStructure(input));
    }

    /// <summary>
    /// Ключи режима «папка целиком». Список и папка существуют одновременно
    /// как две возможности, но в одном запуске источник должен быть один —
    /// иначе непонятно, что заливать.
    /// </summary>
    public class SourceFolderOptionsTests
    {
        private static string[] With(params string[] extra)
            => new[] { "--revit-version", "2024" }.Concat(extra).ToArray();

        [Fact]
        public void SourceFolder_RequiresDestFolder()
            => Assert.Throws<ArgumentException>(
                () => Options.Parse(With("--source-folder", @"C:\out")));

        [Fact]
        public void SourceFolder_WithDestFolder_IsOk()
        {
            var o = Options.Parse(With("--source-folder", @"C:\out", "--dest-folder", "RSN://S/P"));

            Assert.Equal(@"C:\out", o.SourceFolder);
            Assert.Equal(FolderStructure.Flat, o.Structure);
            Assert.True(o.Recurse);
        }

        [Fact]
        public void List_StillWorks_AlongsideNewMode()
        {
            // Список никуда не делся: это вторая равноправная возможность.
            var o = Options.Parse(With("--list", "l.txt", "--dest-folder", "RSN://S/P"));

            Assert.Equal("l.txt", o.ListFile);
            Assert.Null(o.SourceFolder);
        }

        [Fact]
        public void SourceFolder_AndList_Throw()
            => Assert.Throws<ArgumentException>(
                () => Options.Parse(With("--source-folder", @"C:\out", "--list", "l.txt",
                                         "--dest-folder", "RSN://S/P")));

        [Fact]
        public void SourceFolder_AndSource_Throw()
            => Assert.Throws<ArgumentException>(
                () => Options.Parse(With("--source-folder", @"C:\out", "--source", "a.rvt",
                                         "--dest-folder", "RSN://S/P")));

        [Fact]
        public void Structure_WithoutSourceFolder_Throws()
            => Assert.Throws<ArgumentException>(
                () => Options.Parse(With("--list", "l.txt", "--dest-folder", "RSN://S/P",
                                         "--structure", "disk")));

        [Fact]
        public void NoRecurse_WithoutSourceFolder_Throws()
            => Assert.Throws<ArgumentException>(
                () => Options.Parse(With("--list", "l.txt", "--dest-folder", "RSN://S/P",
                                         "--no-recurse")));

        [Fact]
        public void DestName_WithSourceFolder_Throws()
            => Assert.Throws<ArgumentException>(
                () => Options.Parse(With("--source-folder", @"C:\out", "--dest-folder", "RSN://S/P",
                                         "--dest-name", "X.rvt")));

        [Fact]
        public void StructureAndNoRecurse_AreParsed()
        {
            var o = Options.Parse(With("--source-folder", @"C:\out", "--dest-folder", "RSN://S/P",
                                       "--structure", "server", "--no-recurse"));

            Assert.Equal(FolderStructure.Server, o.Structure);
            Assert.False(o.Recurse);
        }
    }

    /// <summary>Разбор /contents — на нём держится раскладка «как на сервере».</summary>
    public class ContentsParsingTests
    {
        [Fact]
        public void ParseContents_ReadsFoldersAndModels()
        {
            var json = @"{""Path"":""|"",
                          ""Folders"":[{""Name"":""АР""},{""Name"":""КР""}],
                          ""Models"":[{""Name"":""Общая.rvt""}]}";

            RevitServerRestClient.ParseContents(json, out var folders, out var models);

            Assert.Equal(new[] { "АР", "КР" }, folders);
            Assert.Equal(new[] { "Общая.rvt" }, models);
        }

        [Fact]
        public void ParseContents_MissingLists_AreEmptyNotNull()
        {
            RevitServerRestClient.ParseContents("{}", out var folders, out var models);

            Assert.Empty(folders);
            Assert.Empty(models);
        }
    }
}
