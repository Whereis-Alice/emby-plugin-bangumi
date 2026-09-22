using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Providers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Bangumi.Web
{
    [Route("/Bangumi/Items/{Id}/WatchTarget", "GET")]
    [Authenticated]
    public class GetBangumiWatchTarget : IReturn<BangumiWatchTarget>
    {
        public long Id { get; set; }
    }

    public class BangumiWatchTarget
    {
        public int ResolverVersion { get; set; } = 2;
        public string Status { get; set; } = "unresolved";
        public string Reason { get; set; }
        public string EpisodeId { get; set; }
        public string SubjectId { get; set; }
    }

    /// <summary>
    /// Read-only bridge adapter. Reuses the metadata resolver without refreshing the library,
    /// rewriting titles or touching user data. No title search and no library-name allowlist.
    /// </summary>
    public class BangumiWatchTargetService : IService, IRequiresRequest
    {
        private readonly ILibraryManager _library;
        private readonly IAuthorizationContext _authorization;
        private readonly BangumiEpisodeProvider _provider;

        public BangumiWatchTargetService(ILibraryManager library, IAuthorizationContext authorization,
            ILogManager logManager)
        {
            _library = library;
            _authorization = authorization;
            _provider = new BangumiEpisodeProvider(logManager);
        }

        public IRequest Request { get; set; }

        public async Task<object> Get(GetBangumiWatchTarget request)
        {
            var user = _authorization.GetAuthorizationInfo(Request).User;
            var item = _library.GetItemById(request.Id);
            if (user == null || item == null || !item.IsVisible(user))
                return new BangumiWatchTarget { Reason = "条目不存在或当前用户无权访问" };

            if (!(item is Episode episode))
                return new BangumiWatchTarget { Reason = "仅对分集解析观看目标；电影使用条目 Bangumi ID" };
            if (episode.IndexNumberEnd.HasValue && episode.IndexNumberEnd != episode.IndexNumber)
                return new BangumiWatchTarget { Reason = "合并多集文件需要明确的多分集映射，暂不自动点格子" };

            // The lookup contains episode -> season -> series ProviderIds and Emby's numbering.
            // It also understands virtual season folders; building this by walking paths does not.
            var info = episode.GetLookupInfo(_library.GetLibraryOptions(episode));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var result = await _provider.GetMetadataForWatchSync(info, cts.Token).ConfigureAwait(false);
                if (!result.HasMetadata ||
                    !result.Item.ProviderIds.TryGetValue(BangumiConstants.EpisodeProviderId, out var ep) ||
                    !result.Item.ProviderIds.TryGetValue(BangumiConstants.ProviderId, out var subject) ||
                    !int.TryParse(ep, NumberStyles.None, CultureInfo.InvariantCulture, out var epId) || epId <= 0 ||
                    !int.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out var subjectId) || subjectId <= 0)
                    return new BangumiWatchTarget { Reason = "缺少可靠的 Bangumi 父级 ID，或集号缺失、歧义、尚无已播出的对应分集" };

                return new BangumiWatchTarget
                {
                    Status = "matched", EpisodeId = ep, SubjectId = subject,
                    Reason = "插件按季度/整部 ID 与集号解析；未修改 Emby 元数据"
                };
            }
        }
    }
}
