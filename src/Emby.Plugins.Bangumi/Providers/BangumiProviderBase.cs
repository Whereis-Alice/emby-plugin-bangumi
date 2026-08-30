using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using Emby.Plugins.Bangumi.Utils;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>
    /// Shared plumbing for every Bangumi provider: id extraction, candidate scoring,
    /// subject to <see cref="BaseItem"/> mapping and image fetching.
    /// </summary>
    public abstract class BangumiProviderBase
    {
        // \u30FC is the katakana prolonged sound mark. Bangumi wraps subtitles in it
        // ("…第三季 ーBONUS STAGEー") and it is a letter, not punctuation, so it has to be listed
        // explicitly or it splits an otherwise identical title.
        private static readonly Regex NonWord =
            new Regex(@"[\s\p{P}\p{S}\u30FC\u2015\u2500]", RegexOptions.Compiled);
        private static readonly Regex MinutesOnly = new Regex(@"(\d+)\s*(?:分|min|mins|minutes|m)\b?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Clock = new Regex(@"^(?:(\d+):)?(\d{1,2}):(\d{2})$", RegexOptions.Compiled);

        /// <summary>
        /// Infobox keys that carry the actual animation studio. Checked first because these are the
        /// only keys whose value is reliably a studio name and nothing else.
        /// </summary>
        private static readonly string[] StudioKeys = { "动画制作", "動画制作", "アニメーション制作", "アニメーション" };

        /// <summary>
        /// Fallback infobox keys, used only when <see cref="StudioKeys"/> produced nothing. On Bangumi
        /// these frequently hold a production committee plus a run-on list of individual producers
        /// ("XX製作委員会(A、B、C)；山田太郎、鈴木花子"), which is why they are not trusted by default.
        /// </summary>
        private static readonly string[] StudioFallbackKeys = { "制作", "製作", "出品方" };

        /// <summary>Separators seen inside a single infobox studio value.</summary>
        private static readonly char[] StudioSeparators =
            { '、', '，', ',', '/', '×', '；', ';', '(', ')', '（', '）', '\n', '\r' };


        /// <summary>Infobox keys that carry alternative titles.</summary>
        private static readonly string[] AliasKeys = { "别名", "別名", "英文名", "罗马字", "中文名" };

        private static readonly Dictionary<string, PersonType> StaffRoleMap =
            new Dictionary<string, PersonType>(StringComparer.Ordinal)
            {
                { "导演", PersonType.Director },
                { "总导演", PersonType.Director },
                { "监督", PersonType.Director },
                { "副导演", PersonType.Director },
                { "系列导演", PersonType.Director },
                { "演出", PersonType.Director },
                { "分镜", PersonType.Director },

                { "脚本", PersonType.Writer },
                { "系列构成", PersonType.Writer },
                { "原作", PersonType.Writer },
                { "剧本", PersonType.Writer },
                { "原案", PersonType.Writer },

                { "音乐", PersonType.Composer },
                { "主题歌作曲", PersonType.Composer },
                { "插入歌作曲", PersonType.Composer },
                { "歌曲编曲", PersonType.Composer },
                { "主题歌编曲", PersonType.Composer },
                { "音乐制作", PersonType.Composer },

                { "主题歌作词", PersonType.Lyricist },
                { "插入歌作词", PersonType.Lyricist },

                { "制片人", PersonType.Producer },
                { "动画制片人", PersonType.Producer },
                { "助理制片人", PersonType.Producer },
                { "宣传制片人", PersonType.Producer },
                { "企画", PersonType.Producer },
                { "企划", PersonType.Producer },
                { "製作", PersonType.Producer },
                { "製作総指揮", PersonType.Producer },
                { "执行制片人", PersonType.Producer },
            };

        private static readonly Dictionary<string, DayOfWeek> WeekdayMap =
            new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
            {
                { "月", DayOfWeek.Monday }, { "火", DayOfWeek.Tuesday }, { "水", DayOfWeek.Wednesday },
                { "木", DayOfWeek.Thursday }, { "金", DayOfWeek.Friday }, { "土", DayOfWeek.Saturday },
                { "日", DayOfWeek.Sunday },
                { "一", DayOfWeek.Monday }, { "二", DayOfWeek.Tuesday }, { "三", DayOfWeek.Wednesday },
                { "四", DayOfWeek.Thursday }, { "五", DayOfWeek.Friday }, { "六", DayOfWeek.Saturday },
            };

        protected BangumiProviderBase(ILogManager logManager)
        {
            Logger = logManager == null
                ? null
                : logManager.GetLogger(BangumiConstants.PluginName + "." + GetType().Name);
        }

        protected ILogger Logger { get; private set; }

        /// <summary>Name shown in the Emby metadata provider list and stored in NFO files.</summary>
        public string Name
        {
            get { return BangumiConstants.PluginName; }
        }

        /// <summary>Configurable so users can rank Bangumi above or below TMDB / TVDB.</summary>
        public int Order
        {
            get { return CurrentOptions.ProviderOrder; }
        }

        protected static BangumiApiClient Api
        {
            get { return Plugin.RequireApi(); }
        }

        protected static PluginOptions CurrentOptions
        {
            get { return Plugin.CurrentOptions(); }
        }

        protected void Verbose(string format, params object[] args)
        {
            if (Logger == null) return;
            if (!CurrentOptions.EnableVerboseLogging) return;
            Logger.Info(format, args);
        }

        // ---------------------------------------------------------------- ids

        protected static bool TryGetSubjectId(IDictionary<string, string> providerIds, out int subjectId)
        {
            return TryGetId(providerIds, BangumiConstants.ProviderId, out subjectId);
        }

        protected static bool TryGetId(IDictionary<string, string> providerIds, string key, out int id)
        {
            id = 0;
            if (providerIds == null) return false;

            string raw;
            if (!providerIds.TryGetValue(key, out raw)) return false;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // Some NFO files store the full subject URL rather than a bare id.
            var match = Regex.Match(raw, @"(\d+)");
            if (!match.Success) return false;

            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out id) && id > 0;
        }

        // ---------------------------------------------------------------- parsing

        /// <summary>Parses "yyyy-MM-dd" and its partially unknown variants ("2026-00-00").</summary>
        protected static DateTimeOffset? ParseDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = raw.Trim();
            var match = Regex.Match(text, @"^(\d{4})(?:[-/](\d{1,2}))?(?:[-/](\d{1,2}))?");
            if (!match.Success) return null;

            int year;
            if (!int.TryParse(match.Groups[1].Value, out year) || year < 1900 || year > 2200) return null;

            var month = 1;
            var day = 1;
            if (match.Groups[2].Success) int.TryParse(match.Groups[2].Value, out month);
            if (match.Groups[3].Success) int.TryParse(match.Groups[3].Value, out day);
            if (month < 1 || month > 12) month = 1;
            if (day < 1) day = 1;
            if (day > DateTime.DaysInMonth(year, month)) day = DateTime.DaysInMonth(year, month);

            return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        }

        /// <summary>Parses "24分", "1:52:00", "24 min" into ticks. Returns null when unparsable.</summary>
        protected static long? ParseDurationToTicks(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = TitleNormalizer.ToHalfWidth(raw).Trim();

            var clock = Clock.Match(text);
            if (clock.Success)
            {
                var hours = clock.Groups[1].Success ? int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                var minutes = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture);
                var span = new TimeSpan(hours, minutes, seconds);
                return span > TimeSpan.Zero ? (long?)span.Ticks : null;
            }

            var minutesOnly = MinutesOnly.Match(text);
            if (minutesOnly.Success)
            {
                int value;
                if (int.TryParse(minutesOnly.Groups[1].Value, out value) && value > 0 && value < 60 * 24)
                {
                    return TimeSpan.FromMinutes(value).Ticks;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- search

        /// <summary>
        /// Two-stage lookup: search with the season marker intact first (Bangumi gives every
        /// season its own subject, so "Re:Zero 第四季" is a real title there), then fall back
        /// to the bare name.
        /// </summary>
        protected Task<List<BangumiSubject>> SearchAsync(
            string rawTitle, int subjectType, int? year, CancellationToken cancellationToken)
        {
            return SearchAsync(rawTitle, null, subjectType, year, cancellationToken);
        }

        /// <summary>
        /// Two-stage lookup: search with the season marker intact first (Bangumi gives every
        /// season its own subject, so "Re:Zero 第四季" is a real title there), then fall back
        /// to the bare name.
        ///
        /// <paramref name="pathHint"/> is the item's own path. It matters more than it looks: once a
        /// series has been mis-identified by another provider, Emby keeps handing that wrong name to
        /// every later lookup, while the folder on disk still spells out what the release actually is
        /// ("Clevatess-魔兽之王与婴儿与尸之勇者" stored in "2026年7月 Clevatess II-魔兽之王与虚假的勇者传承-").
        /// </summary>
        protected async Task<List<BangumiSubject>> SearchAsync(
            string rawTitle, string pathHint, int subjectType, int? year, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var titles = BuildTitleCandidates(rawTitle, pathHint);
            if (titles.Count == 0) return new List<BangumiSubject>();

            var attempted = new List<string>();
            var results = new List<BangumiSubject>();

            foreach (var keyword in EnumerateKeywords(titles))
            {
                if (attempted.Contains(keyword)) continue;
                attempted.Add(keyword);

                results = await Api
                    .SearchSubjectsAsync(keyword, subjectType, options.SearchResultLimit, cancellationToken)
                    .ConfigureAwait(false);

                Verbose("Bangumi search \"{0}\" -> {1} candidate(s)", keyword, results == null ? 0 : results.Count);
                if (results != null && results.Count > 0) break;
            }

            if (results == null) results = new List<BangumiSubject>();
            return Rank(results, titles, year);
        }

        /// <summary>
        /// The item name first, then the folder name when it normalises to something different.
        /// </summary>
        private static List<NormalizedTitle> BuildTitleCandidates(string rawTitle, string pathHint)
        {
            var titles = new List<NormalizedTitle>();

            foreach (var source in new[] { rawTitle, FolderNameOf(pathHint) })
            {
                if (string.IsNullOrWhiteSpace(source)) continue;

                var normalized = TitleNormalizer.Normalize(source);
                if (string.IsNullOrWhiteSpace(normalized.KeywordWithSeason)) continue;
                if (titles.Any(t => string.Equals(t.KeywordWithSeason, normalized.KeywordWithSeason, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                titles.Add(normalized);
            }

            return titles;
        }

        /// <summary>Last path segment, directory or file, without touching the file system.</summary>
        private static string FolderNameOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var trimmed = path.TrimEnd('\\', '/');
            var separator = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            var leaf = separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
            return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
        }

        private static IEnumerable<string> EnumerateKeywords(List<NormalizedTitle> titles)
        {
            foreach (var normalized in titles) yield return normalized.KeywordWithSeason;
            foreach (var normalized in titles)
            {
                if (normalized.HasSeasonMarker) yield return normalized.Keyword;
            }
        }

        /// <summary>
        /// Season the caller is really asking about. An explicit sequel marker wins over "no marker",
        /// so a folder named "... 第四季" still steers the lookup when the stored item name lost it.
        /// </summary>
        private static int? WantedSeason(List<NormalizedTitle> titles)
        {
            foreach (var normalized in titles)
            {
                if (normalized.SeasonNumber.HasValue && normalized.SeasonNumber.Value > 1) return normalized.SeasonNumber;
            }

            foreach (var normalized in titles)
            {
                if (normalized.SeasonNumber.HasValue) return normalized.SeasonNumber;
            }

            return null;
        }

        /// <summary>Sorts candidates best-first without dropping any of them.</summary>
        private List<BangumiSubject> Rank(List<BangumiSubject> candidates, List<NormalizedTitle> titles, int? year)
        {
            if (candidates == null || candidates.Count == 0) return new List<BangumiSubject>();

            var options = CurrentOptions;
            var usable = candidates
                .Where(c => c != null && c.Id > 0)
                .Where(c => options.IncludeNsfw || !c.Nsfw)
                .ToList();

            // Whether the season the query asked for is actually present in this result set. Only
            // then is it safe to be harsh about candidates that carry no marker at all, because
            // plenty of real sequels are filed under a subtitle instead of "第N季".
            var wanted = WantedSeason(titles);
            var exactSeasonAvailable = wanted.HasValue && wanted.Value > 1 && usable.Any(c =>
            {
                var marker = BangumiSeasonResolver.SeasonMarkerOf(c.Name, c.NameCn);
                return marker.HasValue && marker.Value == wanted.Value;
            });

            var scored = usable
                .Select((subject, index) => new
                {
                    subject,
                    index,
                    score = Score(subject, titles, year, exactSeasonAvailable),
                })
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.index)
                .ToList();

            if (options.EnableVerboseLogging && Logger != null)
            {
                foreach (var entry in scored.Take(8))
                {
                    Logger.Info(
                        "Bangumi candidate score={0} id={1} name={2} name_cn={3} date={4}",
                        entry.score, entry.subject.Id, entry.subject.Name, entry.subject.NameCn, entry.subject.Date);
                }
            }

            return scored.Select(x => x.subject).ToList();
        }

        private int Score(BangumiSubject subject, List<NormalizedTitle> queries, int? year, bool exactSeasonAvailable)
        {
            var score = 0;
            var wantedSeason = WantedSeason(queries);

            // Once a season is known, only the title sources that actually name it may score. The
            // stored item name is often the franchise root left behind by another provider
            // ("为美好的世界献上祝福！" for a folder holding season 3's bonus stage); keeping it in the
            // pool would hand a perfect title match to every season of the franchise at once.
            var sources = queries;
            if (wantedSeason.HasValue)
            {
                var marked = queries
                    .Where(q => q.SeasonNumber.HasValue && q.SeasonNumber.Value == wantedSeason.Value)
                    .ToList();
                if (marked.Count > 0) sources = marked;
            }

            var wanted = new List<string>();
            foreach (var normalized in sources)
            {
                if (!string.IsNullOrWhiteSpace(normalized.KeywordWithSeason)) wanted.Add(normalized.KeywordWithSeason);
                if (normalized.HasSeasonMarker && !string.IsNullOrWhiteSpace(normalized.Keyword)) wanted.Add(normalized.Keyword);
            }

            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(subject.Name)) titles.Add(subject.Name);
            if (!string.IsNullOrWhiteSpace(subject.NameCn)) titles.Add(subject.NameCn);
            foreach (var aliasKey in AliasKeys)
            {
                foreach (var alias in subject.InfoboxValues(aliasKey))
                {
                    if (!string.IsNullOrWhiteSpace(alias)) titles.Add(alias);
                }
            }

            // When the query names a season, compare the season-stripped titles as well. The two
            // sides spell the marker differently often enough ("第3季" vs "第三季") that the
            // remaining subtitle is the only part worth comparing, and it is what separates
            // "…第三季" from "…第三季 ーBONUS STAGEー". Season agreement below still decides which
            // season won, so dropping the marker here cannot promote the wrong one.
            var compareStripped = wantedSeason.HasValue && wantedSeason.Value > 1;

            var comparable = new List<string>();
            foreach (var title in titles)
            {
                var folded = Fold(title);
                if (folded.Length > 0 && !comparable.Contains(folded)) comparable.Add(folded);
                if (!compareStripped) continue;

                int? ignored;
                var stripped = Fold(TitleNormalizer.StripSeason(TitleNormalizer.Clean(title), out ignored));
                if (stripped.Length > 0 && !comparable.Contains(stripped)) comparable.Add(stripped);
            }

            var best = 0;
            foreach (var want in wanted.Select(Fold).Where(w => w.Length > 0))
            {
                foreach (var title in comparable)
                {
                    var value = Similarity(want, title);
                    if (value > best) best = value;
                }
            }

            score += best;

            // Bangumi files every season as its own subject, so the season marker carried by the
            // candidate's own title is the most reliable signal available. Without this a query
            // for "异兽魔都 第二季" loses to the far more popular season 1 subject, because both
            // titles match one of the keywords exactly.
            score += SeasonAgreement(subject, wantedSeason, exactSeasonAvailable);

            // Bangumi splits every cour into its own subject, so the air year is a strong signal.
            var subjectYear = YearOf(subject);
            if (year.HasValue && subjectYear.HasValue)
            {
                var delta = Math.Abs(subjectYear.Value - year.Value);
                if (delta == 0) score += 120;
                else if (delta == 1) score += 40;
                else score -= 60;
            }

            // Popularity as a tie-break only; capped so it can never beat a title match.
            if (subject.Rating != null && subject.Rating.Total > 0)
            {
                score += Math.Min(40, (int)Math.Round(Math.Log10(subject.Rating.Total + 1) * 14));
            }

            score += PlatformBonus(subject.Platform);

            return score;
        }

        /// <summary>
        /// Title closeness in 0..1000. Partial matches are scaled by how much of the longer
        /// string they cover, so a two character subject name cannot outrank a real translation
        /// merely because the query happens to start with it.
        /// </summary>
        private static int Similarity(string want, string title)
        {
            if (string.Equals(want, title, StringComparison.Ordinal)) return 1000;

            var shorter = want.Length <= title.Length ? want : title;
            var longer = want.Length <= title.Length ? title : want;
            var coverage = (double)shorter.Length / longer.Length;

            if (longer.StartsWith(shorter, StringComparison.Ordinal))
            {
                return 300 + (int)Math.Round(400 * coverage);
            }

            if (longer.IndexOf(shorter, StringComparison.Ordinal) >= 0)
            {
                return 200 + (int)Math.Round(300 * coverage);
            }

            // Neither string contains the other. Character bigram overlap still recognises a
            // different translation of the same work ("穹庐下的魔女" vs "天幕的魔女"); below the
            // floor it is indistinguishable from noise.
            var dice = BigramDice(want, title);
            return dice < 0.34 ? 0 : (int)Math.Round(600 * dice);
        }

        /// <summary>Sørensen-Dice coefficient over character bigrams, multiset semantics.</summary>
        private static double BigramDice(string left, string right)
        {
            if (left == null || right == null || left.Length < 2 || right.Length < 2) return 0d;

            var pool = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < left.Length - 1; i++)
            {
                var gram = left.Substring(i, 2);
                int count;
                pool[gram] = pool.TryGetValue(gram, out count) ? count + 1 : 1;
            }

            var shared = 0;
            for (var i = 0; i < right.Length - 1; i++)
            {
                var gram = right.Substring(i, 2);
                int count;
                if (pool.TryGetValue(gram, out count) && count > 0)
                {
                    pool[gram] = count - 1;
                    shared++;
                }
            }

            return 2d * shared / ((left.Length - 1) + (right.Length - 1));
        }

        /// <summary>
        /// Rewards a candidate whose own title carries the season the query asked for, and pushes
        /// away both the franchise root (no marker) and a different season.
        /// </summary>
        private static int SeasonAgreement(BangumiSubject subject, int? wantedSeason, bool exactSeasonAvailable)
        {
            var marker = BangumiSeasonResolver.SeasonMarkerOf(subject.Name, subject.NameCn);

            if (!wantedSeason.HasValue || wantedSeason.Value <= 1)
            {
                // The query names no season, so a subject advertising one is the wrong entry.
                return marker.HasValue && marker.Value > 1 ? -140 : 0;
            }

            if (marker.HasValue) return marker.Value == wantedSeason.Value ? 220 : -260;

            // No marker on the candidate. Normally only a mild penalty, because Bangumi often files
            // a sequel under a subtitle. But when the asked-for season is sitting right there in the
            // same result set, an unmarked subject is the franchise root, and it has to lose even
            // though the query text contains its title verbatim - which is exactly what happens for
            // "Clevatess II-..." (season 1's name is a substring of the season 2 folder name).
            return exactSeasonAvailable ? -580 : -160;
        }
        /// <summary>
        /// Small nudge so a series lookup prefers a broadcast entry and a movie lookup prefers a
        /// 剧场版 entry. Deliberately tiny: it must never outweigh a title or year match.
        /// </summary>
        protected virtual int PlatformBonus(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return 0;
            if (string.Equals(platform, "TV", StringComparison.OrdinalIgnoreCase)) return 10;
            if (string.Equals(platform, "WEB", StringComparison.OrdinalIgnoreCase)) return 6;
            return 0;
        }

        private static int? YearOf(BangumiSubject subject)
        {
            var date = ParseDate(subject == null ? null : subject.Date);
            return date.HasValue ? (int?)date.Value.Year : null;
        }

        /// <summary>Case and punctuation insensitive comparison form.</summary>
        private static string Fold(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var folded = TitleNormalizer.ToHalfWidth(text).ToLowerInvariant();
            return NonWord.Replace(folded, string.Empty);
        }

        // ---------------------------------------------------------------- mapping

        protected RemoteSearchResult ToSearchResult(BangumiSubject subject)
        {
            var options = CurrentOptions;
            var date = ParseDate(subject.Date);

            var result = new RemoteSearchResult
            {
                Name = PickTitle(subject.Name, subject.NameCn, options),
                SearchProviderName = BangumiConstants.PluginName,
                Overview = subject.Summary,
                ImageUrl = subject.Images == null ? null : subject.Images.Thumbnail(),
                PremiereDate = date,
                ProductionYear = date.HasValue ? (int?)date.Value.Year : null,
            };

            if (options.WriteOriginalTitle) result.OriginalTitle = subject.Name;

            // Shown next to identically named entries in the "Identify" dialog. Bangumi has a
            // lot of same-titled subjects (TV / OVA / 剧场版 cuts of one franchise).
            var disambiguation = new List<string>();
            if (!string.IsNullOrWhiteSpace(subject.Platform)) disambiguation.Add(subject.Platform);
            if (subject.TotalEpisodes > 0) disambiguation.Add(subject.TotalEpisodes + " eps");
            else if (subject.Eps > 0) disambiguation.Add(subject.Eps + " eps");
            if (disambiguation.Count > 0) result.DisambiguationComment = string.Join(", ", disambiguation);

            result.ProviderIds[BangumiConstants.ProviderId] =
                subject.Id.ToString(CultureInfo.InvariantCulture);

            return result;
        }

        protected static string PickTitle(string name, string nameCn, PluginOptions options)
        {
            if (options != null && options.PreferChineseTitle && !string.IsNullOrWhiteSpace(nameCn)) return nameCn.Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            return string.IsNullOrWhiteSpace(nameCn) ? null : nameCn.Trim();
        }

        /// <summary>Copies the fields that every subject-backed item type shares.</summary>
        protected void ApplySubject(BaseItem item, BangumiSubject subject, PluginOptions options)
        {
            if (item == null || subject == null) return;

            item.Name = PickTitle(subject.Name, subject.NameCn, options) ?? item.Name;
            if (options.WriteOriginalTitle && !string.IsNullOrWhiteSpace(subject.Name))
            {
                item.OriginalTitle = subject.Name;
            }

            if (!string.IsNullOrWhiteSpace(subject.Summary)) item.Overview = subject.Summary.Trim();

            var date = ParseDate(subject.Date);
            if (date.HasValue)
            {
                item.PremiereDate = date;
                item.ProductionYear = date.Value.Year;
            }

            if (subject.Rating != null && subject.Rating.Score > 0)
            {
                item.CommunityRating = (float)subject.Rating.Score;
            }

            var genres = BuildGenres(subject, options);
            if (genres.Length > 0) item.Genres = genres;

            var tags = BuildTags(subject, options);
            if (tags.Length > 0) item.Tags = tags;

            var studios = BuildStudios(subject);
            if (studios.Length > 0) item.Studios = studios;

            item.ProviderIds[BangumiConstants.ProviderId] =
                subject.Id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Trimmed, de-duplicated meta_tags, in the order Bangumi returned them.</summary>
        private static List<string> MetaTagsOf(BangumiSubject subject)
        {
            var result = new List<string>();
            if (subject.MetaTags == null) return result;
            foreach (var tag in subject.MetaTags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var trimmed = tag.Trim();
                if (!result.Contains(trimmed, StringComparer.Ordinal)) result.Add(trimmed);
            }

            return result;
        }

        private static HashSet<string> ParseCsvSet(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(csv)) return set;
            foreach (var piece in csv.Split(new[] { ',', '，', '、', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }

            return set;
        }

        private static string[] BuildGenres(BangumiSubject subject, PluginOptions options)
        {
            // meta_tags is Bangumi's curated, low cardinality tag set (TV / 日本 / 漫画改 / 历史) which
            // maps far better onto Emby genres than the free-for-all user tags -- but roughly half of
            // it is platform / country / source-material metadata rather than a genre, so blocklisted
            // entries are filtered out here and kept in Tags instead.
            var genres = new List<string>();
            if (options.ImportMetaTagsAsGenres)
            {
                var blocked = ParseCsvSet(options.GenreBlocklist);
                foreach (var tag in MetaTagsOf(subject))
                {
                    if (blocked.Contains(tag)) continue;
                    if (!genres.Contains(tag)) genres.Add(tag);
                }
            }

            if (options.ImportTagsAsGenres && subject.Tags != null)
            {
                foreach (var tag in subject.Tags.OrderByDescending(t => t.Count).Take(Math.Max(0, options.MaxTags)))
                {
                    if (tag == null || string.IsNullOrWhiteSpace(tag.Name)) continue;
                    var trimmed = tag.Name.Trim();
                    if (!genres.Contains(trimmed)) genres.Add(trimmed);
                }
            }

            return genres.ToArray();
        }

        private static string[] BuildTags(BangumiSubject subject, PluginOptions options)
        {
            if (!options.ImportTags) return new string[0];
            if (subject.Tags == null) return MetaTagsOf(subject).ToArray();

            // meta_tags go in first: they are curated, they are what the genre blocklist filtered out,
            // and they are far more useful than the long tail of user tags ("2026年7月", "神作").
            var tags = new List<string>(MetaTagsOf(subject));
            var limit = Math.Max(0, options.MaxTags);
            foreach (var tag in subject.Tags
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name))
                .OrderByDescending(t => t.Count)
                .Select(t => t.Name.Trim())
                .Distinct(StringComparer.Ordinal))
            {
                if (tags.Count >= limit) break;
                if (!tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
            }

            if (subject.Nsfw && !tags.Contains("NSFW", StringComparer.Ordinal)) tags.Add("NSFW");
            return tags.ToArray();
        }

        private static string[] BuildStudios(BangumiSubject subject)
        {
            var studios = CollectStudios(subject, StudioKeys);
            if (studios.Count == 0) studios = CollectStudios(subject, StudioFallbackKeys);
            if (studios.Count > MaxStudios) studios = studios.GetRange(0, MaxStudios);
            return studios.ToArray();
        }

        private const int MaxStudios = 8;

        private static List<string> CollectStudios(BangumiSubject subject, string[] keys)
        {
            var studios = new List<string>();
            foreach (var key in keys)
            {
                foreach (var value in subject.InfoboxValues(key))
                {
                    foreach (var piece in value.Split(StudioSeparators, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = piece.Trim();
                        if (trimmed.Length == 0 || trimmed.Length > 60) continue;
                        if (!studios.Contains(trimmed)) studios.Add(trimmed);
                    }
                }
            }

            return studios;
        }

        /// <summary>Air day / air time, only meaningful on a series.</summary>
        protected static void ApplyAirSchedule(BangumiSubject subject, out DayOfWeek[] airDays, out string airTime)
        {
            airDays = null;
            airTime = null;
            if (subject == null) return;

            foreach (var key in new[] { "放送星期", "播放星期", "放送日" })
            {
                foreach (var value in subject.InfoboxValues(key))
                {
                    foreach (var pair in WeekdayMap)
                    {
                        if (value.IndexOf(pair.Key, StringComparison.Ordinal) < 0) continue;
                        airDays = new[] { pair.Value };
                        break;
                    }

                    var time = Regex.Match(TitleNormalizer.ToHalfWidth(value), @"(\d{1,2}):(\d{2})");
                    if (time.Success) airTime = time.Value;
                    if (airDays != null) return;
                }
            }
        }

        // ---------------------------------------------------------------- people

        protected async Task ApplyPeopleAsync(
            BaseMetadataResult result, int subjectId, PluginOptions options, CancellationToken cancellationToken)
        {
            if (result == null || subjectId <= 0) return;
            if (!options.ImportStaff && !options.ImportVoiceActors) return;

            var budget = Math.Max(0, options.MaxPersons);
            if (budget == 0) return;

            var people = new List<PersonInfo>();

            if (options.ImportVoiceActors)
            {
                var characters = await Api.GetSubjectCharactersAsync(subjectId, cancellationToken).ConfigureAwait(false);
                if (characters != null)
                {
                    // 主角 before 配角 before 客串 so that truncation keeps the important cast.
                    foreach (var character in characters.OrderBy(c => RelationRank(c == null ? null : c.Relation)))
                    {
                        if (character == null || character.Actors == null) continue;
                        foreach (var actor in character.Actors)
                        {
                            if (actor == null || string.IsNullOrWhiteSpace(actor.Name)) continue;

                            var person = new PersonInfo
                            {
                                Name = actor.Name.Trim(),
                                Type = PersonType.Actor,
                                Role = string.IsNullOrWhiteSpace(character.Name) ? null : character.Name.Trim(),
                                ImageUrl = actor.Images == null ? null : actor.Images.Thumbnail(),
                            };

                            if (actor.Id > 0)
                            {
                                person.ProviderIds[BangumiConstants.PersonProviderId] =
                                    actor.Id.ToString(CultureInfo.InvariantCulture);
                            }

                            people.Add(person);
                        }
                    }
                }
            }

            if (options.ImportStaff)
            {
                var staff = await Api.GetSubjectPersonsAsync(subjectId, cancellationToken).ConfigureAwait(false);
                if (staff != null)
                {
                    foreach (var member in staff)
                    {
                        // type 2 / 3 are companies and bands; those belong in Studios, not the cast list.
                        if (member == null || member.Type != 1) continue;
                        if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.Relation)) continue;

                        PersonType type;
                        if (!StaffRoleMap.TryGetValue(member.Relation.Trim(), out type)) continue;

                        var person = new PersonInfo
                        {
                            Name = member.Name.Trim(),
                            Type = type,
                            Role = member.Relation.Trim(),
                            ImageUrl = member.Images == null ? null : member.Images.Thumbnail(),
                        };

                        if (member.Id > 0)
                        {
                            person.ProviderIds[BangumiConstants.PersonProviderId] =
                                member.Id.ToString(CultureInfo.InvariantCulture);
                        }

                        people.Add(person);
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            result.ResetPeople();
            foreach (var person in people)
            {
                if (added >= budget) break;
                var key = person.Type + "|" + person.Name + "|" + (person.Role ?? string.Empty);
                if (!seen.Add(key)) continue;
                result.AddPerson(person);
                added++;
            }

            Verbose("Bangumi subject {0}: attached {1} of {2} people", subjectId, added, people.Count);
        }

        private static int RelationRank(string relation)
        {
            if (string.Equals(relation, "主角", StringComparison.Ordinal)) return 0;
            if (string.Equals(relation, "配角", StringComparison.Ordinal)) return 1;
            return 2;
        }

        // ---------------------------------------------------------------- images

        /// <summary>
        /// Shared by every provider: Emby calls this to actually download an image or a search
        /// thumbnail. The response message is handed to <see cref="HttpResponseInfo"/> as a
        /// disposable so the socket is released together with the stream.
        /// </summary>
        public async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var response = await Api.GetRawAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && Logger != null)
            {
                Logger.Warn("Bangumi image request failed with {0}: {1}", (int)response.StatusCode, url);
            }

            var info = new HttpResponseInfo(new IDisposable[] { response })
            {
                StatusCode = response.StatusCode,
                ResponseUrl = url,
                ContentType = response.Content != null && response.Content.Headers.ContentType != null
                    ? response.Content.Headers.ContentType.ToString()
                    : null,
                ContentLength = response.Content == null ? null : response.Content.Headers.ContentLength,
            };

            if (response.Content != null)
            {
                info.Content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            }

            return info;
        }
    }
}