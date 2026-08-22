using System;
using System.IO;
using System.Text;

namespace RvsUpload
{
    /// <summary>
    /// Дочитывает лог аддина по мере его написания и отдаёт новые строки наружу.
    ///
    /// Зачем: заливка пакета идёт десятками минут, и всё это время аддин
    /// подробно пишет, что делает, — какую модель открыл, что залил, где
    /// ошибся. Раньше этот лог выводился одним куском ПОСЛЕ закрытия Revit,
    /// и до тех пор в консоли не было ничего, кроме «Аддин загрузился».
    /// Человек не мог отличить работу от зависания, а результат заливки
    /// узнавал только в конце.
    ///
    /// Файл открывается с FileShare.ReadWrite: аддин держит его открытым
    /// на запись, и без этого чтение падало бы.
    ///
    /// Читается только то, что дописано до последнего перевода строки.
    /// Незавершённый хвост остаётся на следующий раз: аддин пишет строку
    /// целиком, но момент чтения может попасть в середину записи.
    /// </summary>
    public sealed class AddinLogTail
    {
        private readonly string _path;
        private long _offset;

        public AddinLogTail(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Отдаёт наружу строки, появившиеся с прошлого вызова.
        /// Ошибки чтения проглатываются намеренно: показ хода работы не повод
        /// ронять заливку, а следующая попытка через пару секунд.
        /// </summary>
        public void Pump(Action<string> log)
        {
            if (log == null || string.IsNullOrEmpty(_path)) return;

            try
            {
                if (!File.Exists(_path)) return;

                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Файл пересоздали (повторный запуск в ту же папку) —
                    // читаем с начала, иначе молчали бы до конца сессии.
                    if (fs.Length < _offset) _offset = 0;
                    if (fs.Length == _offset) return;

                    fs.Seek(_offset, SeekOrigin.Begin);

                    var buffer = new byte[fs.Length - _offset];
                    var read = fs.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;

                    var text = Encoding.UTF8.GetString(buffer, 0, read);

                    var cut = text.LastIndexOf('\n');
                    if (cut < 0) return;

                    var complete = text.Substring(0, cut + 1);
                    _offset += Encoding.UTF8.GetByteCount(complete);

                    foreach (var line in complete.Split('\n'))
                    {
                        var clean = line.Trim();
                        if (clean.Length == 0) continue;
                        log(Format(clean));
                    }
                }
            }
            catch
            {
                // Не страшно: строки догонят при следующем вызове или в конце.
            }
        }

        /// <summary>
        /// Убирает у строки собственную отметку времени аддина: свою отметку
        /// добавит журнал CLI, а две подряд только мешают читать. Расхождение
        /// между ними не больше периода опроса.
        /// </summary>
        private static string Format(string line)
        {
            const string prefix = "аддин: ";

            // Формат отметки — "[ЧЧ:ММ:СС] ", ровно 11 символов.
            if (line.Length > 11 && line[0] == '[' && line[3] == ':' && line[6] == ':' && line[9] == ']')
                return prefix + line.Substring(11).Trim();

            return prefix + line;
        }
    }
}
