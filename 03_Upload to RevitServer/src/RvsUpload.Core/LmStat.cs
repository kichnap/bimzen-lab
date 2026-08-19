using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RvsUpload
{
    /// <summary>Занятость одной лицензионной функции по данным lmstat.</summary>
    public class LicenseFeature
    {
        public string Feature { get; set; }
        public int Total { get; set; }
        public int InUse { get; set; }
        public int Available => Total - InUse;
    }

    /// <summary>
    /// Разбор вывода `lmutil lmstat -a` — опроса сетевого менеджера лицензий Autodesk.
    ///
    /// Зачем: если свободных лицензий нет, Revit показывает модальное окно и висит
    /// до таймаута, занимая сессию впустую. Узнать это заранее можно только при
    /// СЕТЕВОМ лицензировании: для single-user (Autodesk Identity) штатного способа
    /// спросить «свободна ли лицензия» не существует — там спасает только сторож
    /// старта (см. RevitRunner).
    ///
    /// Формат строки, который разбираем:
    ///   Users of 86717REVIT_2021_0F:  (Total of 5 licenses issued;  Total of 3 licenses in use)
    /// </summary>
    public static class LmStat
    {
        private static readonly Regex UsersLine = new Regex(
            @"Users of\s+(?<feature>\S+?):\s*\(Total of\s+(?<total>\d+)\s+licen[sc]es?\s+issued;\s*" +
            @"Total of\s+(?<inuse>\d+)\s+licen[sc]es?\s+in use\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static List<LicenseFeature> Parse(string lmstatOutput)
        {
            var list = new List<LicenseFeature>();
            if (string.IsNullOrWhiteSpace(lmstatOutput)) return list;

            foreach (Match m in UsersLine.Matches(lmstatOutput))
            {
                list.Add(new LicenseFeature
                {
                    Feature = m.Groups["feature"].Value,
                    Total = int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture),
                    InUse = int.Parse(m.Groups["inuse"].Value, CultureInfo.InvariantCulture),
                });
            }

            return list;
        }

        /// <summary>
        /// Находит функцию, относящуюся к Revit нужного года. Имена у Autodesk
        /// вида '86717REVIT_2021_0F' — привязываемся к 'REVIT' и году, а не
        /// к полному совпадению: префикс-код меняется от версии к версии.
        /// Возвращает null, если такой функции в ответе нет.
        /// </summary>
        public static LicenseFeature FindRevit(IEnumerable<LicenseFeature> features, int revitVersion)
        {
            if (features == null) return null;

            var year = revitVersion.ToString(CultureInfo.InvariantCulture);

            foreach (var f in features)
            {
                if (f.Feature == null) continue;
                if (f.Feature.IndexOf("REVIT", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (f.Feature.IndexOf(year, StringComparison.Ordinal) < 0) continue;
                return f;
            }

            return null;
        }
    }
}
