using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
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

            var isSpecial = info.ParentIndexNumber.HasValue && info.ParentIndexNumber.Value == 0;

            var candidates = await ResolveCandidatesAsync(info, options, isSpecial, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                Verbose("Bangumi has no subject candidate for episode \"{0}\" (season {1})",
                    info.Name, info.ParentIndexNumber);
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

            if (episode == null)
            {
                Verbose("Bangumi: no episode matched index {0} (season {1}) in subject(s) {2}, {3} episode(s) inspected",
                    info.IndexNumber, info.ParentIndexNumber, Join(candidates), preceding);
                return result;
            }

            var title = PickTitle(episode.Name, episode.NameCn, options);
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

            // IndexNumber / ParentIndexNumber are intentionally left untouched: the file name is the
            // authority for numbering, and overwriting it here reorders the whole season.

            result.HasMetadata = true;
            Verbose("Bangumi subject {0}: episode index {1} matched ep id {2} via {3}",
                matchedSubjectId, info.IndexNumber, episode.Id, matchedBy);
            return result;
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
                    if (byEpisodeNumber != null)
                    {
                        matchedBy = "ep";
                        return byEpisodeNumber;
                    }

                    var bySortNumber = episodes.FirstOrDefault(bySort);
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

        /// <summary>ep and sort are floating point (7.5 for a mid season special).</summary>
        private static bool NearlyEqual(double value, int target)
        {
            return Math.Abs(value - target) < 0.001;
        }
    }
}