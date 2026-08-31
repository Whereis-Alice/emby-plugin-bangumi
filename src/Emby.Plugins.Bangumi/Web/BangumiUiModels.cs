using System.Collections.Generic;

namespace Emby.Plugins.Bangumi.Web
{
    // Response shapes for the plugin own REST endpoints, consumed by Web/Assets/bangumi-ui.js.
    //
    // Why these exist at all: Emby Person model cannot express "character X is voiced by
    // person Y". PersonType has eight members, none of which is "character", and a PersonInfo
    // carries a single Role string. The web client makes that worse - the People array it
    // receives per item only has Name / Id / Role / Type / PrimaryImageTag, with no ProviderIds,
    // so the front end cannot even tell a character row apart from a voice actor row by id.
    // Serving the Bangumi shape directly is the only way to render a real character section.
    //
    // Property names are PascalCase on purpose: the Emby service serialiser emits them verbatim
    // and bangumi-ui.js reads them as written.

    /// <summary>One key of a wiki infobox, flattened for display.</summary>
    public class BangumiUiInfoboxEntry
    {
        public string Key { get; set; }

        public List<string> Values { get; set; }
    }

    public class BangumiUiTag
    {
        public string Name { get; set; }

        public int Count { get; set; }
    }

    /// <summary>A voice actor, staff member, company or band.</summary>
    public class BangumiUiPerson
    {
        public int Id { get; set; }

        public string Name { get; set; }

        /// <summary>The Chinese infobox name when one was looked up, otherwise null.</summary>
        public string NameCn { get; set; }

        public string Image { get; set; }

        public string Url { get; set; }

        /// <summary>1 individual, 2 corporation, 3 band.</summary>
        public int Type { get; set; }

        /// <summary>Jobs on this subject (staff) or characters voiced on it (cast).</summary>
        public List<string> Roles { get; set; }

        /// <summary>Episode range the credit applies to, when Bangumi records one.</summary>
        public string Eps { get; set; }
    }

    /// <summary>A character plus the people who voice it.</summary>
    public class BangumiUiCharacter
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string NameCn { get; set; }

        /// <summary>Main / supporting / guest, in Bangumi wording.</summary>
        public string Relation { get; set; }

        /// <summary>1 character, 2 mechanic, 3 ship, 4 organization.</summary>
        public int Type { get; set; }

        public string Image { get; set; }

        public string Url { get; set; }

        public List<BangumiUiPerson> Actors { get; set; }
    }

    /// <summary>Staff sharing one Bangumi job title.</summary>
    public class BangumiUiStaffGroup
    {
        public string Position { get; set; }

        public List<BangumiUiPerson> Persons { get; set; }
    }

    /// <summary>A prequel, sequel, theme song album, side story ...</summary>
    public class BangumiUiRelated
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string NameCn { get; set; }

        /// <summary>Sequel / prequel / side story / opening theme / ...</summary>
        public string Relation { get; set; }

        /// <summary>Bangumi subject type: 1 book, 2 anime, 3 music, 4 game, 6 real.</summary>
        public int Type { get; set; }

        public string Image { get; set; }

        public string Url { get; set; }
    }

    /// <summary>Everything the item page needs, in one round trip.</summary>
    public class BangumiUiDetail
    {
        /// <summary>The Emby item the caller asked about.</summary>
        public long ItemId { get; set; }

        /// <summary>The Emby item the subject id was read from, after walking up parents.</summary>
        public long ResolvedItemId { get; set; }

        public int SubjectId { get; set; }

        public string SubjectUrl { get; set; }

        public string Name { get; set; }

        public string NameCn { get; set; }

        /// <summary>TV / OVA / WEB / movie.</summary>
        public string Platform { get; set; }

        /// <summary>"yyyy-MM-dd" as Bangumi stores it; may be partial or empty.</summary>
        public string AirDate { get; set; }

        /// <summary>Broadcast weekday from the infobox.</summary>
        public string AirWeekday { get; set; }

        public int TotalEpisodes { get; set; }

        public double RatingScore { get; set; }

        public int RatingRank { get; set; }

        public int RatingTotal { get; set; }

        public string Summary { get; set; }

        public List<BangumiUiTag> Tags { get; set; }

        public List<BangumiUiCharacter> Characters { get; set; }

        public List<BangumiUiPerson> VoiceActors { get; set; }

        public List<BangumiUiStaffGroup> StaffGroups { get; set; }

        public List<BangumiUiRelated> Related { get; set; }

        /// <summary>Which sections the client may draw, mirroring the plugin options.</summary>
        public BangumiUiLayout Layout { get; set; }
    }

    /// <summary>
    /// Server-side switches handed to the client so that the injected script has no
    /// configuration of its own to keep in sync.
    /// </summary>
    public class BangumiUiLayout
    {
        public bool ShowCharacters { get; set; }

        public bool ShowVoiceActors { get; set; }

        public bool ShowStaffGroups { get; set; }

        public bool ShowRelated { get; set; }

        public bool ShowRating { get; set; }

        public bool ShowTags { get; set; }

        /// <summary>Hide the built-in combined cast and crew row once our sections render.</summary>
        public bool HideNativePeople { get; set; }

        /// <summary>Group the character section by main / supporting / guest.</summary>
        public bool GroupCharactersByRelation { get; set; }

        /// <summary>Fetch bgm.tv artwork through the server instead of straight from the browser.</summary>
        public bool ProxyImages { get; set; }
    }

    /// <summary>GET /Bangumi/Characters/{Id} and /Bangumi/Persons/{Id}: the popup payload.</summary>
    public class BangumiUiEntity
    {
        public int Id { get; set; }

        public int Type { get; set; }

        public string Name { get; set; }

        public string NameCn { get; set; }

        public string Image { get; set; }

        public string Url { get; set; }

        public string Summary { get; set; }

        public string Gender { get; set; }

        /// <summary>A / B / AB / O, decoded from the Bangumi 1-4 code.</summary>
        public string BloodType { get; set; }

        /// <summary>Readable even when Bangumi only knows part of the date.</summary>
        public string BirthDate { get; set; }

        /// <summary>Persons only.</summary>
        public string DeathDate { get; set; }

        /// <summary>Persons only.</summary>
        public string BirthPlace { get; set; }

        /// <summary>Persons only: producer / mangaka / artist / seiyu / writer / ...</summary>
        public List<string> Career { get; set; }

        public List<string> Aliases { get; set; }

        public List<BangumiUiInfoboxEntry> Infobox { get; set; }
    }
}
