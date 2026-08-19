namespace RvsUpload
{
    /// <summary>
    /// Контракт между CLI и аддином.
    ///
    /// Journal-файла в схеме больше нет. Почему — проверено на живом Revit 2021:
    ///
    ///  1. Revit, запущенный С journal-файлом, грузит только «внутренние» аддины
    ///     из своей папки установки: 31 приложение вместо 82. Аддины из
    ///     %ProgramData% и %APPDATA% не загружаются вовсе, и наш в том числе.
    ///
    ///  2. Положить аддин в папку установки нельзя: Revit считает такие аддины
    ///     внутренними и требует подписи Autodesk, о чём пишет прямо:
    ///         DBG_WARN: The assembly -RvsUpload.addin- in internal addin
    ///         ...\AddIns\RvsUpload\RvsUpload.Addin.dll is not signed as internal addin.
    ///
    /// Поэтому Revit запускается БЕЗ аргументов, как обычная сессия — тогда
    /// аддин из %ProgramData% загружается штатно. Задание передаётся переменной
    /// окружения, а закрывает Revit сам аддин, закончив работу.
    ///
    /// Подробности — CODE_REVIEW.md, B-21.
    /// </summary>
    public static class AddinContract
    {
        /// <summary>GUID должен совпадать с AddInId и ClientId в RvsUpload.addin.</summary>
        public const string AddInId = "6E4A8F31-2C7D-4B9E-8A15-3F0D9C2E7B44";

        /// <summary>Класс точки входа аддина — IExternalApplication.</summary>
        public const string ApplicationClass = "RvsUpload.Addin.UploadApplication";

        /// <summary>
        /// Переменная окружения с путём к batch-файлу задания. Задаётся только
        /// процессу Revit, который мы запускаем: пользовательские сессии её
        /// не видят, и аддин в них не делает ничего.
        /// </summary>
        public const string BatchEnvVar = "RVSUPLOAD_BATCH";
    }
}
