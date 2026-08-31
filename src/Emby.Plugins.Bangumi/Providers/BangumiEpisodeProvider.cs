using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using Emby.Plugins.Bangumi.Utils;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>
    /// Episode titles, air dates and synopses.
    ///
    /// Two numbers exist per Bangumi episode: <c>ep</c> counts inside the current subject and
    /// <c>sort</c> counts across the whole franchise (Re:Zero season 4 episode 1 is sort 78). Which
    /// one lines up with the file name depends entirely on how the release group numbered the files,
    /// so the matching strategy is user selectable and defaults to trying both.
    ///
    /// A season can also span several subjects, because Emby keeps a 25 episode season in one folder
    /// while Bangumi files it as 13 + 12. Candidates are therefore a short list, searched in air
    /// order, and the first subject that owns the requested number wins.
    /// </summary>
    public class BangumiEpisodeProvider : BangumiProviderBase,
        IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
    {
        public BangumiEpisodeProvider(ILogManager logManager) : base(logManager)
        {
        }

        /// <summary>
        /// Smallest season or episode number that can only have come from a date or a resolution.
        /// The longest running anime sit in the hundreds of episodes and nothing has ever had a four
        /// digit season, so 1000 separates "wrong" from "impossible" with room to spare.
        /// </summary>
        private const int ImplausibleNumberFloor = 1000;

        /// <summary>
        /// Emby never searches for episodes remotely by name; identification happens through the
        /// parent series, so an empty list is the correct answer here.
        /// </summary>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());
        }

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var result = new MetadataResult<Episode>
            {
                Item = new Episode(),
                HasMetadata = false,
                Provider = BangumiConstants.PluginName,
                ResultLanguage = options.PreferChineseTitle ? "zh" : "ja",
            };

            if (info == null) return result;

            // Numbering is echoed straight back, and that is a hard requirement rather than a
            // courtesy. Emby's MergeBaseItemData copies IndexNumber unconditionally once the refresh
            // asked to replace all metadata, so a provider that leaves the field at its default null
            // does not "keep the file name authority" - it erases the numbering the resolver derived
            // from the file. Losing ParentIndexNumber is the expensive half: every episode falls out
            // of its season and Emby invents a fresh "Season 1" per file. Copying the incoming values
            // makes the merge a no-op for these three fields whichever refresh mode ran.
            result.Item.IndexNumber = info.IndexNumber;
            result.Item.ParentIndexNumber = info.ParentIndexNumber;
            result.Item.IndexNumberEnd = info.IndexNumberEnd;

            // ... unless the resolver produced no number at all. Some bracket-only release names
            // ("[GM-Team][国漫][诛仙 第4季][2026][01][HEVC]") leave IndexNumber null, which kills every
            // lookup below and also drops the file out of its season in the UI. A provider cannot
            // influence the resolver, so this is the last place the number can be recovered, and it
            // has to be written twice: onto the result so Emby persists it, and back onto info so the
            // matching code further down can use it.
            var numberedFromFileName = false;
            if (!info.IndexNumber.HasValue && options.ParseEpisodeNumberFromFileName &&
                !string.IsNullOrWhiteSpace(info.Path))
            {
                var fileName = Path.GetFileName(info.Path);
                var parsedIndex = TitleNormalizer.ParseEpisodeNumber(fileName);
                if (parsedIndex.HasValue)
                {
                    info.IndexNumber = parsedIndex;
                    result.Item.IndexNumber = parsedIndex;
                    numberedFromFileName = true;
                    Logger.Info(
                        "Bangumi: Emby left \"{0}\" unnumbered, recovered episode {1} from the file name",
                        fileName, parsedIndex.Value);
                }
            }

            // Emby's resolver runs before any provider and is happy to read a date out of a
            // collaboration release name: "[FLsnow&WBX] Wonderful Precure! [15.5]
            // [Crayon_Shin-chan-240518_collaborateur-part][1080P]" becomes season 2405 episode 18,
            // which invents a "Season 2405" in the UI and sends every lookup below to the wrong
            // subject. The provider cannot pre-empt the resolver, so the only option is to notice
            // that the value is impossible - no anime has a four digit season or episode - and
            // repair it here. Deliberately narrow: a wrong but *possible* number is left alone,
            // because overruling the resolver on plausible input would break far more files than
            // it fixes.
            if (options.FixImplausibleEpisodeNumbers && !string.IsNullOrWhiteSpace(info.Path))
            {
                var seasonImplausible = info.ParentIndexNumber.HasValue &&
                    info.ParentIndexNumber.Value >= ImplausibleNumberFloor;
                var indexImplausible = info.IndexNumber.HasValue &&
                    info.IndexNumber.Value >= ImplausibleNumberFloor;

                if (seasonImplausible || indexImplausible)
                {
                    var badName = Path.GetFileName(info.Path);

                    // A half episode is genuinely not a numbered episode of the season it sits in,
                    // so specials is not a fallback here, it is the right answer. Special 15 for
                    // "15.5" also cannot collide with the regular episode 15.
                    var fractionalBase = TitleNormalizer.ParseFractionalEpisodeBase(badName);
                    var recovered = fractionalBase.HasValue
                        ? null
                        : TitleNormalizer.ParseEpisodeNumber(badName);

                    if (fractionalBase.HasValue)
                    {
                        Logger.Info(
                            "Bangumi: \"{0}\" was resolved as S{1}E{2} from a date in the file name; " +
                            "it is a half episode, filing it as special {3}",
                            badName, info.ParentIndexNumber, info.IndexNumber, fractionalBase.Value);
                        info.ParentIndexNumber = 0;
                        info.IndexNumber = fractionalBase;
                        info.IndexNumberEnd = null;
                        result.Item.ParentIndexNumber = 0;
                        result.Item.IndexNumber = fractionalBase;
                        result.Item.IndexNumberEnd = null;
                        numberedFromFileName = true;
                    }
                    else if (recovered.HasValue)
                    {
                        Logger.Info(
                            "Bangumi: \"{0}\" was resolved as S{1}E{2}; episode {3} is the only " +
                            "number the file name actually carries, using it",
                            badName, info.ParentIndexNumber, info.IndexNumber, recovered.Value);
                        info.IndexNumber = recovered;
                        info.IndexNumberEnd = null;
                        result.Item.IndexNumber = recovered;
                        result.Item.IndexNumberEnd = null;
                        numberedFromFileName = true;
                    }
                    else
                    {
                        // Nothing safe to substitute. Say so once per refresh rather than writing a
                        // guess into the library.
                        Logger.Warn(
                            "Bangumi: \"{0}\" was resolved as S{1}E{2}, which cannot be real - the " +
                            "numbers come from a date or a resolution in the file name. Rename the " +
                            "file (for example \"SxxEyy\") or move it into Specials.",
                            badName, info.ParentIndexNumber, info.IndexNumber);
                    }
                }
            }

            var isSpecial = info.ParentIndexNumber.HasValue && info.ParentIndexNumber.Value == 0;

            var candidates = await ResolveCandidatesAsync(info, options, isSpecial, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                Verbose("Bangumi has no subject candidate for episode \"{0}\" (season {1})",
                    info.Name, info.ParentIndexNumber);
                if (numberedFromFileName) KeepFileNameNumbering(result, info);
                return result;
            }

            BangumiEpisode episode = null;
            var matchedSubjectId = 0;
            var matchedBy = "none";
            var preceding = 0;

            foreach (var candidateId in candidates)
            {
                var episodes = await LoadEpisodesAsync(candidateId, isSpecial, cancellationToken).ConfigureAwait(false);
                if (episodes.Count == 0) continue;

                episode = Match(episodes, info, options, preceding, out matchedBy);
                if (episode != null)
                {
                    matchedSubjectId = candidateId;
                    break;
                }

                preceding += episodes.Count;
            }

            // Absolute (franchise wide) numbering. Long running donghua and shows like 名侦探柯南 are
            // released with one continuous counter - 仙逆 files are numbered 147..155 - while Bangumi
            // splits the show into one subject per year, each restarting ep at 1. The number therefore
            // exists nowhere in the season's own subjects, but it is exactly Bangumi's sort field, which
            // counts the whole franchise: 仙逆 年番3 is ep 1..52 / sort 129..180. Gated on the number
            // exceeding everything the season accounted for, so a plainly missing episode does not pay
            // for a chain walk.
            if (episode == null && !isSpecial && options.ResolveAbsoluteEpisodeNumbers &&
                info.IndexNumber.HasValue)
            {
                var absoluteTarget = info.IndexNumber.Value + options.EpisodeIndexOffset;
                if (absoluteTarget > preceding)
                {
                    var absolute = await ResolveAbsoluteAsync(candidates[0], absoluteTarget, cancellationToken)
                        .ConfigureAwait(false);
                    if (absolute != null)
                    {
                        episode = absolute.Episode;
                        matchedSubjectId = absolute.SubjectId;
                        matchedBy = "absolute sort";
                    }
                }
            }

            if (episode == null)
            {
                Verbose("Bangumi: no episode matched index {0} (season {1}) in subject(s) {2}, {3} episode(s) inspected",
                    info.IndexNumber, info.ParentIndexNumber, Join(candidates), preceding);
                if (numberedFromFileName) KeepFileNameNumbering(result, info);
                return result;
            }

            var title = PickTitle(episode.Name, episode.NameCn, options);
            if (string.IsNullOrWhiteSpace(title))
            {
                // Donghua and many web releases carry no per-episode titles on Bangumi at all. Leaving
                // Name unset is worse than it looks: Emby keeps the name it generated back when the
                // file was resolved, and for a file it could not number that is the identical
                // "第01集" on every episode in the folder. Naming from the number that was actually
                // matched is both correct and unique.
                title = FormatNumberedTitle(episode, info, options);
            }

            if (!string.IsNullOrWhiteSpace(title)) result.Item.Name = title;
            if (options.WriteOriginalTitle && !string.IsNullOrWhiteSpace(episode.Name))
            {
                result.Item.OriginalTitle = episode.Name;
            }

            if (!string.IsNullOrWhiteSpace(episode.Desc)) result.Item.Overview = episode.Desc.Trim();

            var airdate = ParseDate(episode.Airdate);
            if (airdate.HasValue)
            {
                result.Item.PremiereDate = airdate;
                result.Item.ProductionYear = airdate.Value.Year;
            }

            // Off by default: Emby probes the real duration from the file, and the Bangumi value is
            // the broadcast slot length, which disagrees with the encode more often than not.
            if (options.WriteEpisodeRuntime)
            {
                if (episode.DurationSeconds.HasValue && episode.DurationSeconds.Value > 0)
                {
                    result.Item.RunTimeTicks = TimeSpan.FromSeconds(episode.DurationSeconds.Value).Ticks;
                }
                else
                {
                    var ticks = ParseDurationToTicks(episode.Duration);
                    if (ticks.HasValue) result.Item.RunTimeTicks = ticks;
                }
            }

            if (episode.Id > 0)
            {
                result.Item.ProviderIds[BangumiConstants.EpisodeProviderId] =
                    episode.Id.ToString(CultureInfo.InvariantCulture);
            }

            // The subject that actually owned the episode is recorded so a later refresh of a split
            // cour season does not have to walk the sequel chain again.
            if (matchedSubjectId > 0)
            {
                result.Item.ProviderIds[BangumiConstants.ProviderId] =
                    matchedSubjectId.ToString(CultureInfo.InvariantCulture);
            }

            result.HasMetadata = true;
            Verbose("Bangumi subject {0}: episode index {1} matched ep id {2} via {3}",
                matchedSubjectId, info.IndexNumber, episode.Id, matchedBy);
            return result;
        }

        /// <summary>
        /// Placeholder title for an episode Bangumi never titled. Prefers the number Emby is showing
        /// over Bangumi's <c>ep</c> so the label cannot contradict the episode's own index.
        /// </summary>
        private static string FormatNumberedTitle(BangumiEpisode episode, EpisodeInfo info, PluginOptions options)
        {
            int number;
            if (info.IndexNumber.HasValue && info.IndexNumber.Value > 0)
            {
                number = info.IndexNumber.Value;
            }
            else if (episode.Ep.HasValue && episode.Ep.Value > 0)
            {
                number = (int)episode.Ep.Value;
            }
            else if (episode.Sort > 0)
            {
                number = (int)episode.Sort;
            }
            else
            {
                return null;
            }

            var padded = number.ToString("00", CultureInfo.InvariantCulture);
            return options.PreferChineseTitle
                ? "第" + padded + "集"
                : "第" + padded + "話";
        }

        /// <summary>
        /// A number recovered from the file name is worth keeping even when Bangumi had nothing to
        /// say about the episode, but Emby discards a result whose HasMetadata is false - including
        /// the IndexNumber on it. Name is copied across at the same time so a "replace all metadata"
        /// refresh cannot blank the title on the way through.
        /// </summary>
        private static void KeepFileNameNumbering(MetadataResult<Episode> result, EpisodeInfo info)
        {
            if (string.IsNullOrWhiteSpace(result.Item.Name) && !string.IsNullOrWhiteSpace(info.Name))
            {
                result.Item.Name = info.Name;
            }

            result.HasMetadata = true;
        }

        /// <summary>
        /// Subjects to search, in air order. Most specific source of truth first: an id pinned on the
        /// episode, then the season, then the series. The series path resolves the season itself so
        /// episodes still land correctly when only the series was identified.
        /// </summary>
        private async Task<List<int>> ResolveCandidatesAsync(
            EpisodeInfo info, PluginOptions options, bool isSpecial, CancellationToken cancellationToken)
        {
            int pinnedSubjectId;
            if (TryGetSubjectId(info.ProviderIds, out pinnedSubjectId))
            {
                return new List<int> { pinnedSubjectId };
            }

            int seasonSubjectId;
            if (TryGetSubjectId(info.SeasonProviderIds, out seasonSubjectId))
            {
                // Specials live in the main subject; extending across sequels would only pull in
                // another season worth of extras.
                if (isSpecial || !options.AutoResolveSequelSeasons)
                {
                    return new List<int> { seasonSubjectId };
                }

                return await BangumiSeasonResolver
                    .BuildEpisodeCandidatesAsync(Api, seasonSubjectId, cancellationToken)
                    .ConfigureAwait(false);
            }

            int seriesSubjectId;
            if (!TryGetSubjectId(info.SeriesProviderIds, out seriesSubjectId)) return new List<int>();

            var seasonNumber = info.ParentIndexNumber ?? 1;
            if (isSpecial || seasonNumber <= 1 || !options.AutoResolveSequelSeasons)
            {
                return new List<int> { seriesSubjectId };
            }

            var chain = await BangumiSeasonResolver
                .BuildChainAsync(Api, seriesSubjectId, cancellationToken)
                .ConfigureAwait(false);

            var resolution = BangumiSeasonResolver.ResolveFromChain(chain, seasonNumber);
            if (resolution == null) resolution = BangumiSeasonResolver.ResolveByOrdinal(chain, seasonNumber);
            if (resolution != null) return resolution.SubjectIds;

            // No sequel subjects at all: long running shows keep every episode in a single subject
            // and let sort run past 100, so the series subject is exactly where to look.
            if (chain.Count <= 1) return new List<int> { seriesSubjectId };

            return new List<int>();
        }

        private async Task<List<BangumiEpisode>> LoadEpisodesAsync(
            int subjectId, bool isSpecial, CancellationToken cancellationToken)
        {
            if (isSpecial)
            {
                var specials = await Api
                    .GetEpisodesAsync(subjectId, BangumiConstants.EpisodeType.Special, cancellationToken)
                    .ConfigureAwait(false);
                if (specials != null && specials.Count > 0) return specials;

                // Some subjects file their extras as 其他 / 预告 rather than 特别篇.
                var all = await Api.GetEpisodesAsync(subjectId, null, cancellationToken).ConfigureAwait(false);
                return (all ?? new List<BangumiEpisode>())
                    .Where(e => e != null && e.Type != BangumiConstants.EpisodeType.Main)
                    .ToList();
            }

            var main = await Api
                .GetEpisodesAsync(subjectId, BangumiConstants.EpisodeType.Main, cancellationToken)
                .ConfigureAwait(false);
            return main ?? new List<BangumiEpisode>();
        }

        /// <summary>A hit from the franchise wide sort lookup, together with the subject that owned it.</summary>
        private sealed class AbsoluteMatch
        {
            public BangumiEpisode Episode { get; set; }

            public int SubjectId { get; set; }
        }

        /// <summary>
        /// Looks for an episode whose <c>sort</c> equals <paramref name="target"/> anywhere in the
        /// franchise. Only an exact sort match counts: sort is a single franchise wide counter, so an
        /// exact hit is the definition of "the Nth episode of this show" and needs no offset guessing.
        /// Unaired episodes are rejected, because a file that exists cannot hold one.
        /// </summary>
        private async Task<AbsoluteMatch> ResolveAbsoluteAsync(
            int seedSubjectId, int target, CancellationToken cancellationToken)
        {
            if (seedSubjectId <= 0 || target <= 0) return null;

            var franchise = await BangumiSeasonResolver
                .BuildFranchiseChainAsync(Api, seedSubjectId, cancellationToken)
                .ConfigureAwait(false);

            // A franchise of one subject was already searched by the caller.
            if (franchise.Count <= 1) return null;

            foreach (var subjectId in franchise)
            {
                var episodes = await LoadEpisodesAsync(subjectId, false, cancellationToken).ConfigureAwait(false);
                if (episodes.Count == 0) continue;

                var hit = episodes.FirstOrDefault(e => e != null && NearlyEqual(e.Sort, target) && HasAired(e));
                if (hit == null) continue;

                Verbose("Bangumi: index {0} looks like franchise numbering, sort {0} is ep {1} of subject {2}",
                    target, hit.Ep, subjectId);
                return new AbsoluteMatch { Episode = hit, SubjectId = subjectId };
            }

            return null;
        }

        /// <summary>
        /// Finds the Bangumi episode that belongs to <paramref name="info"/> inside a single subject.
        ///
        /// <paramref name="precedingEpisodeCount"/> is how many episodes the earlier subjects of the
        /// same season already accounted for. It is needed because <c>ep</c> restarts at 1 in every
        /// cour subject while Emby keeps the whole season in one folder: Re:Zero season 2 episode 14
        /// is <c>ep</c> 1 of subject 316247. Its <c>sort</c> is 39, not 14, since sort counts the
        /// entire franchise, so an offset lookup is the only thing that lines those files up.
        /// </summary>
        internal static BangumiEpisode Match(
            List<BangumiEpisode> episodes, EpisodeInfo info, PluginOptions options,
            int precedingEpisodeCount, out string matchedBy)
        {
            matchedBy = "none";

            int episodeId;
            if (TryGetId(info.ProviderIds, BangumiConstants.EpisodeProviderId, out episodeId))
            {
                var pinned = episodes.FirstOrDefault(e => e != null && e.Id == episodeId);

                // An id pointing at an episode that has not aired cannot describe a file that exists,
                // so it is treated as stale rather than as an override. This matters because the id is
                // derived data the provider wrote itself on an earlier pass: without this escape a
                // mismatch from an older matching rule would be pinned forever, immune to any later
                // fix, since the id lookup runs before the number lookup.
                if (pinned != null && !HasAired(pinned))
                {
                    pinned = null;
                }

                if (pinned != null)
                {
                    matchedBy = "episode id";
                    return pinned;
                }
            }

            if (!info.IndexNumber.HasValue) return null;

            var target = info.IndexNumber.Value + options.EpisodeIndexOffset;

            var hit = Find(episodes, target, options.EpisodeNumberMode, out matchedBy);
            if (hit != null) return hit;

            var shifted = target - precedingEpisodeCount;
            if (precedingEpisodeCount > 0 && shifted >= 1)
            {
                hit = Find(episodes, shifted, options.EpisodeNumberMode, out matchedBy);
                if (hit != null)
                {
                    matchedBy = matchedBy + " -" + precedingEpisodeCount.ToString(CultureInfo.InvariantCulture);
                    return hit;
                }
            }

            return null;
        }

        private static BangumiEpisode Find(
            List<BangumiEpisode> episodes, int target, EpisodeNumberMode mode, out string matchedBy)
        {
            matchedBy = "none";

            Func<BangumiEpisode, bool> byEp = e => e != null && e.Ep.HasValue && NearlyEqual(e.Ep.Value, target);
            Func<BangumiEpisode, bool> bySort = e => e != null && NearlyEqual(e.Sort, target);

            switch (mode)
            {
                case EpisodeNumberMode.EpisodeNumber:
                    var onlyEp = episodes.FirstOrDefault(byEp);
                    if (onlyEp != null) matchedBy = "ep";
                    return onlyEp;

                case EpisodeNumberMode.SortNumber:
                    var onlySort = episodes.FirstOrDefault(bySort);
                    if (onlySort != null) matchedBy = "sort";
                    return onlySort;

                default:
                    var byEpisodeNumber = episodes.FirstOrDefault(byEp);
                    var bySortNumber = episodes.FirstOrDefault(bySort);

                    // Both readings can resolve at once, to different episodes, and that is not an
                    // exotic case: it happens on every sequel subject whose sort range starts at or
                    // below its own episode count. 正反対な君と僕 第2期 numbers ep 1..13 as sort 13..25,
                    // so a file called S02E13 is simultaneously "ep 13" (the finale) and "sort 13"
                    // (the premiere). Preferring ep unconditionally, as the plain fallback order does,
                    // stamps the finale onto the first file of the season. A file cannot contain an
                    // episode that has not aired yet, so an unaired candidate loses the tie; when the
                    // air dates do not separate them, ep keeps its original priority.
                    if (byEpisodeNumber != null && bySortNumber != null &&
                        byEpisodeNumber.Id != bySortNumber.Id)
                    {
                        var epAired = HasAired(byEpisodeNumber);
                        var sortAired = HasAired(bySortNumber);
                        if (epAired != sortAired)
                        {
                            matchedBy = epAired ? "ep (unaired sort rejected)" : "sort (unaired ep rejected)";
                            return epAired ? byEpisodeNumber : bySortNumber;
                        }
                    }

                    if (byEpisodeNumber != null)
                    {
                        matchedBy = "ep";
                        return byEpisodeNumber;
                    }

                    if (bySortNumber != null)
                    {
                        matchedBy = "sort";
                        return bySortNumber;
                    }

                    // Last resort for subjects with no usable numbering at all (recap discs and the
                    // like): fall back to position, but only when the count lines up.
                    if (target >= 1 && target <= episodes.Count &&
                        episodes.All(e => e != null && !e.Ep.HasValue && e.Sort <= 0))
                    {
                        matchedBy = "ordinal";
                        return episodes[target - 1];
                    }

                    return null;
            }
        }

        private static string Join(List<int> ids)
        {
            return string.Join("/", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>
        /// Whether the episode is already broadcast. An unparsable or absent air date counts as aired,
        /// so a subject with no dates at all behaves exactly as it did before this check existed.
        /// </summary>
        private static bool HasAired(BangumiEpisode episode)
        {
            if (episode == null) return false;
            if (string.IsNullOrWhiteSpace(episode.Airdate)) return true;

            DateTime parsed;
            if (!DateTime.TryParseExact(episode.Airdate.Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return true;
            }

            return parsed.Date <= DateTime.UtcNow.Date.AddDays(1);
        }

        /// <summary>ep and sort are floating point (7.5 for a mid season special).</summary>
        private static bool NearlyEqual(double value, int target)
        {
            return Math.Abs(value - target) < 0.001;
        }
    }
}