using System.Collections.Generic;

namespace RvsUpload
{
    /// <summary>
    /// Контракт между CLI и аддином. Сериализуется в JSON, путь к файлу
    /// передаётся в Revit через Jrn.Data "APIStringStringMapJournalData".
    /// ВАЖНО: этот файл линкуется и в проект аддина (см. RvsUpload.Addin.csproj),
    /// чтобы обе стороны использовали один и тот же тип.
    /// </summary>
    public class UploadTask
    {
        /// <summary>Локальный .rvt, который нужно залить.</summary>
        public string SourceFile { get; set; }

        /// <summary>Полный путь назначения вида RSN://SERVER/Folder/Sub/Model.rvt</summary>
        public string DestinationPath { get; set; }

        /// <summary>Перезаписывать существующую центральную модель.</summary>
        public bool Overwrite { get; set; }

        /// <summary>Audit при открытии исходника (медленно, но чинит мелкую коррупцию).</summary>
        public bool Audit { get; set; }

        /// <summary>Число бэкапов для SaveAsOptions. 0 = не задавать.</summary>
        public int MaximumBackups { get; set; }

        /// <summary>
        /// Если исходник не workshared — включить worksharing перед заливкой.
        /// Иначе SaveAsCentral упадёт.
        /// </summary>
        public bool EnableWorksharingIfNeeded { get; set; } = true;

        public string LevelsGridsWorksetName { get; set; } = "Shared Levels and Grids";
        public string DefaultWorksetName { get; set; } = "Workset1";

        /// <summary>
        /// Закрывать пользовательские рабочие наборы при открытии исходника.
        ///
        /// Главное назначение — НЕ ОТКРЫВАТЬ связанные модели, размещённые в этих
        /// наборах. Отличие от UnloadLinks принципиальное: тот выгружает связи
        /// уже после открытия, а закрытый набор не даёт им подгрузиться вовсе.
        /// На моделях без связей выигрыша по времени практически нет.
        ///
        /// Закрытый набор не равен удалённому: он остаётся в файле целиком,
        /// закрытие управляет только загрузкой элементов в память. Проверено на
        /// живом Revit 2021 — та же модель, залитая с флагом и без, отличается
        /// на 0,14 % (сохранённое состояние «открыт/закрыт» самих наборов).
        ///
        /// Внимание: «Общие уровни и сетки» (Shared Levels and Grids) — тоже
        /// пользовательский набор, и он закрывается наравне с прочими.
        /// Системные (виды, семейства, стандарты проекта) не трогаются:
        /// WorksharingUtils.GetUserWorksetInfo их не возвращает.
        /// </summary>
        public bool CloseUserWorksets { get; set; }

        /// <summary>Отвязать все внешние ссылки перед заливкой (RVT links -> unload).</summary>
        public bool UnloadLinks { get; set; }

        /// <summary>Сжать модель при сохранении.</summary>
        public bool Compact { get; set; }
    }

    public class UploadBatch
    {
        public string SessionId { get; set; }
        public string LogFile { get; set; }
        public string ResultFile { get; set; }
        public List<UploadTask> Tasks { get; set; } = new List<UploadTask>();
    }

    public class UploadResult
    {
        public string SourceFile { get; set; }
        public string DestinationPath { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public double ElapsedSeconds { get; set; }
    }

    public class UploadBatchResult
    {
        public string SessionId { get; set; }

        /// <summary>
        /// Время завершения в ISO-8601 (UTC), строкой. Именно строкой, а не
        /// DateTime: DataContractJsonSerializer пишет DateTime как \/Date(1234)\/,
        /// а result.json разбирают в том числе глазами при разборе падений.
        /// </summary>
        public string FinishedUtc { get; set; }

        public List<UploadResult> Results { get; set; } = new List<UploadResult>();
    }
}
