using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Bangumi.Web
{
    [Route("/Bangumi/Items/{Id}/Detail", "GET")]
    [Authenticated]
    public class GetBangumiUiDetail : IReturn<BangumiUiDetail>
    {
        /// <summary>Emby item id. Episodes and seasons walk up to the nearest item with a Bangumi id.</summary>
        public string Id { get; set; }

        /// <summary>Bypass the server side cache for this call.</summary>
        public bool Refresh { get; set; }

        /// <summary>
        /// Overrides UiCharacterNameLookups for this call. The client asks for 0 first so the
        /// page can render after four requests instead of forty-something, then asks again with
        /// the configured budget to fill in the Chinese character names.
        /// </summary>
        public int? NameBudget { get; set; }
    }

    [Route("/Bangumi/Characters/{Id}", "GET")]
    [Authenticated]
    public class GetBangumiUiCharacter : IReturn<BangumiUiEntity>
    {
        public int Id { get; set; }
    }

    [Route("/Bangumi/Persons/{Id}", "GET")]
    [Authenticated]
    public class GetBangumiUiPerson : IReturn<BangumiUiEntity>
    {
        public int Id { get; set; }
    }

    // No [Authenticated] on the two asset routes: they are fetched by <script src> / <link href>
    // from index.html, which cannot carry an X-Emby-Token header. They serve nothing but the
    // plugin own static files, and every data route below them is authenticated.

    [Route("/Bangumi/Ui/bangumi-ui.js", "GET")]
    [Unauthenticated]
    public class GetBangumiUiScript
    {
    }

    [Route("/Bangumi/Ui/bangumi-ui.css", "GET")]
    [Unauthenticated]
    public class GetBangumiUiStyle
    {
    }
    /// <summary>
    /// Relays a bgm.tv image through the server. The browser has no proxy configured while the
    /// plugin does, and lain.bgm.tv is not reachable from every network that can reach Emby.
    /// Host-restricted so this cannot be used as an open proxy.
    /// </summary>
    [Route("/Bangumi/Ui/Image", "GET")]
    [Unauthenticated]
    public class GetBangumiUiImage
    {
        public string Url { get; set; }
    }

    /// <summary>
    /// Serves the Bangumi shape of a subject straight to the web client, plus the injected
    /// script and stylesheet that render it.
    ///
    /// Everything here is best effort: a failure returns an empty payload rather than an error,
    /// because the caller is a piece of JavaScript grafted onto a page it does not own. A blank
    /// section is acceptable, a broken item page is not.
    /// </summary>
    public class BangumiUiService : IService, IRequiresRequest
    {
        /// <summary>
        /// Bangumi job titles in the order a viewer expects to read them. Anything not listed
        /// keeps the order Bangumi returned it in, after these.
        /// </summary>
        private static readonly string[] PositionOrder =
        {
            "原作", "原作插画", "导演", "总导演", "监督", "総監督", "总监督", "副导演", "系列导演",
            "系列构成", "シリーズ構成", "脚本", "剧本", "构成", "分镜", "絵コンテ", "演出",
            "人物设定", "角色设计", "キャラクターデザイン", "总作画监督", "作画监督", "原画", "第二原画",
            "美术监督", "美术设计", "色彩设计", "摄影监督", "CG导演", "3DCG导演", "特效",
            "音响监督", "音乐", "音楽", "音乐制作", "主题歌演出", "主题歌作曲", "主题歌作词", "主题歌编曲",
            "插入歌演出", "插入歌作曲", "插入歌作词", "编辑", "剪辑", "设定", "道具设计", "机械设定",
            "动画制作", "动画制作协力", "制作", "製作", "企划", "企画", "制作人", "プロデューサー",
            "アニメーションプロデューサー", "制作助手", "录音", "音响制作", "台词编辑", "字幕"
        };

        private static readonly Dictionary<int, string> BloodTypes = new Dictionary<int, string>
        {
            { 1, "A" }, { 2, "B" }, { 3, "AB" }, { 4, "O" }
        };

        private static readonly ExpiringCache Cache = new ExpiringCache();

        private static readonly object AssetLock = new object();
        private static Dictionary<string, byte[]> _assets;

        private readonly ILogger _logger;
        private readonly IHttpResultFactory _resultFactory;

        public BangumiUiService(ILogManager logManager, IHttpResultFactory resultFactory)
        {
            _logger = logManager.GetLogger(BangumiConstants.PluginName + ".Ui");
            _resultFactory = resultFactory;
        }

        public IRequest Request { get; set; }

        // ------------------------------------------------------------------ assets

        public object Get(GetBangumiUiScript request)
        {
            return StaticAsset("bangumi-ui.js", "application/javascript; charset=utf-8");
        }

        public object Get(GetBangumiUiStyle request)
        {
            return StaticAsset("bangumi-ui.css", "text/css; charset=utf-8");
        }
        public async Task<object> Get(GetBangumiUiImage request)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Cache-Control", "public, max-age=604800" }
            };

            if (!IsBangumiImageUrl(request.Url))
            {
                return _resultFactory.GetResult(
                    Request, new ReadOnlyMemory<byte>(new byte[0]), "image/gif", headers);
            }

            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                using (var response = await Plugin.RequireApi()
                    .GetRawAsync(request.Url, cts.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode || response.Content == null)
                    {
                        return _resultFactory.GetResult(
                            Request, new ReadOnlyMemory<byte>(new byte[0]), "image/gif", headers);
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    var contentType = response.Content.Headers.ContentType != null
                        ? response.Content.Headers.ContentType.ToString()
                        : "image/jpeg";

                    return _resultFactory.GetResult(
                        Request, new ReadOnlyMemory<byte>(bytes), contentType, headers);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Bangumi UI image relay failed for {0}: {1}", request.Url, ex.Message);
                return _resultFactory.GetResult(
                    Request, new ReadOnlyMemory<byte>(new byte[0]), "image/gif", headers);
            }
        }

        private static bool IsBangumiImageUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            Uri uri;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            foreach (var allowed in new[] { "bgm.tv", "bangumi.tv" })
            {
                if (string.Equals(uri.Host, allowed, StringComparison.OrdinalIgnoreCase)) return true;
                if (uri.Host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private object StaticAsset(string name, string contentType)
        {
            var body = LoadAsset(name);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // The file changes whenever the dll is replaced and there is no version in the
                // URL, so a cached copy would silently outlive a plugin upgrade.
                { "Cache-Control", "no-cache, must-revalidate" }
            };

            return _resultFactory.GetResult(Request, new ReadOnlyMemory<byte>(body), contentType, headers);
        }

        private byte[] LoadAsset(string name)
        {
            lock (AssetLock)
            {
                if (_assets == null) _assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                byte[] cached;
                if (_assets.TryGetValue(name, out cached)) return cached;

                var bytes = ReadEmbedded(name);
                _assets[name] = bytes;
                return bytes;
            }
        }

        private byte[] ReadEmbedded(string name)
        {
            try
            {
                var assembly = typeof(BangumiUiService).GetTypeInfo().Assembly;
                var suffix = ".Web.Assets." + name;
                var resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

                if (resource == null)
                {
                    _logger.Error("Bangumi UI asset {0} is missing from the assembly", name);
                    return new byte[0];
                }

                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Bangumi UI could not read asset " + name, ex);
                return new byte[0];
            }
        }

        // ------------------------------------------------------------------ detail

        public async Task<object> Get(GetBangumiUiDetail request)
        {
            var options = Plugin.CurrentOptions();
            var empty = new BangumiUiDetail
            {
                SubjectId = 0,
                Characters = new List<BangumiUiCharacter>(),
                VoiceActors = new List<BangumiUiPerson>(),
                StaffGroups = new List<BangumiUiStaffGroup>(),
                Related = new List<BangumiUiRelated>(),
                Tags = new List<BangumiUiTag>(),
                Layout = BuildLayout(options)
            };

            if (!options.EnableBangumiUi) return empty;

            long itemId;
            if (!TryParseItemId(request.Id, out itemId)) return empty;
            empty.ItemId = itemId;

            int subjectId;
            long resolvedItemId;
            if (!TryResolveSubject(itemId, out subjectId, out resolvedItemId)) return empty;

            var nameBudget = request.NameBudget.HasValue
                ? Math.Max(0, Math.Min(200, request.NameBudget.Value))
                : Math.Max(0, options.UiCharacterNameLookups);

            var cacheKey = "detail:" + subjectId.ToString(CultureInfo.InvariantCulture) + ":"
                           + nameBudget.ToString(CultureInfo.InvariantCulture);

            if (!request.Refresh)
            {
                var hit = Cache.Get(cacheKey) as BangumiUiDetail;
                if (hit != null) return Rebind(hit, itemId, resolvedItemId, options);
            }

            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                try
                {
                    var detail = await BuildDetailAsync(subjectId, options, nameBudget, cts.Token)
                        .ConfigureAwait(false);
                    Cache.Set(cacheKey, detail, TimeSpan.FromMinutes(Math.Max(1, options.UiCacheMinutes)));
                    return Rebind(detail, itemId, resolvedItemId, options);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException(
                        "Bangumi UI failed to build detail for subject " +
                        subjectId.ToString(CultureInfo.InvariantCulture), ex);
                    return empty;
                }
            }
        }

        public async Task<object> Get(GetBangumiUiCharacter request)
        {
            return await WikiEntityAsync(
                "character:" + request.Id.ToString(CultureInfo.InvariantCulture),
                async token =>
                {
                    var character = await Plugin.RequireApi().GetCharacterAsync(request.Id, token)
                        .ConfigureAwait(false);
                    if (character == null) return null;

                    var entity = BaseEntity(character, BangumiConstants.CharacterUrlFormat);
                    return entity;
                }).ConfigureAwait(false);
        }

        public async Task<object> Get(GetBangumiUiPerson request)
        {
            return await WikiEntityAsync(
                "person:" + request.Id.ToString(CultureInfo.InvariantCulture),
                async token =>
                {
                    var person = await Plugin.RequireApi().GetPersonAsync(request.Id, token)
                        .ConfigureAwait(false);
                    if (person == null) return null;

                    var entity = BaseEntity(person, BangumiConstants.PersonUrlFormat);
                    entity.Career = person.Career;
                    entity.DeathDate = person.DeathDateText();
                    entity.BirthPlace = person.BirthPlace();
                    return entity;
                }).ConfigureAwait(false);
        }

        private async Task<object> WikiEntityAsync(
            string cacheKey, Func<CancellationToken, Task<BangumiUiEntity>> factory)
        {
            if (!Plugin.CurrentOptions().EnableBangumiUi) return new BangumiUiEntity();

            var hit = Cache.Get(cacheKey) as BangumiUiEntity;
            if (hit != null) return hit;

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                try
                {
                    var entity = await factory(cts.Token).ConfigureAwait(false);
                    if (entity == null) return new BangumiUiEntity();

                    Cache.Set(
                        cacheKey,
                        entity,
                        TimeSpan.FromMinutes(Math.Max(1, Plugin.CurrentOptions().UiCacheMinutes)));
                    return entity;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Bangumi UI lookup failed for " + cacheKey, ex);
                    return new BangumiUiEntity();
                }
            }
        }

        private static BangumiUiEntity BaseEntity(BangumiWikiEntity source, string urlFormat)
        {
            string bloodType = null;
            if (source.BloodType.HasValue) BloodTypes.TryGetValue(source.BloodType.Value, out bloodType);

            return new BangumiUiEntity
            {
                Id = source.Id,
                Type = source.Type,
                Name = source.Name,
                NameCn = source.ChineseName(),
                Image = source.Images != null ? source.Images.Best() : null,
                Url = string.Format(CultureInfo.InvariantCulture, urlFormat, source.Id),
                Summary = source.Summary,
                Gender = source.Gender,
                BloodType = bloodType,
                BirthDate = source.BirthDateText(),
                Aliases = source.Aliases(),
                Infobox = FlattenInfobox(source.Infobox)
            };
        }

        private static List<BangumiUiInfoboxEntry> FlattenInfobox(List<BangumiInfoboxItem> infobox)
        {
            var result = new List<BangumiUiInfoboxEntry>();
            if (infobox == null) return result;

            foreach (var item in infobox)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;

                var values = item.Values();
                if (values.Count == 0) continue;

                result.Add(new BangumiUiInfoboxEntry { Key = item.Key.Trim(), Values = values });
            }

            return result;
        }

        // ------------------------------------------------------------------ building

        private async Task<BangumiUiDetail> BuildDetailAsync(
            int subjectId, PluginOptions options, int nameBudget, CancellationToken cancellationToken)
        {
            var api = Plugin.RequireApi();

            // Sequential on purpose: BangumiApiClient serialises requests behind a 1-slot
            // semaphore and a minimum interval, so firing these in parallel buys nothing and
            // only makes the log harder to read.
            var subject = await api.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
            var characters = await api.GetSubjectCharactersAsync(subjectId, cancellationToken)
                .ConfigureAwait(false);
            var persons = await api.GetSubjectPersonsAsync(subjectId, cancellationToken).ConfigureAwait(false);

            List<BangumiRelatedSubject> related = null;
            if (options.UiShowRelated)
            {
                related = await api.GetRelatedSubjectsAsync(subjectId, cancellationToken).ConfigureAwait(false);
            }

            var detail = new BangumiUiDetail
            {
                SubjectId = subjectId,
                SubjectUrl = string.Format(
                    CultureInfo.InvariantCulture, BangumiConstants.SubjectUrlFormat, subjectId),
                Layout = BuildLayout(options)
            };

            if (subject != null)
            {
                detail.Name = subject.Name;
                detail.NameCn = subject.NameCn;
                detail.Platform = subject.Platform;
                detail.AirDate = subject.Date;
                detail.AirWeekday = BangumiInfobox.First(subject.Infobox, "放送星期", "放送开始");
                detail.TotalEpisodes = subject.TotalEpisodes > 0 ? subject.TotalEpisodes : subject.Eps;
                detail.Summary = subject.Summary;

                if (subject.Rating != null)
                {
                    detail.RatingScore = subject.Rating.Score;
                    detail.RatingRank = subject.Rating.Rank;
                    detail.RatingTotal = subject.Rating.Total;
                }

                detail.Tags = (subject.Tags ?? new List<BangumiTag>())
                    .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name))
                    .Take(24)
                    .Select(t => new BangumiUiTag { Name = t.Name.Trim(), Count = t.Count })
                    .ToList();
            }

            detail.Tags = detail.Tags ?? new List<BangumiUiTag>();

            detail.Characters = await BuildCharactersAsync(characters, Math.Max(0, nameBudget), cancellationToken)
                .ConfigureAwait(false);
            detail.VoiceActors = BuildVoiceActors(detail.Characters);
            detail.StaffGroups = BuildStaffGroups(persons, options);
            detail.Related = BuildRelated(related);

            return detail;
        }

        private async Task<List<BangumiUiCharacter>> BuildCharactersAsync(
            List<BangumiRelatedCharacter> source, int nameBudget, CancellationToken cancellationToken)
        {
            var result = new List<BangumiUiCharacter>();
            if (source == null) return result;

            var api = Plugin.RequireApi();

            // Main cast first, then supporting, then guests; Bangumi order is preserved inside
            // each bucket because OrderBy is stable.
            var ordered = source
                .Where(c => c != null)
                .OrderBy(c => RelationRank(c.Relation))
                .ToList();

            foreach (var character in ordered)
            {
                var entry = new BangumiUiCharacter
                {
                    Id = character.Id,
                    Name = character.Name,
                    Relation = character.Relation,
                    Type = character.Type,
                    Image = character.Images != null ? character.Images.Thumbnail() : null,
                    Url = string.Format(
                        CultureInfo.InvariantCulture, BangumiConstants.CharacterUrlFormat, character.Id),
                    Actors = new List<BangumiUiPerson>()
                };

                // /v0/subjects/{id}/characters has no Chinese name, so each one costs a request.
                // Budgeted, and the whole payload is cached for hours afterwards.
                if (nameBudget > 0)
                {
                    nameBudget--;
                    try
                    {
                        var full = await api.GetCharacterAsync(character.Id, cancellationToken)
                            .ConfigureAwait(false);
                        if (full != null) entry.NameCn = full.ChineseName();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(
                            "Bangumi UI could not translate character {0}: {1}", character.Id, ex.Message);
                    }
                }

                if (character.Actors != null)
                {
                    foreach (var actor in character.Actors)
                    {
                        if (actor == null) continue;

                        entry.Actors.Add(new BangumiUiPerson
                        {
                            Id = actor.Id,
                            Name = actor.Name,
                            Type = actor.Type,
                            Image = actor.Images != null ? actor.Images.Thumbnail() : null,
                            Url = string.Format(
                                CultureInfo.InvariantCulture, BangumiConstants.PersonUrlFormat, actor.Id),
                            Roles = new List<string>()
                        });
                    }
                }

                result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// Inverts the character list: one card per voice actor, listing every character they
        /// play on this subject. Bangumi returns the pairing character-first, but a cast row is
        /// what a viewer scans for.
        /// </summary>
        private static List<BangumiUiPerson> BuildVoiceActors(List<BangumiUiCharacter> characters)
        {
            var byId = new Dictionary<int, BangumiUiPerson>();
            var order = new List<int>();

            foreach (var character in characters)
            {
                var role = string.IsNullOrWhiteSpace(character.NameCn) ? character.Name : character.NameCn;

                foreach (var actor in character.Actors)
                {
                    BangumiUiPerson existing;
                    if (!byId.TryGetValue(actor.Id, out existing))
                    {
                        existing = new BangumiUiPerson
                        {
                            Id = actor.Id,
                            Name = actor.Name,
                            Type = actor.Type,
                            Image = actor.Image,
                            Url = actor.Url,
                            Roles = new List<string>()
                        };

                        byId[actor.Id] = existing;
                        order.Add(actor.Id);
                    }

                    if (!string.IsNullOrWhiteSpace(role) && !existing.Roles.Contains(role, StringComparer.Ordinal))
                    {
                        existing.Roles.Add(role);
                    }
                }
            }

            return order.Select(id => byId[id]).ToList();
        }

        private static List<BangumiUiStaffGroup> BuildStaffGroups(
            List<BangumiRelatedPerson> persons, PluginOptions options)
        {
            var groups = new List<BangumiUiStaffGroup>();
            if (persons == null) return groups;

            var blocked = ParseBlocklist(options.StaffRelationBlocklist);
            var byPosition = new Dictionary<string, BangumiUiStaffGroup>(StringComparer.Ordinal);

            foreach (var person in persons)
            {
                if (person == null) continue;

                var position = string.IsNullOrWhiteSpace(person.Relation) ? "其他" : person.Relation.Trim();
                if (blocked.Contains(position)) continue;

                BangumiUiStaffGroup group;
                if (!byPosition.TryGetValue(position, out group))
                {
                    group = new BangumiUiStaffGroup
                    {
                        Position = position,
                        Persons = new List<BangumiUiPerson>()
                    };

                    byPosition[position] = group;
                    groups.Add(group);
                }

                // The same person can be credited twice under one job when Bangumi splits the
                // credit by episode range; merge those into one card.
                var already = group.Persons.FirstOrDefault(p => p.Id == person.Id);
                if (already != null)
                {
                    already.Eps = JoinEps(already.Eps, person.Eps);
                    continue;
                }

                group.Persons.Add(new BangumiUiPerson
                {
                    Id = person.Id,
                    Name = person.Name,
                    Type = person.Type,
                    Image = person.Images != null ? person.Images.Thumbnail() : null,
                    Url = string.Format(
                        CultureInfo.InvariantCulture, BangumiConstants.PersonUrlFormat, person.Id),
                    Roles = new List<string> { position },
                    Eps = string.IsNullOrWhiteSpace(person.Eps) ? null : person.Eps.Trim()
                });
            }

            return groups
                .OrderBy(g => PositionRank(g.Position))
                .ToList();
        }

        private static string JoinEps(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(right)) return left;
            if (string.IsNullOrWhiteSpace(left)) return right.Trim();
            return left + ", " + right.Trim();
        }

        private static List<BangumiUiRelated> BuildRelated(List<BangumiRelatedSubject> source)
        {
            var result = new List<BangumiUiRelated>();
            if (source == null) return result;

            foreach (var subject in source)
            {
                if (subject == null) continue;

                result.Add(new BangumiUiRelated
                {
                    Id = subject.Id,
                    Name = subject.Name,
                    NameCn = subject.NameCn,
                    Relation = subject.Relation,
                    Type = subject.Type,
                    Image = subject.Images != null ? subject.Images.Thumbnail() : null,
                    Url = string.Format(
                        CultureInfo.InvariantCulture, BangumiConstants.SubjectUrlFormat, subject.Id)
                });
            }

            return result;
        }

        private static BangumiUiLayout BuildLayout(PluginOptions options)
        {
            return new BangumiUiLayout
            {
                ShowCharacters = true,
                ShowVoiceActors = options.UiShowVoiceActors,
                ShowStaffGroups = options.UiShowStaffGroups,
                ShowRelated = options.UiShowRelated,
                ShowRating = options.UiShowRating,
                ShowTags = options.UiShowTags,
                HideNativePeople = options.UiHideNativePeople,
                GroupCharactersByRelation = options.UiGroupCharactersByRelation,
                ProxyImages = options.UiProxyImages,
                CharacterNameLookups = Math.Max(0, options.UiCharacterNameLookups)
            };
        }

        /// <summary>
        /// A cached payload was built for a subject, not for an item, and the options may have
        /// changed since. Stamp the current request onto a shallow copy so that the cache stays
        /// keyed by subject alone.
        /// </summary>
        private static BangumiUiDetail Rebind(
            BangumiUiDetail source, long itemId, long resolvedItemId, PluginOptions options)
        {
            return new BangumiUiDetail
            {
                ItemId = itemId,
                ResolvedItemId = resolvedItemId,
                SubjectId = source.SubjectId,
                SubjectUrl = source.SubjectUrl,
                Name = source.Name,
                NameCn = source.NameCn,
                Platform = source.Platform,
                AirDate = source.AirDate,
                AirWeekday = source.AirWeekday,
                TotalEpisodes = source.TotalEpisodes,
                RatingScore = source.RatingScore,
                RatingRank = source.RatingRank,
                RatingTotal = source.RatingTotal,
                Summary = source.Summary,
                Tags = source.Tags,
                Characters = source.Characters,
                VoiceActors = source.VoiceActors,
                StaffGroups = source.StaffGroups,
                Related = source.Related,
                Layout = BuildLayout(options)
            };
        }

        // ------------------------------------------------------------------ ids

        private static bool TryParseItemId(string raw, out long itemId)
        {
            itemId = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId);
        }

        /// <summary>
        /// Episodes carry no Bangumi subject id of their own and a season may not have been
        /// matched yet, so walk up to the nearest ancestor that has one. Four hops covers
        /// episode -> season -> series -> collection.
        /// </summary>
        private bool TryResolveSubject(long itemId, out int subjectId, out long resolvedItemId)
        {
            subjectId = 0;
            resolvedItemId = 0;

            var library = Plugin.TryLibraryManager();
            if (library == null) return false;

            BaseItem item;
            try
            {
                item = library.GetItemById(itemId);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Bangumi UI could not load item " + itemId, ex);
                return false;
            }

            for (var hop = 0; hop < 4 && item != null; hop++)
            {
                if (TryGetSubjectId(item.ProviderIds, out subjectId))
                {
                    resolvedItemId = item.InternalId;
                    return true;
                }

                try
                {
                    item = item.GetParent();
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryGetSubjectId(IDictionary<string, string> providerIds, out int subjectId)
        {
            subjectId = 0;
            if (providerIds == null) return false;

            string raw;
            if (!providerIds.TryGetValue(BangumiConstants.ProviderId, out raw)) return false;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            int parsed;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            if (parsed <= 0) return false;

            subjectId = parsed;
            return true;
        }

        // ------------------------------------------------------------------ ordering

        private static int RelationRank(string relation)
        {
            if (string.Equals(relation, "主角", StringComparison.Ordinal)) return 0;
            if (string.Equals(relation, "配角", StringComparison.Ordinal)) return 1;
            if (string.Equals(relation, "客串", StringComparison.Ordinal)) return 2;
            return 3;
        }

        private static int PositionRank(string position)
        {
            for (var i = 0; i < PositionOrder.Length; i++)
            {
                if (string.Equals(PositionOrder[i], position, StringComparison.Ordinal)) return i;
            }

            return PositionOrder.Length;
        }

        private static HashSet<string> ParseBlocklist(string raw)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (var part in raw.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }

            return result;
        }

        // ------------------------------------------------------------------ cache

        /// <summary>
        /// Like <see cref="TtlCache"/> but keeps the built object instead of a JSON string.
        /// Assembling one detail payload costs up to 43 Bangumi requests at roughly three per
        /// second, so this is the difference between an instant panel and a fifteen second wait.
        /// </summary>
        private sealed class ExpiringCache
        {
            private readonly ConcurrentDictionary<string, Entry> _entries =
                new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

            private long _operations;

            private sealed class Entry
            {
                public object Payload;
                public DateTime ExpiresUtc;
            }

            public object Get(string key)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry)) return null;

                if (entry.ExpiresUtc <= DateTime.UtcNow)
                {
                    Entry removed;
                    _entries.TryRemove(key, out removed);
                    return null;
                }

                return entry.Payload;
            }

            public void Set(string key, object payload, TimeSpan ttl)
            {
                if (ttl <= TimeSpan.Zero || payload == null) return;

                _entries[key] = new Entry { Payload = payload, ExpiresUtc = DateTime.UtcNow.Add(ttl) };

                if (Interlocked.Increment(ref _operations) % 64 != 0) return;

                var now = DateTime.UtcNow;
                foreach (var pair in _entries.ToArray())
                {
                    if (pair.Value.ExpiresUtc > now) continue;

                    Entry removed;
                    _entries.TryRemove(pair.Key, out removed);
                }
            }
        }
    }
}
