using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Bangumi.Api
{
    // Shapes verified against https://api.bgm.tv (v0) on 2026-08-30 using
    // subject 552533 (天幕のジャードゥーガル) and a POST /v0/search/subjects query.
    // Notes worth keeping:
    //   * /v0/search/subjects items have the SAME shape as /v0/subjects/{id},
    //     including rating/infobox/tags/meta_tags, so one model covers both.
    //   * infobox values are polymorphic: either a string or [{ "k": .., "v": .. }].
    //   * ep and sort may be fractional (7.5 for a mid-season special), hence double.

    public sealed class BangumiImages
    {
        [JsonPropertyName("large")] public string Large { get; set; }
        [JsonPropertyName("common")] public string Common { get; set; }
        [JsonPropertyName("medium")] public string Medium { get; set; }
        [JsonPropertyName("small")] public string Small { get; set; }
        [JsonPropertyName("grid")] public string Grid { get; set; }

        public string Best()
        {
            if (!string.IsNullOrEmpty(Large)) return Large;
            if (!string.IsNullOrEmpty(Common)) return Common;
            if (!string.IsNullOrEmpty(Medium)) return Medium;
            if (!string.IsNullOrEmpty(Small)) return Small;
            return Grid;
        }

        public string Thumbnail()
        {
            if (!string.IsNullOrEmpty(Medium)) return Medium;
            if (!string.IsNullOrEmpty(Common)) return Common;
            if (!string.IsNullOrEmpty(Small)) return Small;
            if (!string.IsNullOrEmpty(Grid)) return Grid;
            return Large;
        }
    }

    public sealed class BangumiTag
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    }

    public sealed class BangumiRating
    {
        [JsonPropertyName("rank")] public int Rank { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("score")] public double Score { get; set; }
    }

    public sealed class BangumiInfoboxItem
    {
        [JsonPropertyName("key")] public string Key { get; set; }
        [JsonPropertyName("value")] public JsonElement Value { get; set; }

        /// <summary>Flattens the polymorphic value into a list of plain strings.</summary>
        public List<string> Values()
        {
            var result = new List<string>();
            switch (Value.ValueKind)
            {
                case JsonValueKind.String:
                    var s = Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s.Trim());
                    break;

                case JsonValueKind.Number:
                    result.Add(Value.GetRawText());
                    break;

                case JsonValueKind.Array:
                    foreach (var element in Value.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            var direct = element.GetString();
                            if (!string.IsNullOrWhiteSpace(direct)) result.Add(direct.Trim());
                        }
                        else if (element.ValueKind == JsonValueKind.Object &&
                                 element.TryGetProperty("v", out var v) &&
                                 v.ValueKind == JsonValueKind.String)
                        {
                            var nested = v.GetString();
                            if (!string.IsNullOrWhiteSpace(nested)) result.Add(nested.Trim());
                        }
                    }
                    break;
            }

            return result;
        }
    }

    public sealed class BangumiSubject
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("type")] public int Type { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("name_cn")] public string NameCn { get; set; }
        [JsonPropertyName("summary")] public string Summary { get; set; }
        [JsonPropertyName("nsfw")] public bool Nsfw { get; set; }
        [JsonPropertyName("locked")] public bool Locked { get; set; }
        [JsonPropertyName("series")] public bool Series { get; set; }

        /// <summary>Air date, "yyyy-MM-dd". May be null, empty or partially unknown.</summary>
        [JsonPropertyName("date")] public string Date { get; set; }

        /// <summary>TV / OVA / WEB / 剧场版 / ...</summary>
        [JsonPropertyName("platform")] public string Platform { get; set; }

        [JsonPropertyName("images")] public BangumiImages Images { get; set; }
        [JsonPropertyName("infobox")] public List<BangumiInfoboxItem> Infobox { get; set; }
        [JsonPropertyName("volumes")] public int Volumes { get; set; }
        [JsonPropertyName("eps")] public int Eps { get; set; }
        [JsonPropertyName("total_episodes")] public int TotalEpisodes { get; set; }
        [JsonPropertyName("rating")] public BangumiRating Rating { get; set; }
        [JsonPropertyName("tags")] public List<BangumiTag> Tags { get; set; }

        /// <summary>Curated, low-cardinality tags (TV / 日本 / 漫画改 / 历史). Good Genre material.</summary>
        [JsonPropertyName("meta_tags")] public List<string> MetaTags { get; set; }

        public IEnumerable<string> InfoboxValues(string key)
        {
            if (Infobox == null) yield break;
            foreach (var item in Infobox)
            {
                if (item == null || !string.Equals(item.Key, key, System.StringComparison.Ordinal)) continue;
                foreach (var value in item.Values()) yield return value;
            }
        }
    }

    public sealed class BangumiPaged<T>
    {
        [JsonPropertyName("data")] public List<T> Data { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
        [JsonPropertyName("offset")] public int Offset { get; set; }
    }

    public sealed class BangumiEpisode
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("subject_id")] public int SubjectId { get; set; }

        /// <summary>0 main, 1 special, 2 opening, 3 ending, 4 trailer, 5 mad, 6 other.</summary>
        [JsonPropertyName("type")] public int Type { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("name_cn")] public string NameCn { get; set; }

        /// <summary>Number continuing across the whole franchise. Always present.</summary>
        [JsonPropertyName("sort")] public double Sort { get; set; }

        /// <summary>Number inside this subject / season. Absent for some specials.</summary>
        [JsonPropertyName("ep")] public double? Ep { get; set; }

        [JsonPropertyName("airdate")] public string Airdate { get; set; }
        [JsonPropertyName("comment")] public int Comment { get; set; }

        /// <summary>Free-form, usually "HH:mm:ss" but sometimes "24m" or empty.</summary>
        [JsonPropertyName("duration")] public string Duration { get; set; }

        [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; set; }
        [JsonPropertyName("desc")] public string Desc { get; set; }
        [JsonPropertyName("disc")] public int Disc { get; set; }
    }

    public class BangumiPersonBrief
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }

        /// <summary>1 individual, 2 corporation, 3 group.</summary>
        [JsonPropertyName("type")] public int Type { get; set; }

        [JsonPropertyName("images")] public BangumiImages Images { get; set; }
        [JsonPropertyName("career")] public List<string> Career { get; set; }
    }

    public sealed class BangumiRelatedPerson : BangumiPersonBrief
    {
        /// <summary>Chinese staff role: 导演 / 脚本 / 音乐 / 动画制作 / ...</summary>
        [JsonPropertyName("relation")] public string Relation { get; set; }

        [JsonPropertyName("eps")] public string Eps { get; set; }
    }

    public sealed class BangumiRelatedCharacter
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("type")] public int Type { get; set; }
        [JsonPropertyName("images")] public BangumiImages Images { get; set; }

        /// <summary>主角 / 配角 / 客串</summary>
        [JsonPropertyName("relation")] public string Relation { get; set; }

        [JsonPropertyName("actors")] public List<BangumiPersonBrief> Actors { get; set; }
    }

    public sealed class BangumiRelatedSubject
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("type")] public int Type { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("name_cn")] public string NameCn { get; set; }
        [JsonPropertyName("images")] public BangumiImages Images { get; set; }

        /// <summary>续集 / 前传 / 书籍 / 片头曲 / 片尾曲 / 原声集 / 番外篇 / ...</summary>
        [JsonPropertyName("relation")] public string Relation { get; set; }
    }

    public sealed class BangumiSearchRequest
    {
        [JsonPropertyName("keyword")] public string Keyword { get; set; }
        [JsonPropertyName("sort")] public string Sort { get; set; }
        [JsonPropertyName("filter")] public BangumiSearchFilter Filter { get; set; }
    }

    public sealed class BangumiSearchFilter
    {
        [JsonPropertyName("type")] public List<int> Type { get; set; }
        [JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
    }
}