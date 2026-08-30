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

            int characterId;
            if (TryGetId(searchInfo.ProviderIds, BangumiConstants.CharacterProviderId, out characterId))
            {
                // Rows created for an uncredited role point at /v0/characters/{id}; searching the
                // person index for that name would offer real people who merely share the string.
                var character = await Api.GetCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
                if (character != null) results.Add(ToSearchResult(character, options));
                return results;
            }

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

            int characterId;
            if (TryGetId(info.ProviderIds, BangumiConstants.CharacterProviderId, out characterId))
            {
                // Must return unconditionally: falling through to the person search would look the
                // role name up in an unrelated id space and confidently attach a stranger.
                return await BuildCharacterResultAsync(result, info, characterId, options, cancellationToken)
                    .ConfigureAwait(false);
            }

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
        /// Person page for a role Bangumi lists without a voice actor. /v0/characters/{id} is a
        /// separate endpoint over a separate id space and carries no career or death date, so it gets
        /// its own mapping instead of being squeezed through the person path.
        /// </summary>
        private async Task<MetadataResult<Person>> BuildCharacterResultAsync(
            MetadataResult<Person> result,
            PersonLookupInfo info,
            int characterId,
            PluginOptions options,
            CancellationToken cancellationToken)
        {
            var detail = await Api.GetCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
            if (detail == null)
            {
                Verbose("Bangumi character {0} could not be loaded", characterId);
                return result;
            }

            result.QueriedById = true;

            var chinese = detail.ChineseName();
            var preferred = options.PreferChineseTitle && !string.IsNullOrWhiteSpace(chinese)
                ? chinese
                : detail.Name;

            result.Item.Name = string.IsNullOrWhiteSpace(info.Name) ? preferred : info.Name;
            if (!string.IsNullOrWhiteSpace(detail.Name)) result.Item.OriginalTitle = detail.Name;

            result.Item.Overview = BuildCharacterBiography(detail);

            // Fictional birthdays are usually month and day only, which BirthDate() rejects because
            // Emby cannot store a partial date; the readable form still reaches the biography.
            var birth = detail.BirthDate();
            if (birth.HasValue)
            {
                result.Item.PremiereDate = birth;
                result.Item.ProductionYear = birth.Value.Year;
            }

            if (detail.Images != null) result.SearchImageUrl = detail.Images.Best();

            result.Item.ProviderIds[BangumiConstants.CharacterProviderId] =
                detail.Id.ToString(CultureInfo.InvariantCulture);

            result.HasMetadata = true;
            Verbose("Bangumi character matched {0} ({1})", detail.Id, detail.Name);
            return result;
        }

        /// <summary>Character infobox facts, in the order a character page reads best.</summary>
        private static string BuildCharacterBiography(BangumiCharacterDetail detail)
        {
            var facts = new List<string>();

            var gender = GenderText(BangumiInfobox.First(detail.Infobox, "性别"), detail.Gender);
            if (!string.IsNullOrWhiteSpace(gender)) facts.Add("性别：" + gender);

            var birthday = detail.BirthDateText();
            if (!string.IsNullOrWhiteSpace(birthday)) facts.Add("生日：" + birthday);

            foreach (var key in new[] { "身高", "体重", "血型", "星座", "职业", "所属", "声优" })
            {
                var value = BangumiInfobox.First(detail.Infobox, key);
                if (!string.IsNullOrWhiteSpace(value)) facts.Add(key + "：" + value);
            }

            var bloodType = BloodTypeText(detail.BloodType);
            if (!string.IsNullOrWhiteSpace(bloodType) &&
                string.IsNullOrWhiteSpace(BangumiInfobox.First(detail.Infobox, "血型")))
            {
                facts.Add("血型：" + bloodType);
            }

            var aliases = detail.Aliases()
                .Where(alias => !string.Equals(alias, detail.Name, StringComparison.Ordinal))
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

        /// <summary>Infobox wins over the API field, which is the untranslated male / female.</summary>
        private static string GenderText(string fromInfobox, string apiValue)
        {
            if (!string.IsNullOrWhiteSpace(fromInfobox)) return fromInfobox;
            if (string.IsNullOrWhiteSpace(apiValue)) return null;
            if (string.Equals(apiValue, "male", StringComparison.OrdinalIgnoreCase)) return "男";
            if (string.Equals(apiValue, "female", StringComparison.OrdinalIgnoreCase)) return "女";
            return apiValue;
        }

        private static string BloodTypeText(int? bloodType)
        {
            if (!bloodType.HasValue) return null;
            switch (bloodType.Value)
            {
                case 1: return "A";
                case 2: return "B";
                case 3: return "AB";
                case 4: return "O";
                default: return null;
            }
        }

        private static RemoteSearchResult ToSearchResult(BangumiCharacterDetail character, PluginOptions options)
        {
            var chinese = character.ChineseName();
            var result = new RemoteSearchResult
            {
                Name = options.PreferChineseTitle && !string.IsNullOrWhiteSpace(chinese)
                    ? chinese
                    : character.Name,
                SearchProviderName = BangumiConstants.PluginName,
                Overview = character.Summary,
                ImageUrl = character.Images == null ? null : character.Images.Thumbnail(),
            };

            if (!string.IsNullOrWhiteSpace(character.Name)) result.OriginalTitle = character.Name;

            result.ProviderIds[BangumiConstants.CharacterProviderId] =
                character.Id.ToString(CultureInfo.InvariantCulture);

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

            var gender = GenderText(BangumiInfobox.First(detail.Infobox, "性别"), detail.Gender);
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

            BangumiImages source = null;
            var label = "person";
            var id = 0;

            int personId;
            if (TryGetId(item.ProviderIds, BangumiConstants.PersonProviderId, out personId))
            {
                var person = await Api.GetPersonAsync(personId, cancellationToken).ConfigureAwait(false);
                if (person != null) source = person.Images;
                id = personId;
            }

            int characterId;
            if (source == null && TryGetId(item.ProviderIds, BangumiConstants.CharacterProviderId, out characterId))
            {
                // Rows standing in for an uncredited role are backed by a character, whose portrait
                // lives behind a different endpoint.
                var character = await Api.GetCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
                if (character != null) source = character.Images;
                label = "character";
                id = characterId;
            }

            if (source == null) return images;

            var url = source.Best();
            if (string.IsNullOrWhiteSpace(url)) return images;

            images.Add(new RemoteImageInfo
            {
                ProviderName = BangumiConstants.PluginName,
                Type = ImageType.Primary,
                Url = url,
                ThumbnailUrl = source.Thumbnail(),
            });

            Verbose("Bangumi {0} {1}: offering portrait {2}", label, id, url);
            return images;
        }
    }
}
