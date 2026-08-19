using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RvsUpload.Addin
{
    /// <summary>
    /// Главная причина, по которой "headless" Revit зависает в пайплайне —
    /// модальный диалог, который некому нажать. Гасим два источника:
    ///
    ///  - DialogBoxShowing: обычные messagebox'ы и task dialog'и.
    ///  - FailuresProcessing: предупреждения и ошибки движка Revit.
    ///
    /// Стратегия намеренно консервативная: предупреждения глушим,
    /// ошибки пытаемся разрешить штатно, а если не выходит — откатываем
    /// транзакцию, а не давим "продолжить любой ценой". Молча проглотить
    /// ошибку при заливке центральной модели куда хуже, чем упасть.
    /// </summary>
    internal class DialogSuppressor : IDisposable
    {
        private readonly UIControlledApplication _uiapp;
        private readonly ControlledApplication _app;
        private readonly SimpleLog _log;

        /// <summary>
        /// Подписка идёт на UIControlledApplication, который доступен в
        /// IExternalApplication.OnStartup. Это сознательный выбор в пользу
        /// Type="Application" против Type="DBApplication": у DB-варианта есть
        /// FailuresProcessing, но НЕТ DialogBoxShowing, то есть модальные окна
        /// перехватывать было бы нечем — а именно они и вешают headless-сессию.
        /// </summary>
        /// <summary>
        /// Пользователю показывали вопрос об остановке операции. Сбрасывается
        /// перед каждой моделью — см. ResetPerTaskState.
        /// </summary>
        public bool StopWasOfferedToUser { get; private set; }

        /// <summary>Сбрасывает состояние, накопленное на предыдущей модели.</summary>
        public void ResetPerTaskState() => StopWasOfferedToUser = false;

        public DialogSuppressor(UIControlledApplication uiapp, SimpleLog log)
        {
            _uiapp = uiapp;
            _app = uiapp.ControlledApplication;
            _log = log;

            _uiapp.DialogBoxShowing += OnDialogBoxShowing;
            _app.FailuresProcessing += OnFailuresProcessing;
        }

        /// <summary>
        /// Явные ответы на диалоги, которые реально встретились на боевых моделях.
        ///
        /// Зачем список вместо «всегда Cancel». Универсальный Cancel — угадывание:
        /// у диалога может не быть такой кнопки вовсе. Проверено на живом Revit 2021,
        /// журнал сессии:
        ///
        ///     TaskDialog "Остановить выполнение данной операции?"
        ///     Id : TaskDialog_Stop_Operation
        ///     CommonButtons : Yes, No
        ///     DefaultButton : Yes
        ///     TaskDialog API event result : 2
        ///     DBG_WARN: The TaskDilaog doesn't have a CommonButton with Id Cancel
        ///
        /// То есть ответ Cancel был НЕДОПУСТИМ, Revit его отверг и выкрутился сам.
        /// Обновление модели продолжилось по счастливой случайности, притом что
        /// кнопка по умолчанию у этого диалога — «Да», то есть ОСТАНОВИТЬ.
        /// </summary>
        private static readonly Dictionary<string, TaskDialogResult> KnownAnswers =
            new Dictionary<string, TaskDialogResult>(StringComparer.OrdinalIgnoreCase)
            {
                // «Файл изменён сторонними средствами обновления …, которые сейчас
                // не установлены». CommandLink1 = «Продолжить работу с файлом»
                // (сверено с записью ответа в журналах Revit пользователя).
                { "TaskDialog_Missing_Third_Party_Updaters", TaskDialogResult.CommandLink1 },

                // «<файл> обновлен до новой версии Revit. …изменения из существующих
                // локальных файлов будут утеряны…». Единственная кнопка — Close,
                // из журнала Revit:
                //     CommonButtons : Close
                //     DefaultButton : Close
                //     Jrn.Data "TaskDialogResult" , "…", "Close", "IDCLOSE"
                //
                // При открытии на DB-уровне (OpenDocumentFile) оно не появляется —
                // его видно только при открытии через интерфейс. Ответ задан
                // на случай, если всплывёт: Cancel здесь недопустим, такой кнопки нет.
                //
                // Идентификатор выведен из строки журнала
                // [Jrn.AutoConvertedMessageBox] Rvt.Attr.MessageId: IDS_STRING_AS_IS
                // и на живом перехвате не подтверждён — если увидите его в логе
                // как «НЕИЗВЕСТНЫЙ», подставьте фактическое значение DialogId.
                { "IDS_STRING_AS_IS", TaskDialogResult.Close },
            };

        /// <summary>
        /// Диалоги, которые мы НЕ перехватываем — их видит и решает человек.
        ///
        /// «Остановить выполнение данной операции?» появляется только в ответ
        /// на нажатие «Отменить обновление» в окне прогресса обновления модели.
        /// Сам по себе он не всплывает, поэтому в автоматическом запуске, где
        /// у экрана никого нет, появиться не может — а значит, и повесить сессию.
        ///
        /// Если человек нажал осознанно, у него должно быть право отменить:
        /// открытие модели прервётся, задание будет помечено как отменённое,
        /// и пакет продолжится со следующей модели.
        /// </summary>
        private static readonly HashSet<string> PassToUser =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "TaskDialog_Stop_Operation",
            };

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            var id = e.DialogId ?? "(без id)";

            // Отдаём человеку — не трогаем результат вовсе.
            if (PassToUser.Contains(id))
            {
                _log.Write($"Диалог НЕ перехвачен, решает пользователь: {id}");

                // Запоминаем факт. Если операция после этого свалится, Revit
                // отдаст безликий InternalException («A managed exception was
                // thrown by Revit…») — по нему причину не понять. Зато мы точно
                // знаем, что человека спрашивали про остановку, и можем написать
                // в отчёт вменяемую причину вместо этого текста.
                if (id.Equals("TaskDialog_Stop_Operation", StringComparison.OrdinalIgnoreCase))
                    StopWasOfferedToUser = true;

                return;
            }

            if (e is TaskDialogShowingEventArgs td)
            {
                if (KnownAnswers.TryGetValue(id, out var answer))
                {
                    _log.Write($"Диалог перехвачен: {id} -> отвечаю {answer}");
                    _log.Write($"  текст: {Shorten(td.Message)}");
                    td.OverrideResult((int)answer);
                    return;
                }

                // Неизвестный диалог. Cancel — наименее разрушительный ответ
                // (не сохранять, не чинить, не продолжать вслепую), но он может
                // оказаться недопустимым для конкретного окна. Поэтому пишем
                // в лог явно: этот идентификатор нужно разобрать и добавить выше.
                _log.Write($"Диалог перехвачен: {id} -> НЕИЗВЕСТНЫЙ, отвечаю Cancel вслепую");
                _log.Write($"  текст: {Shorten(td.Message)}");
                _log.Write("  Добавьте этот DialogId в KnownAnswers с осознанным ответом.");
                td.OverrideResult((int)TaskDialogResult.Cancel);
                return;
            }

            // Обычные messagebox'ы: 2 == IDCANCEL.
            _log.Write($"Диалог перехвачен: {id} (messagebox) -> IDCANCEL");
            e.OverrideResult(2);
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            var fa = e.GetFailuresAccessor();
            var failures = fa.GetFailureMessages();
            if (!failures.Any()) return;

            var deleted = false;
            var hasErrors = false;

            foreach (var f in failures)
            {
                var severity = f.GetSeverity();
                var description = f.GetDescriptionText();

                if (severity == FailureSeverity.Warning)
                {
                    _log.Write($"Предупреждение подавлено: {Shorten(description)}");
                    fa.DeleteWarning(f);
                    deleted = true;
                }
                else
                {
                    hasErrors = true;
                    _log.Write($"ОШИБКА Revit ({severity}): {Shorten(description)}");

                    // Метода GetApplicableResolutionTypes() в API нет. Доступный
                    // способ (сверено с RevitAPI.dll 2021 и 2025): спросить у самого
                    // сообщения, есть ли у него разрешения, и убедиться, что движок
                    // разрешает их применить в текущем контексте — вне транзакции
                    // ResolveFailure бросит исключение.
                    if (f.HasResolutions() && fa.IsFailureResolutionPermitted(f))
                    {
                        fa.ResolveFailure(f);
                        _log.Write($"  применено разрешение: {f.GetCurrentResolutionType()}");
                        deleted = true;
                    }
                    else
                    {
                        _log.Write("  разрешения недоступны — ошибка останется неразрешённой");
                    }
                }
            }

            if (hasErrors && !deleted)
            {
                _log.Write("Неразрешимая ошибка — откат транзакции.");
                e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
                return;
            }

            if (deleted)
                e.SetProcessingResult(FailureProcessingResult.ProceedWithCommit);
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
        }

        public void Dispose()
        {
            _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
            _app.FailuresProcessing -= OnFailuresProcessing;
        }
    }
}
