using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>
    /// Metadata for the person pages behind every voice actor and staff credit. The subject level
    /// endpoints only hand out a name, an id and a thumbnail, so the biography, the birthday and
    /// the full size portrait have to come from /v0/persons/{id}.
    /// </summary>
    public class BangumiPersonProvider : BangumiProviderBase,
        IRemoteMetadataProvider<Person, PersonLookupInfo>, IHasOrder
    {
        public BangumiPersonProvider(ILogManager logManager) : base(logManager)
        {
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            PersonLookupInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            if (searchInfo == null) return results;

            var options = CurrentOptions;

            int personId;
            if (TryGetId(searchInfo.ProviderIds, BangumiConstants.PersonProviderId, out personId))
            {
                var known = await Api.GetPersonAsync(personId, cancellationToken).ConfigureAwait(false);
                if (known != null)
                {
                    results.Add(ToSearchResult(known, options));
                    return results;
                }
            }

            if (string.IsNullOrWhiteSpace(searchInfo.Name)) return results;

            var found = await Api
                .SearchPersonsAsync(searchInfo.Name.Trim(), Math.Max(1, Math.Min(20, options.SearchResultLimit)), cancellationToken)
                .ConfigureAwait(false);

            foreach (var person in found)
            {
                if (person == null) continue;
                results.Add(ToSearchResult(person, options));
            }

            return results;
        }

        public async Task<MetadataResult<Person>> GetMetadata(
            PersonLookupInfo info, CancellationToken cancellationToken)
        {
            var options = CurrentOptions;
            var result = new MetadataResult<Person>
            {
                Item = new Person(),
                HasMetadata = false,
                Provider = BangumiConstants.PluginName,
                ResultLanguage = options.PreferChineseTitle ? "zh" : "ja",
            };

            if (info == null || !options.ImportPersonMetadata) return result;

            BangumiPersonDetail detail = null;

            int personId;
            if (TryGetId(info.ProviderIds, BangumiConstants.PersonProviderId, out personId))
            {
                detail = await Api.GetPersonAsync(personId, cancellationToken).ConfigureAwait(false);
                if (detail != null) result.QueriedById = true;
            }

            if (detail == null && !string.IsNullOrWhiteSpace(info.Name))
            {
                detail = await FindByNameAsync(info.Name.Trim(), options, cancellationToken).ConfigureAwait(false);
            }

            if (detail == null)
            {
                Verbose("Bangumi found no person match for \"{0}\"", info.Name);
                return result;
            }

            // Renaming an existing Emby person would detach it from every item that credits it,
            // so the incoming name wins and the Bangumi spelling goes to the original title.
            var chinese = detail.ChineseName();
            var preferred = options.PreferChineseTitle && !string.IsNullOrWhiteSpace(chinese)
                ? chinese
                : detail.Name;

            result.Item.Name = string.IsNullOrWhiteSpace(info.Name) ? preferred : info.Name;
            if (!string.IsNullOrWhiteSpace(detail.Name)) result.Item.OriginalTitle = detail.Name;

            result.Item.Overview = BuildBiography(detail);

            var birth = detail.BirthDate();
            if (birth.HasValue)
            {
                result.Item.PremiereDate = birth;
                result.Item.ProductionYear = birth.Value.Year;
            }
            else if (detail.BirthYear.HasValue && detail.BirthYear.Value > 1800)
            {
                result.Item.ProductionYear = detail.BirthYear.Value;
            }

            var birthPlace = detail.BirthPlace();
            if (!string.IsNullOrWhiteSpace(birthPlace))
            {
                result.Item.ProductionLocations = new[] { birthPlace };
            }

            if (detail.Images != null) result.SearchImageUrl = detail.Images.Best();

            result.Item.ProviderIds[BangumiConstants.PersonProviderId] =
                detail.Id.ToString(CultureInfo.InvariantCulture);

            result.HasMetadata = true;
            Verbose("Bangumi person matched {0} ({1})", detail.Id, detail.Name);
            return result;
        }

        /// <summary>
        /// Bangumi's person search is a substring match with no ranking, and a wrong voice actor is
        /// worse than none, so only an exact hit on the name or one of its documented aliases counts.
        /// </summary>
        private async Task<BangumiPersonDetail> FindByNameAsync(
            string name, PluginOptions options, CancellationToken cancellationToken)
        {
            var candidates = await Api
                .SearchPersonsAsync(name, Math.Max(1, Math.Min(20, options.SearchResultLimit)), cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) return candidate;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (candidate.Aliases().Any(a => string.Equals(a, name, StringComparison.Ordinal))) return candidate;
            }

            return null;
        }

        /// <summary>
        /// Emby's person entity has no fields for gender, career or aliases, so the facts Bangumi
        /// keeps in the infobox are appended to the biography instead of being thrown away.
        /// </summary>
        private static string BuildBiography(BangumiPersonDetail detail)
        {
            var facts = new List<string>();

            var gender = BangumiInfobox.First(detail.Infobox, "性别");
            if (string.IsNullOrWhiteSpace(gender) && !string.IsNullOrWhiteSpace(detail.Gender))
            {
                gender = string.Equals(detail.Gender, "male", StringComparison.OrdinalIgnoreCase) ? "男"
                    : string.Equals(detail.Gender, "female", StringComparison.OrdinalIgnoreCase) ? "女"
                    : detail.Gender;
            }

            if (!string.IsNullOrWhiteSpace(gender)) facts.Add("性别：" + gender);

            var birthday = detail.BirthDateText();
            if (!string.IsNullOrWhiteSpace(birthday)) facts.Add("生日：" + birthday);

            var death = detail.DeathDateText();
            if (!string.IsNullOrWhiteSpace(death)) facts.Add("卒日：" + death);

            var place = detail.BirthPlace();
            if (!string.IsNullOrWhiteSpace(place)) facts.Add("出生地：" + place);

            var height = BangumiInfobox.First(detail.Infobox, "身高");
            if (!string.IsNullOrWhiteSpace(height)) facts.Add("身高：" + height);

            var agency = BangumiInfobox.First(detail.Infobox, "所属", "事务所");
            if (!string.IsNullOrWhiteSpace(agency)) facts.Add("所属：" + agency);

            var careers = CareerText(detail.Career);
            if (!string.IsNullOrWhiteSpace(careers)) facts.Add("职业：" + careers);

            var aliases = detail.Aliases()
                .Where(a => !string.Equals(a, detail.Name, StringComparison.Ordinal))
                .Take(6)
                .ToList();
            if (aliases.Count > 0) facts.Add("别名：" + string.Join("、", aliases));

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(detail.Summary)) builder.Append(detail.Summary.Trim());

            if (facts.Count > 0)
            {
                if (builder.Length > 0) builder.Append("\n\n");
                builder.Append(string.Join("\n", facts));
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static readonly Dictionary<string, string> CareerNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "producer", "制作人员" },
                { "mangaka", "漫画家" },
                { "artist", "音乐人" },
                { "seiyu", "声优" },
                { "writer", "作家" },
                { "illustrator", "插画师" },
                { "actor", "演员" },
            };

        private static string CareerText(List<string> career)
        {
            if (career == null || career.Count == 0) return null;

            var names = new List<string>();
            foreach (var item in career)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                string translated;
                names.Add(CareerNames.TryGetValue(item.Trim(), out translated) ? translated : item.Trim());
            }

            return names.Count == 0 ? null : string.Join("、", names);
        }

        private static RemoteSearchResult ToSearchResult(BangumiPersonDetail person, PluginOptions options)
        {
            var chinese = person.ChineseName();
            var result = new RemoteSearchResult
            {
                Name = options.PreferChineseTitle && !string.IsNullOrWhiteSpace(chinese) ? chinese : person.Name,
                SearchProviderName = BangumiConstants.PluginName,
                Overview = person.Summary,
                ImageUrl = person.Images == null ? null : person.Images.Thumbnail(),
                PremiereDate = person.BirthDate(),
            };

            if (!string.IsNullOrWhiteSpace(person.Name)) result.OriginalTitle = person.Name;

            var careers = CareerText(person.Career);
            if (!string.IsNullOrWhiteSpace(careers)) result.DisambiguationComment = careers;

            result.ProviderIds[BangumiConstants.PersonProviderId] =
                person.Id.ToString(CultureInfo.InvariantCulture);

            return result;
        }
    }

    /// <summary>Portraits for person pages. Bangumi stores exactly one picture per person.</summary>
    public class BangumiPersonImageProvider : BangumiProviderBase, IRemoteImageProvider, IHasOrder
    {
        public BangumiPersonImageProvider(ILogManager logManager) : base(logManager)
        {
        }

        public bool Supports(BaseItem item)
        {
            return item is Person;
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

            int personId;
            if (!TryGetId(item.ProviderIds, BangumiConstants.PersonProviderId, out personId)) return images;

            var detail = await Api.GetPersonAsync(personId, cancellationToken).ConfigureAwait(false);
            if (detail == null || detail.Images == null) return images;

            var url = detail.Images.Best();
            if (string.IsNullOrWhiteSpace(url)) return images;

            images.Add(new RemoteImageInfo
            {
                ProviderName = BangumiConstants.PluginName,
                Type = ImageType.Primary,
                Url = url,
                ThumbnailUrl = detail.Images.Thumbnail(),
            });

            Verbose("Bangumi person {0}: offering portrait {1}", personId, url);
            return images;
        }
    }
}
