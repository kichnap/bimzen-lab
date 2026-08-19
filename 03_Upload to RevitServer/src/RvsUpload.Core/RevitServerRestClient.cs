using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RvsUpload
{
    /// <summary>
    /// Клиент Revit Server Admin RESTful API.
    ///
    /// База: http://&lt;host&gt;/RevitServerAdminRESTService&lt;year&gt;/AdminRESTService.svc/
    /// (для 2012 — без года в имени сервиса).
    ///
    /// Обязательные заголовки на КАЖДЫЙ запрос:
    ///   User-Name          — имя пользователя (пишется в журнал сервера)
    ///   User-Machine-Name  — имя машины
    ///   Operation-GUID     — уникальный GUID операции
    ///
    /// Поддерживает только метаданные: содержимое папок, инфо о модели, блокировки,
    /// создание/удаление/переименование папок. Залить модель через него НЕЛЬЗЯ —
    /// для этого нужен Revit (см. RvsUpload.Addin).
    /// </summary>
    public class RevitServerRestClient
    {
        private readonly string _baseUrl;
        private readonly string _userName;
        private readonly string _machineName;

        public RevitServerRestClient(string host, int revitVersion)
        {
            var service = revitVersion <= 2012
                ? "RevitServerAdminRESTService"
                : "RevitServerAdminRESTService" + revitVersion;

            _baseUrl = $"http://{host}/{service}/AdminRESTService.svc";
            _userName = Environment.UserName;
            _machineName = Environment.MachineName;
        }

        private HttpWebRequest CreateRequest(string relativeUrl, string method)
        {
            var url = _baseUrl + "/" + Uri.EscapeUriString(relativeUrl.TrimStart('/')).Replace("+", "%2B");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 120_000;
            req.Headers.Add("User-Name", _userName);
            req.Headers.Add("User-Machine-Name", _machineName);
            req.Headers.Add("Operation-GUID", Guid.NewGuid().ToString());
            return req;
        }

        private string Send(string relativeUrl, string method)
        {
            var req = CreateRequest(relativeUrl, method);
            if (method == "PUT" || method == "POST")
                req.ContentLength = 0;

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                var http = wex.Response as HttpWebResponse;
                string body = "";
                if (http != null)
                {
                    using (var sr = new StreamReader(http.GetResponseStream() ?? Stream.Null))
                        body = sr.ReadToEnd();
                }
                throw new RevitServerException(
                    $"REST {method} {relativeUrl} -> {(http != null ? ((int)http.StatusCode).ToString() : "нет ответа")} " +
                    $"{wex.Message}. {CleanBody(body)}",
                    http?.StatusCode);
            }
        }

        /// <summary>
        /// На ошибку сервер отдаёт HTML-страницу WCF на сотни строк со стилями.
        /// Целиком в лог она не нужна — вытаскиваем только текст и подрезаем.
        /// </summary>
        private static string CleanBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";

            if (body.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Осмысленное в такой странице — единственный абзац с текстом ошибки.
                var text = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

                // Отрезаем CSS, попавший внутрь <style> и потому не снятый тегами.
                var brace = text.LastIndexOf('}');
                if (brace >= 0 && brace < text.Length - 1) text = text.Substring(brace + 1).Trim();

                body = text;
            }

            return body.Length <= 300 ? body : body.Substring(0, 300) + "...";
        }

        /// <summary>GET /serverProperties — проверка доступности сервера (сырой ответ).</summary>
        public string GetServerProperties() => Send("/serverProperties", "GET");

        /// <summary>
        /// GET /serverProperties с разбором. Сервер сам сообщает свои ограничения
        /// на длину пути папки и имени модели — их и надо проверять в pre-flight,
        /// вместо того чтобы зашивать цифры в код: на разных серверах они могут
        /// отличаться.
        /// </summary>
        public ServerProperties GetServerPropertiesParsed()
        {
            var raw = GetServerProperties();
            try
            {
                return Json.Deserialize<ServerProperties>(raw);
            }
            catch (Exception ex)
            {
                // Разбор не удался — не повод падать: лимиты возьмутся запасные.
                // Сам факт ответа уже подтверждает доступность сервера.
                throw new RevitServerException(
                    $"Не удалось разобрать ответ /serverProperties: {ex.Message}. Ответ: {raw}");
            }
        }

        /// <summary>GET /&lt;path&gt;/contents</summary>
        public string GetContents(string serverRelativePath)
            => SendWithRetry(ToUrlPath(serverRelativePath) + "/contents", "GET");

        /// <summary>
        /// GET с повтором при обрыве связи.
        ///
        /// Нужен обходу дерева сервера: он делает по запросу на папку,
        /// а на измеренной площадке около 8% соединений обрывались сами
        /// по себе. Обход из десятков запросов почти наверняка попадёт
        /// хотя бы в один обрыв, и без повтора он ронял весь пакет —
        /// поймано живым прогоном.
        ///
        /// Повторяем ТОЛЬКО чтение и ТОЛЬКО обрыв связи. Ответ сервера
        /// с кодом (404, 405, 500) — это осмысленный ответ, повторять его
        /// бессмысленно и вредно: 500 при создании кириллической папки
        /// означает «уже создана».
        /// </summary>
        private string SendWithRetry(string relativeUrl, string method, int attempts = 3)
        {
            RevitServerException last = null;
            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    return Send(relativeUrl, method);
                }
                catch (RevitServerException ex) when (ex.StatusCode == null)
                {
                    last = ex;
                }
            }
            throw last;
        }

        /// <summary>
        /// Путь для URL. Корень сервера обозначается одиночным '|': проверено на
        /// живом сервере 2021 — '/|/contents' отвечает, а '/contents' и '/|contents'
        /// дают 405 Method Not Allowed.
        /// </summary>
        public static string ToUrlPath(string serverRelativePath)
        {
            var normalized = Normalize(serverRelativePath);
            return normalized.Length == 0 ? "|" : normalized;
        }

        /// <summary>GET /&lt;model&gt;/modelInfo — существует ли модель.</summary>
        public bool ModelExists(string serverRelativeModelPath)
        {
            try
            {
                Send(ToUrlPath(serverRelativeModelPath) + "/modelInfo", "GET");
                return true;
            }
            catch (RevitServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public bool FolderExists(string serverRelativePath)
        {
            try
            {
                Send(ToUrlPath(serverRelativePath) + "/DirectoryInfo", "GET");
                return true;
            }
            catch (RevitServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <summary>PUT /&lt;path&gt; — создать папку.</summary>
        public void CreateFolder(string serverRelativePath)
            => Send(Normalize(serverRelativePath), "PUT");

        /// <summary>
        /// Рекурсивно создаёт всю цепочку папок. SaveAs в Revit не создаёт
        /// отсутствующие папки на сервере — он просто падает.
        /// </summary>
        public void EnsureFolderChain(string serverRelativeFolderPath)
        {
            var parts = Normalize(serverRelativeFolderPath)
                .Trim('|', '/')
                .Split(new[] { '|', '/' }, StringSplitOptions.RemoveEmptyEntries);

            var current = "";
            foreach (var part in parts)
            {
                current = current.Length == 0 ? part : current + "|" + part;
                if (FolderExists(current)) continue;

                try
                {
                    CreateFolder(current);
                }
                catch (RevitServerException)
                {
                    // Revit Server 2021 отвечает 500 на создание папки с
                    // КИРИЛЛИЦЕЙ в имени, хотя папку при этом создаёт.
                    // Проверено перебором на живом сервере: латинские имена
                    // дают 200, русские — 500, и в обоих случаях папка
                    // появляется. Верить коду ответа тут нельзя, поэтому
                    // проверяем факт; если папки действительно нет — ошибка
                    // настоящая и её надо отдать наверх.
                    if (!FolderExists(current)) throw;
                }
            }
        }

        /// <summary>
        /// Состояние блокировки модели.
        ///
        /// Возвращает false, если сервер не поддерживает опрос блокировки: на
        /// Revit Server 2021 `GET /&lt;model&gt;/lock` отвечает **405 Method Not Allowed**
        /// — ресурс есть, но GET на нём не разрешён (PUT/DELETE для постановки и
        /// снятия работают). Проверено перебором: /lock/ , /lockstate ,
        /// /isLockedByOthers дают 404, то есть альтернативного GET-эндпоинта нет.
        ///
        /// Отличать «не заблокировано» от «узнать нельзя» обязательно: молча
        /// считать второе первым — значит убрать защиту, ради которой проверка
        /// и делалась.
        /// </summary>
        public bool TryGetLockState(string serverRelativeModelPath, out string state)
        {
            state = null;
            try
            {
                state = Send(ToUrlPath(serverRelativeModelPath) + "/lock", "GET");
                return true;
            }
            catch (RevitServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return true; // не заблокировано
            }
            catch (RevitServerException ex) when (ex.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                return false; // сервер не отдаёт статус блокировки
            }
        }

        /// <summary>DELETE /&lt;path&gt;/inProgressLock — снять зависший lock после падения клиента.</summary>
        public void CancelInProgressLock(string serverRelativePath)
            => Send(ToUrlPath(serverRelativePath) + "/inProgressLock", "DELETE");

        /// <summary>
        /// REST API использует '|' как разделитель пути. Повторяющиеся разделители
        /// схлопываем: 'A||B' сервер трактует как несуществующую папку с пустым именем.
        /// </summary>
        private static string Normalize(string p)
            => Join((p ?? "").Split('\\', '/', '|'));

        /// <summary>Склеивает непустые сегменты пути разделителем REST API.</summary>
        private static string Join(string[] segments)
        {
            var kept = new List<string>();
            foreach (var s in segments)
                if (!string.IsNullOrEmpty(s)) kept.Add(s);
            return string.Join("|", kept.ToArray());
        }

        // ---- парсинг RSN:// ----

        /// <summary>
        /// RSN://SERVER/Folder/Sub/Model.rvt -> host="SERVER", relative="Folder|Sub|Model.rvt"
        /// </summary>
        public static void ParseRsn(string rsnPath, out string host, out string serverRelativePath)
        {
            if (string.IsNullOrWhiteSpace(rsnPath) ||
                !rsnPath.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Путь назначения должен начинаться с RSN:// — получено: '{rsnPath}'");

            var rest = rsnPath.Substring("RSN://".Length).Replace('\\', '/').TrimStart('/');
            var slash = rest.IndexOf('/');
            if (slash < 0)
                throw new ArgumentException($"В пути '{rsnPath}' не указана модель после имени сервера.");

            host = rest.Substring(0, slash);

            // Схлопываем повторяющиеся слэши: 'RSN://srv//A//B/M.rvt' -> 'A|B|M.rvt'.
            // Пустой сегмент в середине пути сервер воспримет как отдельную папку с
            // пустым именем и вернёт 404 вместо внятной ошибки.
            serverRelativePath = Join(rest.Substring(slash + 1).Split('/'));

            if (serverRelativePath.Length == 0)
                throw new ArgumentException($"В пути '{rsnPath}' не указана модель после имени сервера.");
        }

        /// <summary>
        /// Склеивает папку назначения с именем файла: RSN://srv/Folder + Model.rvt
        /// -> RSN://srv/Folder/Model.rvt
        ///
        /// Имя файла подставляется КАК ЕСТЬ. Никаких суффиксов, префиксов и
        /// переименований утилита не делает и делать не должна: модель обязана
        /// лечь на сервер под тем же именем, что и на диске. Закреплено тестами.
        /// </summary>
        public static string CombineRsn(string destFolder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(destFolder))
                throw new ArgumentException("Не задана папка назначения.", nameof(destFolder));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("Не задано имя файла.", nameof(fileName));

            return destFolder.TrimEnd('/', '\\') + "/" + fileName;
        }

        public static string ParentOf(string serverRelativePath)
        {
            var idx = serverRelativePath.LastIndexOf('|');
            return idx <= 0 ? "" : serverRelativePath.Substring(0, idx);
        }

        // ---- обход дерева сервера ----

        /// <summary>
        /// Разбирает ответ /contents в списки имён папок и моделей.
        /// Формат: {"Folders":[{"Name":"..."}],"Models":[{"Name":"..."}], ...}
        /// </summary>
        public static void ParseContents(string json, out List<string> folders, out List<string> models)
        {
            var parsed = Json.Deserialize<ContentsResponse>(json);
            folders = new List<string>();
            models = new List<string>();

            if (parsed?.Folders != null)
                foreach (var f in parsed.Folders) if (!string.IsNullOrEmpty(f?.Name)) folders.Add(f.Name);

            if (parsed?.Models != null)
                foreach (var m in parsed.Models) if (!string.IsNullOrEmpty(m?.Name)) models.Add(m.Name);
        }

        /// <summary>
        /// Ищет модели с заданными именами в дереве сервера начиная с указанной
        /// папки. Возвращает: имя модели -> список папок, где она найдена.
        ///
        /// Одним обходом на весь пакет, а не поиском на каждую модель: дерево
        /// проекта — это сотни папок, и обходить его заново для каждой из
        /// полусотни моделей значит превратить подготовку в самую долгую часть
        /// работы.
        ///
        /// Список папок, а не одна папка, потому что одноимённые модели в разных
        /// папках встречаются, и выбирать за человека, куда лить, нельзя.
        /// </summary>
        /// <param name="maxFolders">
        /// Предохранитель от бесконечного обхода: сервер с тысячами папок
        /// должен дать понятную ошибку, а не молча работать час.
        /// </param>
        public Dictionary<string, List<string>> FindModels(
            string rootRelativePath, IEnumerable<string> modelNames, int maxFolders = 2000)
        {
            var wanted = new HashSet<string>(modelNames, StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var queue = new Queue<string>();
            queue.Enqueue(Normalize(rootRelativePath));
            var visited = 0;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (++visited > maxFolders)
                    throw new RevitServerException(
                        $"Обход дерева сервера прервён: просмотрено больше {maxFolders} папок. " +
                        "Сузьте область поиска через --dest-folder.");

                List<string> folders, models;
                try
                {
                    ParseContents(GetContents(current), out folders, out models);
                }
                catch (RevitServerException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                foreach (var m in models)
                {
                    if (!wanted.Contains(m)) continue;
                    if (!found.TryGetValue(m, out var list))
                    {
                        list = new List<string>();
                        found[m] = list;
                    }
                    list.Add(current);
                }

                foreach (var f in folders)
                    queue.Enqueue(current.Length == 0 ? f : current + "|" + f);
            }

            return found;
        }
    }

    /// <summary>Ответ GET /&lt;path&gt;/contents. Нужны только имена.</summary>
    public class ContentsResponse
    {
        public List<ContentsEntry> Folders { get; set; }
        public List<ContentsEntry> Models { get; set; }
    }

    public class ContentsEntry
    {
        public string Name { get; set; }
    }

    /// <summary>
    /// Ответ GET /serverProperties. Пример с живого сервера 2021:
    /// {"AccessLevelTypes":[],"MachineName":"REVIT","MaximumFolderPathLength":98,
    ///  "MaximumModelNameLength":40,"ServerRoles":[0,1,2],"Servers":["localhost","revit"]}
    /// </summary>
    public class ServerProperties
    {
        public string MachineName { get; set; }

        /// <summary>Максимальная длина пути папки. 0 = сервер не сообщил.</summary>
        public int MaximumFolderPathLength { get; set; }

        /// <summary>Максимальная длина имени модели. 0 = сервер не сообщил.</summary>
        public int MaximumModelNameLength { get; set; }

        public List<int> ServerRoles { get; set; } = new List<int>();
        public List<string> Servers { get; set; } = new List<string>();
        public List<string> AccessLevelTypes { get; set; } = new List<string>();
    }

    public class RevitServerException : Exception
    {
        public HttpStatusCode? StatusCode { get; }

        public RevitServerException(string message, HttpStatusCode? statusCode = null)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
