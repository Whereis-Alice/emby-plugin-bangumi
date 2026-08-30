using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>
    /// Anime films. On Bangumi these are still subject type 2 (anime); only the
    /// <c>platform</c> field distinguishes them ("剧场版"), which is why the platform bonus
    /// is inverted here.
    /// </summary>
    public class BangumiMovieProvider : BangumiProviderBase,
        IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private static readonly string[] RuntimeKeys = { "片长", "话数", "上映年度" };

        public BangumiMovieProvider(ILogManager logManager) : base(logManager)
        {
        }

        protected override int PlatformBonus(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return 0;
            if (platform.IndexOf("剧场版", StringComparison.Ordinal) >= 0) return 15;
            if (platform.IndexOf("劇場版", StringComparison.Ordinal) >= 0) return 15;
            if (string.Equals(platform, "OVA", StringComparison.OrdinalIgnoreCase)) return 5;
            if (string.Equals(platform, "TV", StringComparison.OrdinalIgnoreCase)) return -15;
            return 0;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            if (searchInfo == null) return results;

            int subjectId;
            if (TryGetSubjectId(searchInfo.ProviderIds, out subjectId))
            {
                var known = await Api.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
                if (known != null)
                {
                    results.Add(ToSearchResult(known));
                    return results;
                }
            }

            var ranked = await SearchAsync(
                searchInfo.Name, searchInfo.Path, BangumiConstants.SubjectType.Anime, searchInfo.Year, cancellationToken)
                .ConfigureAwait(false);

            foreach (var subject in ranked)
            {
                results.Add(ToSearchResult(subject));
            }

            return results;
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var result = new MetadataResult<Movie>
            {
                Item = new Movie(),
                HasMetadata = false,
                Provider = BangumiConstants.PluginName,
                ResultLanguage = options.PreferChineseTitle ? "zh" : "ja",
            };

            if (info == null) return result;

            BangumiSubject subject = null;

            int subjectId;
            if (TryGetSubjectId(info.ProviderIds, out subjectId))
            {
                subject = await Api.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
                if (subject != null) result.QueriedById = true;
            }

            if (subject == null)
            {
                var outcome = await SearchDetailedAsync(
                    info.Name, info.Path, BangumiConstants.SubjectType.Anime, info.Year, cancellationToken)
                    .ConfigureAwait(false);
                subject = await HydrateAsync(PickAutoMatch(outcome, info.Name, options), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (subject == null)
            {
                Verbose("Bangumi found no movie match for \"{0}\" ({1})", info.Name, info.Year);
                return result;
            }

            ApplySubject(result.Item, subject, options);

            // Only fill the runtime when the file itself has not been probed yet; Emby overwrites
            // this from the media stream anyway, so a wrong value here is harmless but noisy.
            foreach (var key in RuntimeKeys)
            {
                var ticks = subject.InfoboxValues(key)
                    .Select(ParseDurationToTicks)
                    .FirstOrDefault(t => t.HasValue);
                if (ticks.HasValue)
                {
                    result.Item.RunTimeTicks = ticks;
                    break;
                }
            }

            if (subject.Images != null) result.SearchImageUrl = subject.Images.Best();

            await ApplyPeopleAsync(result, subject.Id, options, cancellationToken).ConfigureAwait(false);

            result.HasMetadata = true;
            Verbose("Bangumi movie matched subject {0} ({1})", subject.Id, result.Item.Name);
            return result;
        }
    }
}