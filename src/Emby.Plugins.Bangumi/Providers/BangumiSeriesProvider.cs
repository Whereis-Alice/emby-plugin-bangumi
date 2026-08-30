using System;
using System.Collections.Generic;
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
    /// Series level metadata. This is the provider that makes Bangumi worth installing:
    /// Bangumi gives every cour its own subject, so "第N季" titles that TMDB folds into one
    /// entry (and therefore fails to match) resolve cleanly here.
    /// </summary>
    public class BangumiSeriesProvider : BangumiProviderBase,
        IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
    {
        public BangumiSeriesProvider(ILogManager logManager) : base(logManager)
        {
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeriesInfo searchInfo, CancellationToken cancellationToken)
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

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var result = new MetadataResult<Series>
            {
                Item = new Series(),
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
                if (subject != null)
                {
                    result.QueriedById = true;
                }
                else if (Logger != null)
                {
                    Logger.Warn("Bangumi subject {0} was requested by id but could not be fetched", subjectId);
                }
            }

            if (subject == null)
            {
                var ranked = await SearchAsync(
                    info.Name, info.Path, BangumiConstants.SubjectType.Anime, info.Year, cancellationToken)
                    .ConfigureAwait(false);
                subject = ranked.FirstOrDefault();
            }

            if (subject == null)
            {
                Verbose("Bangumi found no series match for \"{0}\" ({1})", info.Name, info.Year);
                return result;
            }

            ApplySubject(result.Item, subject, options);

            DayOfWeek[] airDays;
            string airTime;
            ApplyAirSchedule(subject, out airDays, out airTime);
            if (airDays != null) result.Item.AirDays = airDays;
            if (!string.IsNullOrWhiteSpace(airTime)) result.Item.AirTime = airTime;

            if (subject.Images != null) result.SearchImageUrl = subject.Images.Best();

            await ApplyPeopleAsync(result, subject.Id, options, cancellationToken).ConfigureAwait(false);

            result.HasMetadata = true;
            Verbose("Bangumi series matched subject {0} ({1})", subject.Id, result.Item.Name);
            return result;
        }
    }
}