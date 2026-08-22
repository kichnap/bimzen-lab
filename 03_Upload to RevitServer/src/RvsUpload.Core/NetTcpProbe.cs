using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace RvsUpload
{
    /// <summary>
    /// Проверка канала, по которому Revit на самом деле передаёт модели.
    ///
    /// Зачем отдельно от REST. У Revit Server два канала: администраторский
    /// REST по HTTP (порт 80) и передача моделей по net.tcp (порт 808).
    /// Предполёт спрашивал только первый и потому рапортовал «сервер
    /// доступен», когда заливка была невозможна: отказ выяснялся через
    /// пять-шесть минут после запуска Revit, открытия и обновления модели.
    ///
    /// Почему нельзя обойтись проверкой открытости порта 808. На 808 слушает
    /// служба разделения портов Windows, а нужная служба живёт за ней:
    /// клиент соединяется с портом и уже внутри соединения просит
    /// `ModelService&lt;год&gt;`. Порт отвечает и тогда, когда самой службы нет —
    /// проверено на несуществующем `ModelService9999`, TCP-соединение
    /// устанавливается штатно.
    ///
    /// Поэтому проверяем по-настоящему: разыгрываем начало сеанса .NET
    /// Message Framing (MC-NMF) и смотрим ответ.
    ///   0x0B  подтверждение — служба на месте;
    ///   0x08  отказ с текстом, например EndpointUnavailable — службы нет.
    ///
    /// ЧЕГО ЭТА ПРОВЕРКА НЕ ЛОВИТ. Она проверяет установление сеанса, а не
    /// передачу объёма. Отказ, ради которого её писали, был как раз при
    /// передаче: сеанс поднимался, а заливка умирала на десятках мегабайт.
    /// Пробник в тот момент отвечал «служба на месте» — и был прав.
    /// Не приписывайте ему большего, чем он делает.
    /// </summary>
    public static class NetTcpProbe
    {
        public const int DefaultPort = 808;

        public enum Outcome
        {
            /// <summary>Служба ответила подтверждением — канал рабочий.</summary>
            Ok,

            /// <summary>Соединение с портом не установилось.</summary>
            NoConnection,

            /// <summary>Порт ответил, служба отказала (обычно её нет).</summary>
            ServiceUnavailable,

            /// <summary>Порт принял соединение, но ответа не последовало.</summary>
            NoAnswer,
        }

        public class Result
        {
            public Outcome Outcome;
            public string Detail;
            public int ElapsedMs;

            public bool IsOk => Outcome == Outcome.Ok;
        }

        /// <summary>
        /// Проверка с повторами. Нужна не для красоты: на измеренной площадке
        /// около 8% попыток соединения обрываются сами по себе (22 успеха
        /// из 24). Однократная проверка отклоняла бы примерно каждый
        /// двенадцатый запуск, который на самом деле прошёл бы, — а это
        /// хуже, чем отсутствие проверки: заливка по расписанию начала бы
        /// падать без причины.
        ///
        /// Хватило одной удачной попытки: мы отвечаем на вопрос «канал
        /// в принципе жив», а не «насколько он надёжен».
        /// </summary>
        public static Result ProbeWithRetries(string host, int revitVersion,
                                              int attempts = 3, int timeoutMs = 8000, int port = DefaultPort)
        {
            Result last = null;
            for (var i = 0; i < Math.Max(1, attempts); i++)
            {
                last = Probe(host, revitVersion, timeoutMs, port);
                if (last.IsOk) return last;

                // Отсутствие службы повторять бессмысленно: ответ не изменится.
                if (last.Outcome == Outcome.ServiceUnavailable) return last;
            }
            return last;
        }

        /// <param name="host">Адрес сервера — тот же, что в RSN-пути.</param>
        /// <param name="revitVersion">Год: служба называется ModelService&lt;год&gt;.</param>
        public static Result Probe(string host, int revitVersion, int timeoutMs = 8000, int port = DefaultPort)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Не задан адрес сервера.", nameof(host));

            var via = string.Format("net.tcp://{0}/ModelService{1}/ModelService.svc/tcpbuffer", host, revitVersion);
            var started = DateTime.UtcNow;
            Func<int> elapsed = () => (int)(DateTime.UtcNow - started).TotalMilliseconds;

            using (var client = new TcpClient())
            {
                try
                {
                    var connect = client.BeginConnect(host, port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                        return new Result
                        {
                            Outcome = Outcome.NoConnection,
                            Detail = $"порт {port} не ответил за {timeoutMs} мс",
                            ElapsedMs = elapsed()
                        };
                    client.EndConnect(connect);
                }
                catch (Exception ex)
                {
                    return new Result
                    {
                        Outcome = Outcome.NoConnection,
                        Detail = ex.Message,
                        ElapsedMs = elapsed()
                    };
                }

                try
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;

                    var preamble = BuildPreamble(via);
                    stream.Write(preamble, 0, preamble.Length);
                    stream.Flush();

                    var buffer = new byte[512];
                    var read = stream.Read(buffer, 0, buffer.Length);

                    if (read <= 0)
                        return new Result
                        {
                            Outcome = Outcome.NoAnswer,
                            Detail = $"служба ModelService{revitVersion} не ответила на приветствие",
                            ElapsedMs = elapsed()
                        };

                    if (buffer[0] == 0x0B)
                        return new Result { Outcome = Outcome.Ok, ElapsedMs = elapsed() };

                    if (buffer[0] == 0x08)
                        return new Result
                        {
                            Outcome = Outcome.ServiceUnavailable,
                            Detail = ReadFaultText(buffer, read),
                            ElapsedMs = elapsed()
                        };

                    return new Result
                    {
                        Outcome = Outcome.NoAnswer,
                        Detail = $"неожиданный ответ 0x{buffer[0]:X2}",
                        ElapsedMs = elapsed()
                    };
                }
                catch (Exception ex)
                {
                    return new Result
                    {
                        Outcome = Outcome.NoAnswer,
                        Detail = ex.Message,
                        ElapsedMs = elapsed()
                    };
                }
            }
        }

        /// <summary>
        /// Начало сеанса MC-NMF: версия, режим, адрес службы, кодировка, конец
        /// приветствия. Длина строки — 7-битное кодирование, как в протоколе.
        /// </summary>
        internal static byte[] BuildPreamble(string via)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x00); ms.WriteByte(0x01); ms.WriteByte(0x00);  // версия 1.0
                ms.WriteByte(0x01); ms.WriteByte(0x02);                      // режим: duplex

                ms.WriteByte(0x02);                                          // адрес службы
                var uri = Encoding.UTF8.GetBytes(via);
                WriteVarInt(ms, uri.Length);
                ms.Write(uri, 0, uri.Length);

                ms.WriteByte(0x03); ms.WriteByte(0x08);                      // кодировка: двоичная
                ms.WriteByte(0x0C);                                          // конец приветствия
                return ms.ToArray();
            }
        }

        internal static void WriteVarInt(Stream s, int value)
        {
            while (value >= 0x80)
            {
                s.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            s.WriteByte((byte)value);
        }

        /// <summary>Текст отказа: 0x08, длина 7-битным кодированием, строка UTF-8.</summary>
        internal static string ReadFaultText(byte[] buffer, int count)
        {
            var i = 1;
            var length = 0;
            var shift = 0;
            byte b;
            do
            {
                if (i >= count) return "отказ без текста";
                b = buffer[i++];
                length |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            if (length <= 0 || i >= count) return "отказ без текста";
            var text = Encoding.UTF8.GetString(buffer, i, Math.Min(length, count - i));

            // Полный текст — URI вида .../framing/faults/EndpointUnavailable.
            // Человеку нужна последняя часть.
            var slash = text.LastIndexOf('/');
            return slash >= 0 && slash < text.Length - 1 ? text.Substring(slash + 1) : text;
        }
    }
}
