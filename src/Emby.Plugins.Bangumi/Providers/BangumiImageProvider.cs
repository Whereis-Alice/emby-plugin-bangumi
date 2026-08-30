using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>
    /// Posters. Bangumi only stores one cover per subject and nothing at all per episode, so the
    /// single supported image type is <see cref="ImageType.Primary"/>; backdrops and logos are
    /// left to TMDB / fanart providers.
    /// </summary>
    public class BangumiImageProvider : BangumiProviderBase, IRemoteImageProvider, IHasOrder
    {
        public BangumiImageProvider(ILogManager logManager) : base(logManager)
        {
        }

        public bool Supports(BaseItem item)
        {
            return item is Series || item is Season || item is Movie;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();
            if (item == null) return images;

            int subjectId;
            if (!TryGetSubjectId(item.ProviderIds, out subjectId))
            {
                // A season that was matched by the folder layout alone has no id of its own;
                // its poster is then the parent series cover, which Emby already has.
                return images;
            }

            var subject = await Api.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
            if (subject == null || subject.Images == null) return images;

            var url = subject.Images.Best();
            if (string.IsNullOrWhiteSpace(url)) return images;

            images.Add(new RemoteImageInfo
            {
                ProviderName = BangumiConstants.PluginName,
                Type = ImageType.Primary,
                Url = url,
                ThumbnailUrl = subject.Images.Thumbnail(),
                Language = CurrentOptions.PreferChineseTitle ? "zh" : "ja",
                CommunityRating = subject.Rating != null && subject.Rating.Score > 0
                    ? (double?)subject.Rating.Score
                    : null,
                VoteCount = subject.Rating != null && subject.Rating.Total > 0
                    ? (int?)subject.Rating.Total
                    : null,
            });

            Verbose("Bangumi subject {0}: offering primary image {1}", subjectId, url);
            return images;
        }
    }
}