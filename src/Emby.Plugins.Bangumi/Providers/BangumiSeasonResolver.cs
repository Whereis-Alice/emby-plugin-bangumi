using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Api;
using Emby.Plugins.Bangumi.Utils;

namespace Emby.Plugins.Bangumi.Providers
{
    /// <summary>One subject in a franchise chain, together with the season number its title claims.</summary>
    internal sealed class SubjectChainEntry
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string NameCn { get; set; }

        /// <summary>Season number parsed out of the title, when the title carried one.</summary>
        public int? SeasonMarker { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(NameCn)) return NameCn;
                if (!string.IsNullOrWhiteSpace(Name)) return Name;
                return Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Outcome of mapping an Emby season number onto Bangumi subjects.</summary>
    internal sealed class SeasonResolution
    {
        public SeasonResolution(List<int> subjectIds, string matchedBy)
        {
            SubjectIds = subjectIds ?? new List<int>();
            MatchedBy = matchedBy;
        }

        /// <summary>
        /// Subjects covering the season, in air order. More than one entry means Bangumi split the
        /// season into separate cour subjects while Emby keeps it as a single folder.
        /// </summary>
        public List<int> SubjectIds { get; private set; }

        public int PrimaryId
        {
            get { return SubjectIds.Count > 0 ? SubjectIds[0] : 0; }
        }

        public string MatchedBy { get; private set; }

        public string Describe()
        {
            return string.Join(", ", SubjectIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())
                   + " (" + (MatchedBy ?? "?") + ")";
        }
    }

    /// <summary>
    /// Bangumi has no season concept: every cour is a standalone subject linked to the previous one
    /// through a <c>续集</c> relation. Turning an Emby season number into the right subject therefore
    /// means walking that chain, and the walk cannot simply count hops.
    ///
    /// Measured counter example, the Re:Zero chain:
    /// 140001 (season 1) -> 278826 (第二季) -> 316247 (第二季 後半) -> 425998 (第三季 襲撃編) -> ...
    /// Hop counting puts season 3 on the second half of season 2. What actually works is reading the
    /// season number out of each subject title, and then absorbing the immediately following subjects
    /// that are the same season split into another cour.
    /// </summary>
    internal static class BangumiSeasonResolver
    {
        public const string SequelRelation = "续集";

        /// <summary>Inverse of <see cref="SequelRelation"/>, used to find an earlier cour of the same season.</summary>
        public const string PrequelRelation = "前传";

        /// <summary>Hard stop for chain walking. No anime franchise on Bangumi is deeper than this.</summary>
        private const int MaxChainDepth = 16;

        /// <summary>A season is allowed to span at most this many Bangumi subjects.</summary>
        private const int MaxSubjectsPerSeason = 3;

        /// <summary>
        /// Titles that mark a subject as another slice of the same season rather than a new one.
        /// Used only when the follow up subject carries no season number of its own, so a missing
        /// marker cannot silently merge a genuinely different season.
        /// </summary>
        private static readonly Regex SplitCourHint = new Regex(
            @"(?:後半|后半|前半|後編|後篇|后篇|前編|前篇|第\s*[0-9０-９一二三四五六七八九]+\s*(?:クール|cour)|" +
            @"2nd\s*cour|second\s*cour|part\s*(?:2|two|ii|２)|後期|后期|下巻|下卷|下半)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Walks the <c>续集</c> chain starting at <paramref name="rootId"/>. The root itself is the
        /// first element. Cycles are impossible to rule out in user edited data, hence the visited set.
        /// </summary>
        public static async Task<List<SubjectChainEntry>> BuildChainAsync(
            BangumiApiClient api, int rootId, CancellationToken cancellationToken)
        {
            var chain = new List<SubjectChainEntry>();
            if (api == null || rootId <= 0) return chain;

            var visited = new HashSet<int>();
            visited.Add(rootId);

            var root = await api.GetSubjectAsync(rootId, cancellationToken).ConfigureAwait(false);
            chain.Add(root == null
                ? new SubjectChainEntry { Id = rootId }
                : Entry(root.Id, root.Name, root.NameCn));

            var currentId = rootId;
            for (var depth = 0; depth < MaxChainDepth; depth++)
            {
                var related = await api.GetRelatedSubjectsAsync(currentId, cancellationToken).ConfigureAwait(false);
                if (related == null) break;

                // Ordering by id approximates air order: the related-subjects endpoint returns no
                // dates, and Bangumi ids grow monotonically with registration time.
                var next = related
                    .Where(r => r != null && r.Id > 0 &&
                                r.Type == BangumiConstants.SubjectType.Anime &&
                                string.Equals(r.Relation, SequelRelation, StringComparison.Ordinal) &&
                                !visited.Contains(r.Id))
                    .OrderBy(r => r.Id)
                    .FirstOrDefault();

                if (next == null) break;

                visited.Add(next.Id);
                chain.Add(Entry(next.Id, next.Name, next.NameCn));
                currentId = next.Id;
            }

            return chain;
        }

        /// <summary>Reads a season number out of either title. The Chinese title wins when both carry one.</summary>
        public static int? SeasonMarkerOf(string name, string nameCn)
        {
            int? marker;
            TitleNormalizer.StripSeason(nameCn, out marker);
            if (marker.HasValue) return marker;

            TitleNormalizer.StripSeason(name, out marker);
            return marker;
        }

        /// <summary>
        /// Finds the subject whose title claims <paramref name="seasonNumber"/>, plus any directly
        /// following subjects that are the same season in another cour. Returns null when no title
        /// in the chain claims that season.
        /// </summary>
        public static SeasonResolution ResolveFromChain(List<SubjectChainEntry> chain, int seasonNumber)
        {
            if (chain == null || chain.Count == 0) return null;

            var index = chain.FindIndex(e => e != null && e.SeasonMarker.HasValue && e.SeasonMarker.Value == seasonNumber);
            if (index < 0) return null;

            return Collect(chain, index, "chain marker");
        }

        /// <summary>
        /// Last resort: treat the chain as a plain season list. Correct for the common case of one
        /// subject per season, wrong whenever a season was split, which is why it runs last.
        /// </summary>
        public static SeasonResolution ResolveByOrdinal(List<SubjectChainEntry> chain, int seasonNumber)
        {
            if (seasonNumber < 1) return null;
            return ResolveAt(chain, seasonNumber - 1, "chain ordinal");
        }

        /// <summary>Takes the subject at <paramref name="index"/> plus any following cour of the same season.</summary>
        public static SeasonResolution ResolveAt(List<SubjectChainEntry> chain, int index, string matchedBy)
        {
            if (chain == null || index < 0 || index >= chain.Count) return null;
            return Collect(chain, index, matchedBy);
        }

        /// <summary>
        /// Subjects to search for an episode of a season whose primary subject is
        /// <paramref name="startId"/>. Emby keeps a 25 episode season in one folder even when Bangumi
        /// filed it as 13 + 12, so the following cour subjects have to be searched too.
        /// </summary>
        public static async Task<List<int>> BuildEpisodeCandidatesAsync(
            BangumiApiClient api, int startId, CancellationToken cancellationToken)
        {
            var chain = await BuildChainAsync(api, startId, cancellationToken).ConfigureAwait(false);
            if (chain.Count == 0) return new List<int> { startId };

            var collected = Collect(chain, 0, "episode candidates").SubjectIds;

            // A season is frequently identified through its *second* cour, because that is the subject
            // whose title matches the folder: Re:Zero season 4 is 喪失編 (547888) + 奪還編 (633836) and
            // the folder is named after 奪還編. BuildChainAsync only walks 续集, so the earlier cour
            // stays invisible and every episode that belongs to it fails to match. Walking 前传 first
            // and prepending is what makes the offset arithmetic in the episode provider line up.
            var earlier = await BuildPrecedingCoursAsync(api, chain[0], cancellationToken).ConfigureAwait(false);
            if (earlier.Count == 0) return collected;

            var ids = new List<int>(earlier);
            foreach (var id in collected)
            {
                if (!ids.Contains(id)) ids.Add(id);
            }

            if (ids.Count > MaxSubjectsPerSeason) ids = ids.GetRange(0, MaxSubjectsPerSeason);
            return ids;
        }

        /// <summary>
        /// Every anime subject of the franchise, oldest first. Unlike
        /// <see cref="BuildEpisodeCandidatesAsync"/> this deliberately ignores season boundaries: it
        /// exists so a franchise wide, absolute episode number can be looked up, and Bangumi's
        /// <c>sort</c> field is franchise wide by definition (仙逆 年番3 numbers its episodes ep 1..52
        /// but sort 129..180, which is exactly how GM-Team names the files).
        /// </summary>
        public static async Task<List<int>> BuildFranchiseChainAsync(
            BangumiApiClient api, int anyId, CancellationToken cancellationToken)
        {
            var ids = new List<int>();
            if (api == null || anyId <= 0) return ids;

            var rootId = await FindFranchiseRootAsync(api, anyId, cancellationToken).ConfigureAwait(false);
            var chain = await BuildChainAsync(api, rootId, cancellationToken).ConfigureAwait(false);
            foreach (var entry in chain)
            {
                if (entry != null && entry.Id > 0 && !ids.Contains(entry.Id)) ids.Add(entry.Id);
            }

            // The walk starts from a 前传 hop and can therefore miss the subject it was asked about,
            // whenever that subject is reachable only through a relation this resolver does not follow.
            if (!ids.Contains(anyId)) ids.Add(anyId);
            return ids;
        }

        /// <summary>
        /// Oldest subject reachable from <paramref name="startId"/> through <c>前传</c> links. No season
        /// agreement is required, because the caller wants the whole franchise rather than one season.
        /// </summary>
        private static async Task<int> FindFranchiseRootAsync(
            BangumiApiClient api, int startId, CancellationToken cancellationToken)
        {
            var visited = new HashSet<int>();
            visited.Add(startId);

            var currentId = startId;
            for (var depth = 0; depth < MaxChainDepth; depth++)
            {
                var related = await api.GetRelatedSubjectsAsync(currentId, cancellationToken).ConfigureAwait(false);
                if (related == null) break;

                // Lowest id first: the oldest registration is the earliest work in the franchise.
                var previous = related
                    .Where(r => r != null && r.Id > 0 &&
                                r.Type == BangumiConstants.SubjectType.Anime &&
                                string.Equals(r.Relation, PrequelRelation, StringComparison.Ordinal) &&
                                !visited.Contains(r.Id))
                    .OrderBy(r => r.Id)
                    .FirstOrDefault();

                if (previous == null) break;

                visited.Add(previous.Id);
                currentId = previous.Id;
            }

            return currentId;
        }

        /// <summary>
        /// Earlier cours of the same season as <paramref name="start"/>, oldest first. Only subjects
        /// that agree about the season number, or that carry an explicit split cour hint, are accepted:
        /// a genuine previous season restarts its episode numbering and would shift every offset lookup.
        /// </summary>
        private static async Task<List<int>> BuildPrecedingCoursAsync(
            BangumiApiClient api, SubjectChainEntry start, CancellationToken cancellationToken)
        {
            var earlier = new List<int>();
            if (api == null || start == null || start.Id <= 0) return earlier;

            var visited = new HashSet<int>();
            visited.Add(start.Id);

            var current = start;
            for (var depth = 1; depth < MaxSubjectsPerSeason; depth++)
            {
                var related = await api.GetRelatedSubjectsAsync(current.Id, cancellationToken).ConfigureAwait(false);
                if (related == null) break;

                // Newest first: when several prequels are listed, the immediately preceding cour is
                // the one registered last.
                var previous = related
                    .Where(r => r != null && r.Id > 0 &&
                                r.Type == BangumiConstants.SubjectType.Anime &&
                                string.Equals(r.Relation, PrequelRelation, StringComparison.Ordinal) &&
                                !visited.Contains(r.Id))
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefault();

                if (previous == null) break;

                var entry = Entry(previous.Id, previous.Name, previous.NameCn);

                // Checked both ways round so that an unmarked "後半" subject can still recognise the
                // marked first cour as its own season.
                if (!IsSameSeason(entry, current.SeasonMarker) && !IsSameSeason(current, entry.SeasonMarker)) break;

                visited.Add(entry.Id);
                earlier.Insert(0, entry.Id);
                current = entry;
            }

            return earlier;
        }

        private static SeasonResolution Collect(List<SubjectChainEntry> chain, int index, string matchedBy)
        {
            var primary = chain[index];
            var ids = new List<int> { primary.Id };

            for (var i = index + 1; i < chain.Count && ids.Count < MaxSubjectsPerSeason; i++)
            {
                if (!IsSameSeason(chain[i], primary.SeasonMarker)) break;
                ids.Add(chain[i].Id);
            }

            return new SeasonResolution(ids, matchedBy);
        }

        private static bool IsSameSeason(SubjectChainEntry candidate, int? primaryMarker)
        {
            if (candidate == null) return false;

            if (candidate.SeasonMarker.HasValue)
            {
                return primaryMarker.HasValue && candidate.SeasonMarker.Value == primaryMarker.Value;
            }

            return HasSplitCourHint(candidate.Name) || HasSplitCourHint(candidate.NameCn);
        }

        private static bool HasSplitCourHint(string title)
        {
            return !string.IsNullOrWhiteSpace(title) && SplitCourHint.IsMatch(title);
        }

        private static SubjectChainEntry Entry(int id, string name, string nameCn)
        {
            return new SubjectChainEntry
            {
                Id = id,
                Name = name,
                NameCn = nameCn,
                SeasonMarker = SeasonMarkerOf(name, nameCn),
            };
        }
    }
}