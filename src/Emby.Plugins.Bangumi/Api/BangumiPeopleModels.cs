using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Bangumi.Api
{
    /// <summary>Helpers shared by every entity that carries a wiki-style infobox.</summary>
    public static class BangumiInfobox
    {
        /// <summary>Every value stored under <paramref name="key"/>, flattened.</summary>
        public static IEnumerable<string> Values(List<BangumiInfoboxItem> infobox, string key)
        {
            if (infobox == null) yield break;
            foreach (var item in infobox)
            {
                if (item == null || !string.Equals(item.Key, key, StringComparison.Ordinal)) continue;
                foreach (var value in item.Values())
                {
                    if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
                }
            }
        }

        /// <summary>First value stored under any of <paramref name="keys"/>, or null.</summary>
        public static string First(List<BangumiInfoboxItem> infobox, params string[] keys)
        {
            if (infobox == null || keys == null) return null;
            foreach (var key in keys)
            {
                foreach (var value in Values(infobox, key)) return value;
            }

            return null;
        }
    }

    /// <summary>
    /// Common shape of /v0/persons/{id} and /v0/characters/{id}: both are "a name, a picture,
    /// a wiki infobox and a free-text summary", and both use the same partial birth date fields.
    /// </summary>
    public abstract class BangumiWikiEntity
    {
        [JsonPropertyName("id")] public int Id { get; set; }

        /// <summary>Person: 1 individual / 2 company / 3 band. Character: 1 character / 2 mechanic / 3 ship / 4 organization.</summary>
        [JsonPropertyName("type")] public int Type { get; set; }

        /// <summary>Original (usually Japanese) name. Bangumi has no name_cn on people or characters.</summary>
        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("summary")] public string Summary { get; set; }

        [JsonPropertyName("images")] public BangumiImages Images { get; set; }

        [JsonPropertyName("infobox")] public List<BangumiInfoboxItem> Infobox { get; set; }

        [JsonPropertyName("gender")] public string Gender { get; set; }

        [JsonPropertyName("blood_type")] public int? BloodType { get; set; }

        [JsonPropertyName("birth_year")] public int? BirthYear { get; set; }

        [JsonPropertyName("birth_mon")] public int? BirthMonth { get; set; }

        [JsonPropertyName("birth_day")] public int? BirthDay { get; set; }

        [JsonPropertyName("locked")] public bool Locked { get; set; }

        /// <summary>The 简体中文名 infobox entry, which is where a Chinese rendering of the name lives.</summary>
        public string ChineseName()
        {
            return BangumiInfobox.First(Infobox, "简体中文名", "中文名");
        }

        /// <summary>
        /// Bangumi records partial birth dates (year only, or month/day without a year). Emby's
        /// PremiereDate cannot express those, so anything without all three components is dropped.
        /// </summary>
        public DateTimeOffset? BirthDate()
        {
            if (!BirthYear.HasValue || !BirthMonth.HasValue || !BirthDay.HasValue) return null;
            if (BirthYear.Value < 1 || BirthMonth.Value < 1 || BirthMonth.Value > 12) return null;
            if (BirthDay.Value < 1 || BirthDay.Value > 31) return null;

            try
            {
                return new DateTimeOffset(
                    new DateTime(BirthYear.Value, BirthMonth.Value, BirthDay.Value, 0, 0, 0, DateTimeKind.Utc));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>Human readable birthday even when the date is partial ("1961年" / "12月1日").</summary>
        public string BirthDateText()
        {
            var fromInfobox = BangumiInfobox.First(Infobox, "生日", "出生");
            if (!string.IsNullOrWhiteSpace(fromInfobox)) return fromInfobox;

            if (BirthYear.HasValue && BirthMonth.HasValue && BirthDay.HasValue)
            {
                return string.Format(
                    CultureInfo.InvariantCulture, "{0}年{1}月{2}日", BirthYear, BirthMonth, BirthDay);
            }

            if (BirthMonth.HasValue && BirthDay.HasValue)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}月{1}日", BirthMonth, BirthDay);
            }

            return BirthYear.HasValue ? BirthYear.Value.ToString(CultureInfo.InvariantCulture) + "年" : null;
        }

        /// <summary>All alternative spellings: 别名 sub-entries plus the romanised / kana keys.</summary>
        public List<string> Aliases()
        {
            var aliases = new List<string>();
            foreach (var key in new[] { "别名", "別名", "简体中文名", "中文名", "罗马字", "纯假名", "英文名" })
            {
                foreach (var value in BangumiInfobox.Values(Infobox, key))
                {
                    if (!aliases.Contains(value, StringComparer.Ordinal)) aliases.Add(value);
                }
            }

            return aliases;
        }
    }

    /// <summary>GET /v0/persons/{id} - a real person, a company or a band.</summary>
    public sealed class BangumiPersonDetail : BangumiWikiEntity
    {
        /// <summary>producer / mangaka / artist / seiyu / writer / illustrator / actor.</summary>
        [JsonPropertyName("career")] public List<string> Career { get; set; }

        [JsonPropertyName("last_modified")] public string LastModified { get; set; }

        /// <summary>死亡日期 from the infobox; Bangumi has no structured field for it.</summary>
        public string DeathDateText()
        {
            return BangumiInfobox.First(Infobox, "卒日", "去世", "逝世");
        }

        public string BirthPlace()
        {
            return BangumiInfobox.First(Infobox, "出生地", "出身地", "国籍");
        }
    }

    /// <summary>GET /v0/characters/{id} - used to translate a role name into Chinese.</summary>
    public sealed class BangumiCharacterDetail : BangumiWikiEntity
    {
        [JsonPropertyName("nsfw")] public bool Nsfw { get; set; }
    }

    /// <summary>POST /v0/search/persons body.</summary>
    public sealed class BangumiPersonSearchRequest
    {
        [JsonPropertyName("keyword")] public string Keyword { get; set; }
    }
}
