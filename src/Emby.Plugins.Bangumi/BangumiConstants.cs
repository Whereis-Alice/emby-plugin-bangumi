namespace Emby.Plugins.Bangumi
{
    public static class BangumiConstants
    {
        public const string PluginName = "Bangumi";

        /// <summary>
        /// Must stay in sync with the assembly GUID in AssemblyInfo.cs: Emby derives
        /// <see cref="MediaBrowser.Common.Plugins.BasePlugin.Id"/> from the assembly GUID.
        /// </summary>
        public const string PluginGuid = "a3f5b1c2-6d4e-4b8a-9c17-2e5f7d9a0b31";

        /// <summary>
        /// Provider id key stored in <c>ProviderIds</c>. Intentionally identical to the key used by
        /// <c>kookxiang/jellyfin-plugin-bangumi</c> so that libraries and NFO files are interchangeable.
        /// </summary>
        public const string ProviderId = "Bangumi";

        /// <summary>Provider id key for a Bangumi episode (not subject) id.</summary>
        public const string EpisodeProviderId = "BangumiEpisode";

        /// <summary>Provider id key for a Bangumi person id.</summary>
        public const string PersonProviderId = "BangumiPerson";

        /// <summary>
        /// Provider id key for a Bangumi character id. Characters and persons are separate id
        /// spaces on Bangumi - character 531 and person 531 are unrelated - so a character backed
        /// Emby person must never be stored under <see cref="PersonProviderId"/>.
        /// </summary>
        public const string CharacterProviderId = "BangumiCharacter";

        public const string DefaultApiBaseUrl = "https://api.bgm.tv";

        public const string SubjectUrlFormat = "https://bgm.tv/subject/{0}";
        public const string EpisodeUrlFormat = "https://bgm.tv/ep/{0}";
        public const string PersonUrlFormat = "https://bgm.tv/person/{0}";
        public const string CharacterUrlFormat = "https://bgm.tv/character/{0}";

        /// <summary>
        /// Bangumi blocks requests carrying a generic user agent (curl, python-requests, .NET default, ...)
        /// and asks third-party clients to identify themselves with a repository URL.
        /// </summary>
        public const string DefaultUserAgent =
            "Whereis-Alice/emby-plugin-bangumi/1.0.0 (Emby metadata provider; +https://github.com/Whereis-Alice/emby-plugin-bangumi)";

        /// <summary>
        /// meta_tags entries that describe the release platform, the production country or the source
        /// material. They are useful as tags but they are not genres, so they are dropped from
        /// the item genres unless the user clears the blocklist.
        /// </summary>
        public const string DefaultGenreBlocklist =
            "TV,WEB,OVA,OAD,剧场版,动态漫画,短片,PV,CM,广播剧,日本,中国,美国,韩国,英国,法国,德国,俄罗斯,苏联,加拿大,中国大陆,中国香港,中国台湾,欧美,其他,漫画改,小说改,轻小说改,游戏改,动画改,绘本改,视觉小说改,特摄,原创";

        /// <summary>Bangumi subject types.</summary>
        public static class SubjectType
        {
            public const int Book = 1;
            public const int Anime = 2;
            public const int Music = 3;
            public const int Game = 4;
            public const int Real = 6;
        }

        /// <summary>Bangumi episode types.</summary>
        public static class EpisodeType
        {
            public const int Main = 0;
            public const int Special = 1;
            public const int Opening = 2;
            public const int Ending = 3;
            public const int Trailer = 4;
            public const int Mad = 5;
            public const int Other = 6;
        }
    }
}