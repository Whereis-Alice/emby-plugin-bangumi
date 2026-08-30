using System.ComponentModel;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;

namespace Emby.Plugins.Bangumi
{
    /// <summary>
    /// How an Emby episode number is matched against a Bangumi episode.
    /// Bangumi carries two numbers per episode: <c>ep</c> (number inside the
    /// subject / season) and <c>sort</c> (number continuing across the whole
    /// franchise). Long-running shows are usually numbered by <c>sort</c> on
    /// Bangumi while release groups number files by <c>ep</c>, so neither alone
    /// is correct for every library.
    /// </summary>
    public enum EpisodeNumberMode
    {
        /// <summary>Try <c>ep</c> first, then fall back to <c>sort</c>.</summary>
        Auto = 0,

        /// <summary>Only match against <c>ep</c>.</summary>
        EpisodeNumber = 1,

        /// <summary>Only match against <c>sort</c>.</summary>
        SortNumber = 2
    }

    /// <summary>
    /// What to do with a staff credit whose Bangumi relation has no Emby
    /// <see cref="MediaBrowser.Model.Entities.PersonType"/> counterpart (作画监督, 人物设定,
    /// 美术监督, 色彩设计, 摄影监督, 音响监督 ...). Emby only ships eight person types, so the
    /// long tail of animation credits has to be either dropped or filed under a generic type
    /// with the exact Bangumi job kept in the person's role text.
    /// </summary>
    public enum UnmappedStaffMode
    {
        /// <summary>Import nothing that is not one of the well known jobs.</summary>
        Skip = 0,

        /// <summary>Import as Producer, role = the Bangumi job title (default).</summary>
        Producer = 1,

        /// <summary>Import as GuestStar, which the Emby web client renders inside the cast list.</summary>
        GuestStar = 2
    }

    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Bangumi 番组计划";

        public override string EditorDescription =>
            "从 bgm.tv 抓取动画元数据。Bangumi 每一季是独立条目，因此天生支持「第二季 / 第三季」这类被其他刮削器合并的番剧。" +
            "中国大陆以外或被墙的网络环境请填写代理地址。";

        // ---------- 账号 ----------

        [DisplayName("Access Token")]
        [Description("可选。https://next.bgm.tv/demo/access-token 生成。留空时仍可正常刮削公开条目；填写后可访问 NSFW 条目，并让请求配额更宽松。")]
        [IsPassword]
        public string AccessToken { get; set; } = string.Empty;

        [DisplayName("包含 NSFW 条目")]
        [Description("搜索结果中包含 R18 条目。需要有效的 Access Token，否则 Bangumi 不会返回这些条目。")]
        public bool IncludeNsfw { get; set; } = false;

        // ---------- 标题与文本 ----------

        [DisplayName("优先使用中文标题")]
        [Description("有中文译名时用 name_cn 作为标题，原文名写入「原始标题」。关闭则反之。")]
        public bool PreferChineseTitle { get; set; } = true;

        [DisplayName("同时写入原文标题")]
        [Description("把另一个语种的名字写进 Emby 的「原始标题」字段。")]
        public bool WriteOriginalTitle { get; set; } = true;

        // ---------- 集数匹配 ----------

        [DisplayName("集号匹配方式")]
        [Description("Auto：先按季内集号 ep 匹配，匹配不到再按全系列连续集号 sort 匹配（推荐）。")]
        public EpisodeNumberMode EpisodeNumberMode { get; set; } = EpisodeNumberMode.Auto;

        [DisplayName("集号偏移")]
        [Description("在查询 Bangumi 之前加到 Emby 集号上的偏移量。例如文件是 E01-E12 但 Bangumi 记作 13-24 时填 12。")]
        [MinValue(-9999)]
        [MaxValue(9999)]
        public int EpisodeIndexOffset { get; set; } = 0;

        [DisplayName("自动解析续集季度")]
        [Description("当 Emby 的 Season N（N>1）自身没有 Bangumi ID 时，沿条目的「续集」关系向后走 N-1 步来定位该季的条目。关闭后所有季共用主条目。")]
        public bool AutoResolveSequelSeasons { get; set; } = true;

        [DisplayName("写入分集时长")]
        [Description("用 Bangumi 记录的单集时长填充「运行时间」。媒体文件本身的时长通常更准，默认关闭。")]
        public bool WriteEpisodeRuntime { get; set; } = false;

        // ---------- 标签 / 人员 ----------

        [DisplayName("导入标签")]
        [Description("把 Bangumi 的用户标签写入 Emby 的「标签」。")]
        public bool ImportTags { get; set; } = true;

        [DisplayName("标签数量上限")]
        [Description("按热度取前 N 个标签。")]
        [MinValue(0)]
        [MaxValue(100)]
        public int MaxTags { get; set; } = 15;

        [DisplayName("meta_tags 写入类型(Genres)")]
        [Description("Bangumi 的 meta_tags 是官方维护的低基数标签集（TV / 日本 / 漫画改 / 历史），比用户标签更适合当 Genres。关闭后 Genres 留空，交给排在后面的刮削器填。")]
        public bool ImportMetaTagsAsGenres { get; set; } = true;

        [DisplayName("Genres 黑名单")]
        [Description("逗号分隔。meta_tags 里描述播出平台 / 制作国家 / 原作类型的条目不是类型，默认过滤掉；被过滤的仍然会进 Tags。清空即不过滤。")]
        public string GenreBlocklist { get; set; } = BangumiConstants.DefaultGenreBlocklist;

        [DisplayName("标签同时写入类型(Genres)")]
        [Description("Bangumi 没有独立的 Genre 字段。开启后把标签也写入 Emby 的「类型」，会让类型列表变得很杂。")]
        public bool ImportTagsAsGenres { get; set; } = false;

        [DisplayName("导入制作人员")]
        [Description("导入 /v0/subjects/{id}/persons 的全部制作人员：监督、系列构成、脚本、音乐、原作、人物设定、作画监督、美术监督、摄影监督、音响监督、制片人等。")]
        public bool ImportStaff { get; set; } = true;

        [DisplayName("导入声优")]
        [Description("把声优导入为「演员」，角色名填写其配音的角色（取自 /v0/subjects/{id}/characters）。")]
        public bool ImportVoiceActors { get; set; } = true;

        [DisplayName("未识别职位的处理")]
        [Description("Emby 只有 8 种人员类型，装不下 Bangumi 的全部职位。Producer：把作画监督 / 人物设定 / 色彩设计 这类职位导入为「制作人」，职位原文写进角色名（推荐，信息不丢）。GuestStar：导入为客串，会显示在演员区。Skip：只导入能精确映射的职位。")]
        public UnmappedStaffMode UnmappedStaff { get; set; } = UnmappedStaffMode.Producer;

        [DisplayName("职位黑名单")]
        [Description("逗号分隔的 Bangumi 职位名，命中的人员不导入。热门番的「原画」「第二原画」动辄上百人，想让人员列表干净可以填 原画,第二原画,动画,动画检查。默认不过滤。")]
        public string StaffRelationBlocklist { get; set; } = string.Empty;

        [DisplayName("声优数量上限")]
        [Description("按 主角 → 配角 → 客串 排序后取前 N 位，保证被截断的一定是龙套。")]
        [MinValue(0)]
        [MaxValue(500)]
        public int MaxVoiceActors { get; set; } = 60;

        [DisplayName("制作人员数量上限")]
        [Description("按 监督 → 编剧 → 音乐 → 制片 → 其他 排序后取前 N 位。")]
        [MinValue(0)]
        [MaxValue(500)]
        public int MaxStaff { get; set; } = 60;

        [DisplayName("人员数量总上限")]
        [Description("声优 + 制作人员写入 Emby 的总条数上限，兜底防止单个条目塞进几百号人。")]
        [MinValue(0)]
        [MaxValue(1000)]
        public int MaxPersons { get; set; } = 200;

        [DisplayName("合并同一声优的多个角色")]
        [Description("一人分饰多角时合并成一条「角色A / 角色B」，而不是重复出现多次。")]
        public bool MergeMultiRoleActors { get; set; } = true;

        [DisplayName("角色名使用中文译名")]
        [Description("Bangumi 的条目角色接口只给日文名，中文译名在角色详情的 infobox 里。开启后逐个请求 /v0/characters/{id} 取「简体中文名」，会明显增加一次刮削的请求数（有缓存）。")]
        public bool TranslateCharacterNames { get; set; } = true;

        [DisplayName("声优 / 制作人员名使用中文译名")]
        [Description("同理请求 /v0/persons/{id} 取「简体中文名」。日文人名通常直接用汉字原文即可，默认关闭。")]
        public bool TranslatePersonNames { get; set; } = false;

        [DisplayName("中文译名查询上限")]
        [Description("每个条目最多请求多少条角色 / 人物详情用于取中文名。0 表示关闭译名查询。")]
        [MinValue(0)]
        [MaxValue(200)]
        public int MaxDetailLookups { get; set; } = 40;

        [DisplayName("人物页面元数据")]
        [Description("为演员 / 制作人员的人物页面填写简介、生日、出生地和头像（来自 /v0/persons/{id}）。关闭后人物页只有名字和从条目带过来的头像。")]
        public bool ImportPersonMetadata { get; set; } = true;

        // ---------- 网络 ----------

        [DisplayName("代理地址")]
        [Description("例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080。留空使用系统默认。")]
        [IsAdvanced]
        public string ProxyUrl { get; set; } = string.Empty;

        [DisplayName("User-Agent")]
        [Description("Bangumi 会拒绝通用 UA。保持默认值即可，除非你知道自己在做什么。")]
        [IsAdvanced]
        public string UserAgent { get; set; } = BangumiConstants.DefaultUserAgent;

        [DisplayName("API 地址")]
        [Description("默认 https://api.bgm.tv 。可改为自建反向代理。")]
        [IsAdvanced]
        public string ApiBaseUrl { get; set; } = BangumiConstants.DefaultApiBaseUrl;

        [DisplayName("请求最小间隔(毫秒)")]
        [Description("两次 API 请求之间的最小间隔，用于自我限速，避免被 Bangumi 拒绝。")]
        [MinValue(0)]
        [MaxValue(10000)]
        [IsAdvanced]
        public int RequestIntervalMs { get; set; } = 340;

        [DisplayName("请求超时(秒)")]
        [MinValue(5)]
        [MaxValue(300)]
        [IsAdvanced]
        public int RequestTimeoutSeconds { get; set; } = 30;

        [DisplayName("失败重试次数")]
        [Description("仅对超时、网络错误和 5xx / 429 重试，采用指数退避。")]
        [MinValue(0)]
        [MaxValue(10)]
        [IsAdvanced]
        public int MaxRetries { get; set; } = 2;

        [DisplayName("响应缓存时长(分钟)")]
        [Description("条目 / 分集 / 人员响应在内存中的缓存时间，让一次媒体库扫描不会对同一条目反复发请求。0 表示不缓存。")]
        [MinValue(0)]
        [MaxValue(1440)]
        [IsAdvanced]
        public int CacheMinutes { get; set; } = 30;

        // ---------- 刮削器行为 ----------

        [DisplayName("搜索结果数量")]
        [MinValue(1)]
        [MaxValue(50)]
        [IsAdvanced]
        public int SearchResultLimit { get; set; } = 20;

        [DisplayName("刮削器优先级")]
        [Description("数字越小越靠前。设为 -1 可让 Bangumi 排在 TheTVDB / TMDb 之前。")]
        [MinValue(-100)]
        [MaxValue(100)]
        [IsAdvanced]
        public int ProviderOrder { get; set; } = 0;

        [DisplayName("详细日志")]
        [Description("把每一次 API 请求、标题清洗结果和候选打分写进 Emby 日志。排查匹配错误时打开。")]
        [IsAdvanced]
        public bool EnableVerboseLogging { get; set; } = false;
    }
}