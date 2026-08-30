using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        // Bangumi files a few company credits under person type 1, so KADOKAWA turns up in
        // /v0/subjects/{id}/persons next to the actual staff. These belong in Studios, which
        // CollectStudios already fills from the infobox, so they never become people.
        private static readonly HashSet<string> CompanyRelations =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "製作",
                "制作",
                "动画制作",
                "動画制作",
                "音乐制作",
                "音楽制作",
                "出品方",
                "联合制作",
                "製作協力",
                "制作协力",
            };

        private static readonly string[] AliasKeys = { "别名", "別名", "英文名", "罗马字", "中文名" };

        /// <summary>
        /// Title similarity that is trusted on its own, with no other signal agreeing. Anything
        /// below this needs the air year to line up before an unattended refresh may write it.
        /// </summary>
        private const int StrongTitleScore = 550;

        /// <summary>Extensions considered a video file when mining a folder for title hints.</summary>
        private static readonly string[] MediaExtensions =
            { ".mkv", ".mp4", ".m2ts", ".ts", ".avi", ".mov", ".flv", ".rmvb", ".webm", ".wmv", ".iso" };

        /// <summary>
        /// Bangumi job title -> Emby person type, for the jobs that have an exact counterpart.
        /// Everything else (作画监督, 人物设定, 色彩设计, 摄影监督, 音响监督, 原画 ...) is handled by
        /// <see cref="PluginOptions.UnmappedStaff"/> so that no credit is silently lost.
        /// </summary>
        private static readonly Dictionary<string, PersonType> StaffRoleMap =
            new Dictionary<string, PersonType>(StringComparer.Ordinal)
            {
                { "导演", PersonType.Director },
                { "总导演", PersonType.Director },
                { "监督", PersonType.Director },
                { "総監督", PersonType.Director },
                { "总监督", PersonType.Director },
                { "副导演", PersonType.Director },
                { "系列导演", PersonType.Director },
                { "助监督", PersonType.Director },
                { "演出", PersonType.Director },
                { "分镜", PersonType.Director },
                { "絵コンテ", PersonType.Director },
                { "CG导演", PersonType.Director },
                { "CG 导演", PersonType.Director },
                { "3DCG导演", PersonType.Director },

                { "脚本", PersonType.Writer },
                { "系列构成", PersonType.Writer },
                { "シリーズ構成", PersonType.Writer },
                { "原作", PersonType.Writer },
                { "剧本", PersonType.Writer },
                { "原案", PersonType.Writer },
                { "构成", PersonType.Writer },
                { "脚本协力", PersonType.Writer },
                { "原作插画", PersonType.Writer },

                { "音乐", PersonType.Composer },
                { "音楽", PersonType.Composer },
                { "主题歌作曲", PersonType.Composer },
                { "插入歌作曲", PersonType.Composer },
                { "歌曲编曲", PersonType.Composer },
                { "主题歌编曲", PersonType.Composer },
                { "插入歌编曲", PersonType.Composer },
                { "音乐制作", PersonType.Composer },
                { "音乐监督", PersonType.Composer },
                { "劇伴", PersonType.Composer },

                { "主题歌作词", PersonType.Lyricist },
                { "插入歌作词", PersonType.Lyricist },
                { "作词", PersonType.Lyricist },

                { "指挥", PersonType.Conductor },
                { "指揮", PersonType.Conductor },

                { "制片人", PersonType.Producer },
                { "动画制片人", PersonType.Producer },
                { "助理制片人", PersonType.Producer },
                { "宣传制片人", PersonType.Producer },
                { "企画", PersonType.Producer },
                { "企划", PersonType.Producer },
                { "製作", PersonType.Producer },
                { "製作総指揮", PersonType.Producer },
                { "执行制片人", PersonType.Producer },
                { "制作总指挥", PersonType.Producer },
                { "プロデューサー", PersonType.Producer },
                { "音响制作", PersonType.Producer },
                { "制作担当", PersonType.Producer },
                { "制作管理", PersonType.Producer },
                { "设定制作", PersonType.Producer },
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
        protected async Task<List<BangumiSubject>> SearchAsync(
            string rawTitle, int subjectType, int? year, CancellationToken cancellationToken)
        {
            var outcome = await SearchDetailedAsync(rawTitle, null, subjectType, year, cancellationToken)
                .ConfigureAwait(false);
            return outcome.Ranked;
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
            var outcome = await SearchDetailedAsync(rawTitle, pathHint, subjectType, year, cancellationToken)
                .ConfigureAwait(false);
            return outcome.Ranked;
        }

        /// <summary>
        /// Everything <see cref="PickAutoMatch"/> needs to decide whether the best candidate is
        /// good enough to write without a human looking at it.
        /// </summary>
        protected sealed class SearchOutcome
        {
            public SearchOutcome()
            {
                Ranked = new List<BangumiSubject>();
            }

            /// <summary>All candidates seen, best first. Never filtered.</summary>
            public List<BangumiSubject> Ranked { get; set; }

            /// <summary>Title closeness of <see cref="Ranked"/>[0] in 0..1000.</summary>
            public int TitleScore { get; set; }

            /// <summary>True when <see cref="Ranked"/>[0] aired in the year the caller asked for.</summary>
            public bool YearMatched { get; set; }
        }

        /// <summary>
        /// Runs every keyword the item can offer and ranks the union of the results.
        ///
        /// Searching until the first keyword returns something is not enough: Bangumi almost always
        /// returns something, just not the right thing. A library where TMDB has already renamed the
        /// series to its Chinese release title hands over "罪恶之渊", which matches a 2013 show about
        /// scissors, while "ギルティホール" sitting in the very same item's original-title field is an
        /// exact hit. So all keywords are tried and pooled, and the loop only stops early once a
        /// candidate matches a title outright, which keeps the common case at one request.
        /// </summary>
        protected async Task<SearchOutcome> SearchDetailedAsync(
            string rawTitle, string pathHint, int subjectType, int? year, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var titles = BuildTitleCandidates(rawTitle, pathHint, LocalTitleHints(pathHint, options));
            var outcome = new SearchOutcome();
            if (titles.Count == 0) return outcome;

            var keywords = new List<string>();
            foreach (var keyword in EnumerateKeywords(titles))
            {
                if (string.IsNullOrWhiteSpace(keyword)) continue;
                if (keywords.Any(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase))) continue;
                keywords.Add(keyword);
            }

            var pool = new List<BangumiSubject>();
            var seen = new HashSet<int>();

            await SearchPassAsync(keywords, titles, year, subjectType, false, options, pool, seen, outcome, cancellationToken)
                .ConfigureAwait(false);

            // A weak winner here does not mean Bangumi lacks the show. The v0 index has holes -
            // "ギルティホール" and "ハーレムきゃんぷっ！" return no trace of the anime they name - while it
            // still answers with *something* for any input, so the miss looks like a bad match rather
            // than a missing entry. The legacy index has both as its first hit, hence a second pass.
            if (options.UseLegacySearchFallback && outcome.TitleScore < StrongTitleScore)
            {
                await SearchPassAsync(keywords, titles, year, subjectType, true, options, pool, seen, outcome, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (outcome.Ranked.Count == 0 && pool.Count > 0) Rank(pool, titles, year, outcome);
            return outcome;
        }

        /// <summary>
        /// Adds every hit of one search endpoint to <paramref name="pool"/> and re-ranks after each
        /// keyword, stopping as soon as a candidate matches a title outright.
        /// </summary>
        private async Task SearchPassAsync(
            List<string> keywords, List<NormalizedTitle> titles, int? year, int subjectType, bool legacy,
            PluginOptions options, List<BangumiSubject> pool, HashSet<int> seen, SearchOutcome outcome,
            CancellationToken cancellationToken)
        {
            foreach (var keyword in keywords)
            {
                var found = legacy
                    ? await Api.SearchSubjectsLegacyAsync(keyword, subjectType, options.SearchResultLimit, cancellationToken)
                        .ConfigureAwait(false)
                    : await Api.SearchSubjectsAsync(keyword, subjectType, options.SearchResultLimit, cancellationToken)
                        .ConfigureAwait(false);

                var added = 0;
                if (found != null)
                {
                    foreach (var subject in found)
                    {
                        if (subject == null || subject.Id <= 0) continue;
                        if (!seen.Add(subject.Id)) continue;
                        pool.Add(subject);
                        added++;
                    }
                }

                Verbose(
                    "Bangumi {0} search \"{1}\" -> {2} candidate(s), {3} new", legacy ? "legacy" : "v0", keyword,
                    found == null ? 0 : found.Count, added);

                if (added == 0) continue;

                Rank(pool, titles, year, outcome);
                if (outcome.TitleScore >= StrongTitleScore) return;
            }
        }

        /// <summary>
        /// The complete subject behind a candidate.
        ///
        /// The legacy search index answers without <c>infobox</c>, <c>tags</c>, <c>meta_tags</c> or
        /// <c>platform</c>, so writing one of its hits straight to an item would silently drop the
        /// studios, the genres and every tag. <see cref="Api"/> caches, so the extra call costs one
        /// request per newly matched item at most.
        /// </summary>
        protected async Task<BangumiSubject> HydrateAsync(BangumiSubject subject, CancellationToken cancellationToken)
        {
            if (subject == null || subject.Id <= 0) return subject;
            if (subject.Infobox != null && subject.Infobox.Count > 0) return subject;

            var full = await Api.GetSubjectAsync(subject.Id, cancellationToken).ConfigureAwait(false);
            if (full == null) return subject;

            Verbose("Bangumi hydrated partial subject {0} ({1})", subject.Id, full.Name);
            return full;
        }

        /// <summary>
        /// The candidate an unattended refresh is allowed to write, or null when nothing is close
        /// enough. Manual identification is untouched: the search UI still lists every candidate.
        ///
        /// A wrong subject id is far more expensive than a missing one, because it does not stop at
        /// the title - artwork, episode list, studios and every credited person come from that
        /// subject too, and the mistake is invisible until someone opens the series.
        /// </summary>
        protected BangumiSubject PickAutoMatch(SearchOutcome outcome, string queriedName, PluginOptions options)
        {
            if (outcome == null) return null;

            var best = outcome.Ranked.FirstOrDefault();
            if (best == null) return null;

            var floor = options.MinTitleMatchScore;
            if (floor <= 0) return best;
            if (outcome.TitleScore >= StrongTitleScore) return best;
            if (outcome.TitleScore >= floor && outcome.YearMatched) return best;

            if (Logger != null)
            {
                Logger.Info(
                    "Bangumi refused to auto-match \"{0}\": best candidate {1} \"{2}\" only scores {3}/1000 on the " +
                    "title (floor {4}, air year agrees: {5}). Use 「识别」 to pick it manually, or lower " +
                    "「标题匹配最低分」.",
                    queriedName,
                    best.Id,
                    string.IsNullOrWhiteSpace(best.NameCn) ? best.Name : best.NameCn,
                    outcome.TitleScore,
                    floor,
                    outcome.YearMatched);
            }

            return null;
        }

        /// <summary>
        /// Title sources that only exist on this machine: the original-language title another
        /// scraper already stored on the item, and the names of the media files in the folder.
        ///
        /// Emby hands a provider the display name and the path, nothing else. The Japanese title is
        /// what Bangumi indexes best, and it is usually sitting right there in the item's
        /// 「原始标题」 field; the file names carry the same information for libraries whose folders
        /// are named in Chinese but whose releases are not ("Harem Camp! - 03.5.mkv").
        /// </summary>
        private List<string> LocalTitleHints(string pathHint, PluginOptions options)
        {
            var hints = new List<string>();
            if (string.IsNullOrWhiteSpace(pathHint)) return hints;

            if (options.UseOriginalTitleHint)
            {
                try
                {
                    var library = Plugin.TryLibraryManager();
                    if (library != null)
                    {
                        var item = library.FindByPath(pathHint, null);
                        if (item != null && !string.IsNullOrWhiteSpace(item.OriginalTitle))
                        {
                            hints.Add(item.OriginalTitle);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Logger != null)
                    {
                        Logger.ErrorException("Bangumi could not read the stored original title of {0}", ex, pathHint);
                    }
                }
            }

            if (options.MaxFileNameHints > 0)
            {
                hints.AddRange(FileNameHints(pathHint, options.MaxFileNameHints));
            }

            if (hints.Count > 0) Verbose("Bangumi local title hints for {0}: {1}", pathHint, string.Join(" | ", hints));
            return hints;
        }

        /// <summary>
        /// Distinct title stems of the media files under <paramref name="path"/>, most frequent
        /// first. A season folder repeats the same stem once per episode, so frequency is a good
        /// proxy for "this is the show's name".
        /// </summary>
        private List<string> FileNameHints(string path, int limit)
        {
            var hints = new List<string>();

            try
            {
                if (!Directory.Exists(path)) return hints;

                var counted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();

                foreach (var file in EnumerateMediaFiles(path))
                {
                    // Deliberately the season-stripped keyword: a series folder that holds
                    // several season subfolders would otherwise vote for whichever season has the
                    // most files, and an exact hit on "<show> 2nd Season" scores 1000 and wins.
                    // The Emby item being scraped here is the show, not one of its seasons, and
                    // BangumiSeasonProvider resolves seasons from the sequel chain afterwards.
                    var normalized = TitleNormalizer.NormalizeFileName(Path.GetFileName(file));
                    var keyword = normalized == null ? null : normalized.Keyword;
                    if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2) continue;

                    int count;
                    if (counted.TryGetValue(keyword, out count))
                    {
                        counted[keyword] = count + 1;
                    }
                    else
                    {
                        counted[keyword] = 1;
                        order.Add(keyword);
                    }
                }

                foreach (var keyword in order.OrderByDescending(k => counted[k]).Take(limit))
                {
                    hints.Add(keyword);
                }
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.ErrorException("Bangumi could not list media files under {0}", ex, path);
            }

            return hints;
        }

        /// <summary>The folder itself, then one level of subfolders, capped so a huge library folder cannot stall a refresh.</summary>
        private static IEnumerable<string> EnumerateMediaFiles(string path)
        {
            const int cap = 200;
            var yielded = 0;

            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (!IsMediaFile(file)) continue;
                yield return file;
                if (++yielded >= cap) yield break;
            }

            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (!IsMediaFile(file)) continue;
                    yield return file;
                    if (++yielded >= cap) yield break;
                }
            }
        }

        private static bool IsMediaFile(string file)
        {
            var extension = Path.GetExtension(file);
            if (string.IsNullOrEmpty(extension)) return false;

            foreach (var candidate in MediaExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// The item name first, then the folder name when it normalises to something different,
        /// then whatever the machine itself could tell us (<paramref name="extras"/>).
        ///
        /// Order is the search order, and it is deliberate: the name the user sees is tried first so
        /// a correctly named library still costs a single request, and the guessed titles only get a
        /// turn after that.
        /// </summary>
        private static List<NormalizedTitle> BuildTitleCandidates(string rawTitle, string pathHint, List<string> extras)
        {
            var titles = new List<NormalizedTitle>();

            var sources = new List<string> { rawTitle, FolderNameOf(pathHint) };
            if (extras != null) sources.AddRange(extras);

            foreach (var source in sources)
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

        /// <summary>
        /// Sorts candidates best-first without dropping any of them, and records how well the
        /// winner actually matched so <see cref="PickAutoMatch"/> can refuse a bad one.
        /// </summary>
        private void Rank(List<BangumiSubject> candidates, List<NormalizedTitle> titles, int? year, SearchOutcome outcome)
        {
            outcome.Ranked = new List<BangumiSubject>();
            outcome.TitleScore = 0;
            outcome.YearMatched = false;

            if (candidates == null || candidates.Count == 0) return;

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
                .Select((subject, index) =>
                {
                    int titleScore;
                    bool yearMatched;
                    var score = Score(subject, titles, year, exactSeasonAvailable, out titleScore, out yearMatched);
                    return new { subject, index, score, titleScore, yearMatched };
                })
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.index)
                .ToList();

            if (options.EnableVerboseLogging && Logger != null)
            {
                foreach (var entry in scored.Take(8))
                {
                    Logger.Info(
                        "Bangumi candidate score={0} title={1} year_ok={2} id={3} name={4} name_cn={5} date={6}",
                        entry.score, entry.titleScore, entry.yearMatched, entry.subject.Id, entry.subject.Name,
                        entry.subject.NameCn, entry.subject.Date);
                }
            }

            outcome.Ranked = scored.Select(x => x.subject).ToList();

            var winner = scored.FirstOrDefault();
            if (winner != null)
            {
                outcome.TitleScore = winner.titleScore;
                outcome.YearMatched = winner.yearMatched;
            }
        }

        /// <summary>
        /// Overall desirability of a candidate. <paramref name="titleScore"/> and
        /// <paramref name="yearMatched"/> are reported separately because the total mixes in
        /// popularity and platform nudges, which say nothing about whether this is the right show.
        /// </summary>
        private int Score(
            BangumiSubject subject, List<NormalizedTitle> queries, int? year, bool exactSeasonAvailable,
            out int titleScore, out bool yearMatched)
        {
            titleScore = 0;
            yearMatched = false;

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

            titleScore = best;
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
                if (delta == 0)
                {
                    score += 120;
                    yearMatched = true;
                }
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

        /// <summary>
        /// Fills in cast and crew. Bangumi splits the two across separate endpoints:
        /// <c>/characters</c> gives characters with their voice actors, <c>/persons</c> gives the
        /// production staff with a free-text job title. Both are ordered by importance here so
        /// that hitting a cap truncates 龙套 / 原画 rather than the 监督.
        /// </summary>
        protected async Task ApplyPeopleAsync(
            BaseMetadataResult result, int subjectId, PluginOptions options, CancellationToken cancellationToken)
        {
            if (result == null || subjectId <= 0) return;
            if (!options.ImportStaff && !options.ImportVoiceActors) return;

            var totalBudget = Math.Max(0, options.MaxPersons);
            if (totalBudget == 0) return;

            // Chinese names live in per-entity infobox entries, i.e. one extra request each.
            // A single shared budget keeps a full library scan from exploding into thousands of calls.
            var lookups = new LookupBudget(Math.Max(0, options.MaxDetailLookups));

            // Lets the crew pass recognise "this is already in the cast", so a seiyuu who also
            // sang the theme song stays one row instead of turning up twice.
            var castKeys = new HashSet<string>(StringComparer.Ordinal);

            var cast = options.ImportVoiceActors
                ? await BuildCastAsync(subjectId, options, castKeys, lookups, cancellationToken).ConfigureAwait(false)
                : new List<PersonInfo>();

            var crew = options.ImportStaff
                ? await BuildCrewAsync(subjectId, options, castKeys, lookups, cancellationToken).ConfigureAwait(false)
                : new List<PersonInfo>();

            result.ResetPeople();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            var castAdded = Emit(result, cast, Math.Max(0, options.MaxVoiceActors), totalBudget, seen, ref added);
            var crewAdded = Emit(result, crew, Math.Max(0, options.MaxStaff), totalBudget, seen, ref added);

            Verbose(
                "Bangumi subject {0}: attached {1} voice actors (of {2}) and {3} staff (of {4}), {5} name lookups spent",
                subjectId, castAdded, cast.Count, crewAdded, crew.Count, lookups.Spent);
        }

        private static int Emit(
            BaseMetadataResult result,
            List<PersonInfo> people,
            int cap,
            int totalBudget,
            HashSet<string> seen,
            ref int added)
        {
            var emitted = 0;
            foreach (var person in people)
            {
                if (emitted >= cap || added >= totalBudget) break;

                var key = ((int)person.Type).ToString(CultureInfo.InvariantCulture) + "|" + person.Name;
                if (!seen.Add(key)) continue;

                result.AddPerson(person);
                emitted++;
                added++;
            }

            return emitted;
        }

        private async Task<List<PersonInfo>> BuildCastAsync(
            int subjectId,
            PluginOptions options,
            HashSet<string> castKeys,
            LookupBudget lookups,
            CancellationToken cancellationToken)
        {
            var cast = new List<PersonInfo>();
            var characters = await Api.GetSubjectCharactersAsync(subjectId, cancellationToken).ConfigureAwait(false);
            if (characters == null) return cast;

            // OrderBy is a stable sort, so 主角 before 配角 before 客串 while keeping Bangumi's
            // own order (which is roughly billing order) inside each group.
            var ordered = characters
                .Where(c => c != null && c.Actors != null && c.Actors.Count > 0)
                .OrderBy(c => RelationRank(c.Relation))
                .ToList();

            var index = new Dictionary<string, PersonInfo>(StringComparer.Ordinal);

            foreach (var character in ordered)
            {
                var role = await ResolveCharacterNameAsync(character, options, lookups, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var actor in character.Actors)
                {
                    if (actor == null || string.IsNullOrWhiteSpace(actor.Name)) continue;

                    var name = await ResolvePersonNameAsync(actor.Id, actor.Name, options, lookups, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var key = actor.Id > 0
                        ? "id:" + actor.Id.ToString(CultureInfo.InvariantCulture)
                        : "name:" + name;

                    PersonInfo existing;
                    if (options.MergeMultiRoleActors && index.TryGetValue(key, out existing))
                    {
                        // 一人分饰多角: keep one entry whose role reads "角色A / 角色B".
                        existing.Role = MergeRole(existing.Role, role);
                        continue;
                    }

                    var person = new PersonInfo
                    {
                        Name = name,
                        Type = PersonType.Actor,
                        Role = role,
                        ImageUrl = actor.Images == null ? null : actor.Images.Thumbnail(),
                    };

                    if (actor.Id > 0)
                    {
                        person.ProviderIds[BangumiConstants.PersonProviderId] =
                            actor.Id.ToString(CultureInfo.InvariantCulture);
                    }

                    if (!index.ContainsKey(key)) index[key] = person;
                    castKeys.Add(key);
                    cast.Add(person);
                }
            }

            return cast;
        }

        private async Task<List<PersonInfo>> BuildCrewAsync(
            int subjectId,
            PluginOptions options,
            HashSet<string> castKeys,
            LookupBudget lookups,
            CancellationToken cancellationToken)
        {
            var crew = new List<PersonInfo>();
            var staff = await Api.GetSubjectPersonsAsync(subjectId, cancellationToken).ConfigureAwait(false);
            if (staff == null) return crew;

            var blocked = ParseCsvSet(options.StaffRelationBlocklist);
            var ranked = new List<CrewEntry>();
            var index = new Dictionary<string, CrewEntry>(StringComparer.Ordinal);
            var sequence = 0;

            foreach (var member in staff)
            {
                // type 2 / 3 are companies and bands; those belong in Studios, not the cast list.
                if (member == null || member.Type != 1) continue;
                if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.Relation)) continue;

                var relation = member.Relation.Trim();
                if (blocked.Contains(relation) || CompanyRelations.Contains(relation)) continue;

                PersonType type;
                int priority;
                var mapped = StaffRoleMap.TryGetValue(relation, out type);
                if (mapped)
                {
                    priority = PriorityOf(type);
                }
                else
                {
                    switch (options.UnmappedStaff)
                    {
                        case UnmappedStaffMode.Producer:
                            type = PersonType.Producer;
                            break;
                        case UnmappedStaffMode.GuestStar:
                            type = PersonType.GuestStar;
                            break;
                        default:
                            continue;
                    }

                    // Behind every exactly mapped job, and within that tail the creative leads
                    // (総作画監督, 音響監督, ...) rank above the long 原画 / 协力 list, so a cap trims
                    // the least interesting credits first instead of Bangumi's arbitrary order.
                    priority = UnmappedPriority + UnmappedRankOf(relation);
                }

                var name = await ResolvePersonNameAsync(member.Id, member.Name, options, lookups, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var key = member.Id > 0
                    ? "id:" + member.Id.ToString(CultureInfo.InvariantCulture)
                    : "name:" + name;

                // Only a genuinely different credit (監督, 脚本, 音楽, ...) earns a second row next to
                // the person's Actor entry; a stray 主题歌演出 would just be noise.
                if (!mapped && castKeys.Contains(key)) continue;

                CrewEntry existing;
                if (index.TryGetValue(key, out existing))
                {
                    // One person, several jobs (監督 + 絵コンテ, 作画监督 + 人物设定). Emby has a single
                    // type per person per item, so the highest ranking job wins it and the remaining
                    // jobs are folded into the role text.
                    if (priority < existing.Priority)
                    {
                        existing.Priority = priority;
                        existing.Person.Type = type;
                        existing.Person.Role = MergeRole(relation, existing.Person.Role);
                    }
                    else
                    {
                        existing.Person.Role = MergeRole(existing.Person.Role, relation);
                    }

                    if (string.IsNullOrEmpty(existing.Person.ImageUrl) && member.Images != null)
                    {
                        existing.Person.ImageUrl = member.Images.Thumbnail();
                    }

                    continue;
                }

                var person = new PersonInfo
                {
                    Name = name,
                    Type = type,
                    Role = relation,
                    ImageUrl = member.Images == null ? null : member.Images.Thumbnail(),
                };

                if (member.Id > 0)
                {
                    person.ProviderIds[BangumiConstants.PersonProviderId] =
                        member.Id.ToString(CultureInfo.InvariantCulture);
                }

                var entry = new CrewEntry
                {
                    Priority = priority,
                    Sequence = sequence++,
                    Person = person,
                };

                index[key] = entry;
                ranked.Add(entry);
            }

            // Priority first, then Bangumi's own order, which is roughly billing order inside a job.
            foreach (var entry in ranked.OrderBy(e => e.Priority).ThenBy(e => e.Sequence))
            {
                crew.Add(entry.Person);
            }

            return crew;
        }

        /// <summary>A crew credit while it is still being merged; <see cref="PersonInfo"/> has no rank field.</summary>
        private sealed class CrewEntry
        {
            public int Priority;

            public int Sequence;

            public PersonInfo Person;
        }

        private const int UnmappedPriority = 90;

        private const int DefaultUnmappedRank = 6;

        /// <summary>
        /// Emby only knows eight <see cref="PersonType"/> values, so every other Bangumi job lands in
        /// one bucket. This orders that bucket by how much a viewer cares, which is what a cap cuts on.
        /// </summary>
        private static readonly Dictionary<string, int> UnmappedRanks =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "副导演", 0 },
                { "副監督", 0 },
                { "总作画监督", 0 },
                { "総作画監督", 0 },
                { "人物设定", 0 },
                { "人物設定", 0 },
                { "角色设计", 0 },
                { "音响监督", 1 },
                { "音響監督", 1 },
                { "美术监督", 1 },
                { "美術監督", 1 },
                { "摄影监督", 1 },
                { "撮影監督", 1 },
                { "色彩设计", 1 },
                { "色彩設計", 1 },
                { "剪辑", 1 },
                { "編集", 1 },
                { "作画监督", 2 },
                { "作畫監督", 2 },
                { "机械设定", 2 },
                { "美术设计", 2 },
                { "美術設定", 2 },
                { "道具设计", 2 },
                { "3DCG", 2 },
                { "特效", 3 },
                { "监修", 3 },
                { "设定考证", 3 },
                { "設定考証", 3 },
                { "主动画师", 4 },
                { "音效", 4 },
                { "录音", 4 },
                { "摄影", 5 },
                { "背景美术", 5 },
                { "原画", 7 },
                { "第二原画", 8 },
                { "动画", 8 },
                { "動画", 8 },
                { "在线剪辑", 8 },
                { "制作管理", 8 },
                { "制作进行", 9 },
                { "协力", 9 },
                { "企画协力", 9 },
                { "特别鸣谢", 9 },
            };

        private static int UnmappedRankOf(string relation)
        {
            int rank;
            return UnmappedRanks.TryGetValue(relation, out rank) ? rank : DefaultUnmappedRank;
        }

        private static int PriorityOf(PersonType type)
        {
            switch (type)
            {
                case PersonType.Director: return 0;
                case PersonType.Writer: return 10;
                case PersonType.Composer: return 20;
                case PersonType.Lyricist: return 30;
                case PersonType.Conductor: return 40;
                case PersonType.Producer: return 50;
                case PersonType.GuestStar: return 60;
                default: return 70;
            }
        }

        /// <summary>
        /// Joins two credits into one "A / B / C" string. Either side may already be a merged list, so
        /// both are split again: the result stays duplicate free whichever way round it is called.
        /// </summary>
        private static string MergeRole(string existing, string addition)
        {
            if (string.IsNullOrWhiteSpace(addition)) return existing;
            if (string.IsNullOrWhiteSpace(existing)) return addition.Trim();

            var parts = new List<string>();
            AppendRoleParts(parts, existing);
            var before = parts.Count;
            AppendRoleParts(parts, addition);
            if (parts.Count == before) return existing;

            // Emby stores the role in a single column that the web client shows on one line.
            var text = new StringBuilder();
            foreach (var part in parts)
            {
                var cost = text.Length == 0 ? part.Length : part.Length + 3;
                if (text.Length + cost > MaxRoleLength) break;
                if (text.Length > 0) text.Append(" / ");
                text.Append(part);
            }

            return text.Length == 0 ? existing : text.ToString();
        }

        private static void AppendRoleParts(List<string> parts, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            foreach (var raw in text.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (parts.Contains(part, StringComparer.Ordinal)) continue;
                parts.Add(part);
            }
        }

        private const int MaxRoleLength = 160;

        /// <summary>
        /// The subject-level character list carries the Japanese name only; the Chinese rendering
        /// is an infobox entry on the character itself, hence the optional extra request.
        /// </summary>
        private async Task<string> ResolveCharacterNameAsync(
            BangumiRelatedCharacter character,
            PluginOptions options,
            LookupBudget lookups,
            CancellationToken cancellationToken)
        {
            var fallback = character == null || string.IsNullOrWhiteSpace(character.Name)
                ? null
                : character.Name.Trim();

            if (character == null || character.Id <= 0) return fallback;
            if (!options.TranslateCharacterNames || !lookups.Take()) return fallback;

            var detail = await Api.GetCharacterAsync(character.Id, cancellationToken).ConfigureAwait(false);
            if (detail == null) return fallback;

            var chinese = detail.ChineseName();
            return string.IsNullOrWhiteSpace(chinese) ? fallback : chinese.Trim();
        }

        private async Task<string> ResolvePersonNameAsync(
            int personId,
            string rawName,
            PluginOptions options,
            LookupBudget lookups,
            CancellationToken cancellationToken)
        {
            var fallback = string.IsNullOrWhiteSpace(rawName) ? null : rawName.Trim();

            if (personId <= 0) return fallback;
            if (!options.TranslatePersonNames || !lookups.Take()) return fallback;

            var detail = await Api.GetPersonAsync(personId, cancellationToken).ConfigureAwait(false);
            if (detail == null) return fallback;

            var chinese = detail.ChineseName();
            return string.IsNullOrWhiteSpace(chinese) ? fallback : chinese.Trim();
        }

        /// <summary>A plain counter; async methods cannot take a <c>ref int</c>.</summary>
        private sealed class LookupBudget
        {
            private int _left;

            public LookupBudget(int budget)
            {
                _left = budget;
            }

            public int Spent { get; private set; }

            public bool Take()
            {
                if (_left <= 0) return false;
                _left--;
                Spent++;
                return true;
            }
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