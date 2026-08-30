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
    /// Maps an Emby season onto a Bangumi subject.
    ///
    /// The mapping itself lives in <see cref="BangumiSeasonResolver"/>; this provider only decides
    /// which strategies to try and which fields are safe to write. Season numbering comes from the
    /// folder layout and is copied back verbatim so a full refresh cannot clear it.
    /// </summary>
    public class BangumiSeasonProvider : BangumiProviderBase,
        IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
    {
        public BangumiSeasonProvider(ILogManager logManager) : base(logManager)
        {
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeasonInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            if (searchInfo == null) return results;

            int subjectId;
            if (TryGetSubjectId(searchInfo.ProviderIds, out subjectId))
            {
                var known = await Api.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
                if (known != null) results.Add(ToSearchResult(known));
                return results;
            }

            var resolution = await ResolveAsync(searchInfo, cancellationToken).ConfigureAwait(false);
            if (resolution == null || resolution.PrimaryId <= 0) return results;

            var subject = await Api.GetSubjectAsync(resolution.PrimaryId, cancellationToken).ConfigureAwait(false);
            if (subject != null) results.Add(ToSearchResult(subject));
            return results;
        }

        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var result = new MetadataResult<Season>
            {
                Item = new Season(),
                HasMetadata = false,
                Provider = BangumiConstants.PluginName,
                ResultLanguage = options.PreferChineseTitle ? "zh" : "ja",
            };

            if (info == null) return result;

            BangumiSubject subject = null;
            var byId = false;

            int subjectId;
            if (TryGetSubjectId(info.ProviderIds, out subjectId))
            {
                subject = await Api.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
                byId = subject != null;
            }

            if (subject == null)
            {
                var resolution = await ResolveAsync(info, cancellationToken).ConfigureAwait(false);
                if (resolution != null && resolution.PrimaryId > 0)
                {
                    Verbose("Bangumi season {0} of \"{1}\" -> {2}",
                        info.IndexNumber, info.SeriesName, resolution.Describe());
                    subject = await Api.GetSubjectAsync(resolution.PrimaryId, cancellationToken).ConfigureAwait(false);
                }
            }

            if (subject == null)
            {
                // Deliberately nothing: writing the series subject onto season 3 would be worse than
                // leaving the season blank, and the episode provider resolves independently anyway.
                Verbose("Bangumi could not resolve a subject for season {0} of \"{1}\"",
                    info.IndexNumber, info.SeriesName);
                return result;
            }

            result.QueriedById = byId;

            // The season number comes from the folder layout, so it is echoed back unchanged rather
            // than left null: a full refresh (ReplaceAllMetadata) merges the provider value verbatim,
            // and a null there would strip the season of its number and orphan every episode under it.
            result.Item.IndexNumber = info.IndexNumber;

            result.Item.ProviderIds[BangumiConstants.ProviderId] =
                subject.Id.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(subject.Summary)) result.Item.Overview = subject.Summary.Trim();

            var date = ParseDate(subject.Date);
            if (date.HasValue)
            {
                result.Item.PremiereDate = date;
                result.Item.ProductionYear = date.Value.Year;
            }

            if (subject.Rating != null && subject.Rating.Score > 0)
            {
                result.Item.CommunityRating = (float)subject.Rating.Score;
            }

            // A sequel subject carries a distinct title ("夺还篇"), which is genuinely useful as the
            // season name. The first season title would only duplicate the series, so it is left
            // alone and Emby keeps "Season N".
            int seriesSubjectId;
            var isSequelEntry = TryGetSubjectId(info.SeriesProviderIds, out seriesSubjectId) &&
                                seriesSubjectId != subject.Id;
            if (isSequelEntry)
            {
                var title = PickTitle(subject.Name, subject.NameCn, options);
                if (!string.IsNullOrWhiteSpace(title)) result.Item.Name = title;
                if (options.WriteOriginalTitle && !string.IsNullOrWhiteSpace(subject.Name))
                {
                    result.Item.OriginalTitle = subject.Name;
                }
            }

            if (subject.Images != null) result.SearchImageUrl = subject.Images.Best();

            // A season page has its own cast area, and a sequel season has its own cast: filling it
            // from the season subject is the only way The NATURAL and The AVVENIRE stop showing the
            // first season crew. Season 1 resolves to the series subject and therefore adds nothing new.
            if (options.ImportSeasonPeople)
            {
                await ApplyPeopleAsync(result, subject.Id, options, cancellationToken).ConfigureAwait(false);
            }

            result.HasMetadata = true;
            return result;
        }

        /// <summary>
        /// Three strategies, most reliable first: season number claimed by a subject title in the
        /// sequel chain, then a targeted search, then plain position in the chain.
        /// </summary>
        private async Task<SeasonResolution> ResolveAsync(SeasonInfo info, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;

            int seriesSubjectId;
            if (!TryGetSubjectId(info.SeriesProviderIds, out seriesSubjectId)) return null;

            var seasonNumber = info.IndexNumber ?? 1;

            // Season 0 holds specials, which live inside the main subject on Bangumi.
            if (seasonNumber <= 1)
            {
                return new SeasonResolution(new List<int> { seriesSubjectId }, "series subject");
            }

            if (!options.AutoResolveSequelSeasons)
            {
                return new SeasonResolution(new List<int> { seriesSubjectId }, "sequel resolution disabled");
            }

            var chain = await BangumiSeasonResolver
                .BuildChainAsync(Api, seriesSubjectId, cancellationToken)
                .ConfigureAwait(false);

            if (options.EnableVerboseLogging && chain.Count > 0)
            {
                Verbose("Bangumi sequel chain for {0}: {1}", seriesSubjectId,
                    string.Join(" -> ", chain.Select(c => c.Id + (c.SeasonMarker.HasValue ? "[S" + c.SeasonMarker.Value + "]" : "[S?]")).ToArray()));
            }

            var byMarker = BangumiSeasonResolver.ResolveFromChain(chain, seasonNumber);
            if (byMarker != null) return byMarker;

            var bySearch = await SearchForSeasonAsync(info, seasonNumber, seriesSubjectId, chain, cancellationToken)
                .ConfigureAwait(false);
            if (bySearch != null) return bySearch;

            var byOrdinal = BangumiSeasonResolver.ResolveByOrdinal(chain, seasonNumber);
            if (byOrdinal != null) return byOrdinal;

            return null;
        }

        /// <summary>
        /// Searches for "&lt;series&gt; 第N季". Accepted only when the hit is part of the sequel chain,
        /// or when the series has no chain at all, because search alone happily returns a spin off.
        /// </summary>
        private async Task<SeasonResolution> SearchForSeasonAsync(
            SeasonInfo info, int seasonNumber, int seriesSubjectId,
            List<SubjectChainEntry> chain, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(info.SeriesName)) return null;

            var keyword = info.SeriesName.Trim() + " 第" + seasonNumber.ToString(CultureInfo.InvariantCulture) + "季";
            var hits = await SearchAsync(keyword, BangumiConstants.SubjectType.Anime, null, cancellationToken)
                .ConfigureAwait(false);
            if (hits == null || hits.Count == 0) return null;

            var chainOnlyHasRoot = chain == null || chain.Count <= 1;

            foreach (var hit in hits)
            {
                if (hit == null || hit.Id <= 0 || hit.Id == seriesSubjectId) continue;

                var inChain = chain != null && chain.Any(c => c != null && c.Id == hit.Id);
                if (!inChain && !chainOnlyHasRoot) continue;

                // The hit must at least agree about which season it is, otherwise a search for
                // "X 第3季" that only matched "X" would be accepted as season 3.
                var marker = BangumiSeasonResolver.SeasonMarkerOf(hit.Name, hit.NameCn);
                if (!marker.HasValue || marker.Value != seasonNumber) continue;

                if (inChain)
                {
                    var index = chain.FindIndex(c => c != null && c.Id == hit.Id);
                    var fromChain = BangumiSeasonResolver.ResolveAt(chain, index, "search + chain");
                    if (fromChain != null) return fromChain;
                }

                return new SeasonResolution(new List<int> { hit.Id }, "search");
            }

            return null;
        }
    }
}