using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>
    /// Turns the stored Bangumi subject id into a clickable link on the item page and makes the
    /// id editable in the metadata editor.
    /// </summary>
    public class BangumiSubjectExternalId : IExternalId
    {
        public string Name
        {
            get { return BangumiConstants.PluginName; }
        }

        public string Key
        {
            get { return BangumiConstants.ProviderId; }
        }

        public string UrlFormatString
        {
            get { return BangumiConstants.SubjectUrlFormat; }
        }

        public bool Supports(IHasProviderIds item)
        {
            // Episodes carry the id of the subject that actually owned them, which matters for
            // split cour seasons; exposing it here keeps the value visible and editable.
            return item is Series || item is Season || item is Movie || item is Episode;
        }
    }

    /// <summary>Per-episode Bangumi id, which is a different id space from subjects.</summary>
    public class BangumiEpisodeExternalId : IExternalId
    {
        public string Name
        {
            get { return BangumiConstants.PluginName + " Episode"; }
        }

        public string Key
        {
            get { return BangumiConstants.EpisodeProviderId; }
        }

        public string UrlFormatString
        {
            get { return BangumiConstants.EpisodeUrlFormat; }
        }

        public bool Supports(IHasProviderIds item)
        {
            return item is Episode;
        }
    }

    /// <summary>Bangumi person id, attached to staff and voice actors.</summary>
    public class BangumiPersonExternalId : IExternalId
    {
        public string Name
        {
            get { return BangumiConstants.PluginName; }
        }

        public string Key
        {
            get { return BangumiConstants.PersonProviderId; }
        }

        public string UrlFormatString
        {
            get { return BangumiConstants.PersonUrlFormat; }
        }

        public bool Supports(IHasProviderIds item)
        {
            return item is Person;
        }
    }
}