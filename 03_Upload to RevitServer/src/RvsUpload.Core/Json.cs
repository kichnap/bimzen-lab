using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace RvsUpload
{
    /// <summary>
    /// Минимальная обёртка над DataContractJsonSerializer из BCL.
    ///
    /// Зачем не Newtonsoft.Json: утилита командной строки должна тащить за собой
    /// минимум файлов, а аддин вдобавок грузится внутрь Revit, у которого своя
    /// копия Newtonsoft.Json.dll в папке установки. Две разные версии одной
    /// сборки в одном процессе — классический источник TypeLoadException при
    /// загрузке аддина. DataContractJsonSerializer входит в состав и
    /// .NET Framework 4.8, и .NET 8, и netstandard2.0, поэтому лишних DLL
    /// рядом с RvsUpload.exe и в папке аддина не появляется вовсе.
    ///
    /// Ограничения, которые мы принимаем осознанно (контракт в TaskFile.cs
    /// подогнан под них):
    ///  - типы должны быть простыми POCO с публичными get/set;
    ///  - DateTime сериализуется в нечитаемый формат \/Date(…)\/, поэтому
    ///    в контракте времени нет — только строки ISO-8601;
    ///  - порядок полей в выводе алфавитный, а не как в объявлении класса.
    /// </summary>
    public static class Json
    {
        /// <summary>Сериализует объект в JSON с отступами (result.json читают глазами).</summary>
        public static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(
                           ms, Encoding.UTF8, ownsStream: false, indent: true, indentChars: "  "))
                {
                    serializer.WriteObject(writer, value);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Пустой JSON.", nameof(json));

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)serializer.ReadObject(ms);
        }

        /// <summary>Пишет UTF-8 без BOM: BOM ломает разбор на стороне читателя.</summary>
        public static void WriteFile<T>(string path, T value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(value), new UTF8Encoding(false));
        }

        public static T ReadFile<T>(string path) => Deserialize<T>(File.ReadAllText(path));
    }
}
