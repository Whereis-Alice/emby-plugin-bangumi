using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Plugins.Bangumi.Utils
{
    /// <summary>
    /// Result of cleaning a title that Emby handed to a provider.
    /// </summary>
    internal sealed class NormalizedTitle
    {
        /// <summary>Cleaned title with any season marker removed. Best base keyword.</summary>
        public string Keyword { get; set; }

        /// <summary>Cleaned title with the season marker left in place.</summary>
        public string KeywordWithSeason { get; set; }

        /// <summary>Season number parsed out of the title, when the title carried one.</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>True when <see cref="Keyword"/> differs from <see cref="KeywordWithSeason"/>.</summary>
        public bool HasSeasonMarker
        {
            get { return SeasonNumber.HasValue && !string.Equals(Keyword, KeywordWithSeason, StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// Turns folder / file derived titles into keywords that Bangumi search can actually match.
    ///
    /// Emby normally hands over a reasonably clean series name, but libraries fed by an
    /// auto-download pipeline routinely keep release group prefixes and encoding tags, and
    /// Bangumi's search is unforgiving about those. Everything here is intentionally
    /// conservative: when a rule would empty the string, the previous value is kept.
    /// </summary>
    internal static class TitleNormalizer
    {
        // [ANi] / 【喵萌奶茶屋】 style groups, plus stray parenthesised technical blocks.
        private static readonly Regex BracketBlock =
            new Regex(@"[\[\【][^\]\】]*[\]\】]", RegexOptions.Compiled);

        private static readonly Regex ParenBlock =
            new Regex(@"[\(\（][^\)\）]*[\)\）]", RegexOptions.Compiled);

        // Tokens that never belong to a real title. Kept deliberately narrow: OVA / OAD / SP /
        // Movie are NOT here because they are part of legitimate Bangumi subject names.
        private static readonly Regex JunkTokens = new Regex(
            @"(?<![A-Za-z0-9])(?:" +
            @"2160p|1080p|1080i|720p|480p|4k|8k|uhd|" +
            @"x264|x265|h\.?264|h\.?265|hevc|avc|vp9|av1|ma10p|ma444|hi10p|10bit|8bit|yuv420p10|" +
            @"aac|aacx2|flac|opus|ac3|eac3|ddp|dts|truehd|atmos|" +
            @"bdrip|bdrip2|blu-?ray|bd|dvdrip|dvdiso|dvd|webrip|web-?dl|webdl|web|hdtv|tvrip|remux|" +
            @"crf\d*|sdr|hdr10|hdr|dolby|" +
            @"repack|v0|v2|v3|dual-?audio|" +
            @"chs|cht|jpsc|jptc|big5|sc&tc|baha|b-?global|" +
            @"简日双语|繁日双语|简繁日|简繁内封|简繁外挂|简繁|简体|繁體|繁体|简中|繁中|" +
            @"内封字幕|内嵌字幕|外挂字幕|内封|内嵌|外挂|双语字幕|双语|字幕组|字幕社|生肉|无修|招募" +
            @")(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex KnownExtension = new Regex(
            @"\.(?:mkv|mp4|m2ts|ts|avi|mov|wmv|flv|rmvb|webm|iso|nfo|ass|srt|sup|sub|idx)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TrailingYear =
            new Regex(@"\s*[\(\（]\s*(19|20)\d{2}\s*[\)\）]\s*$", RegexOptions.Compiled);

        private static readonly Regex Whitespace = new Regex(@"\s{2,}", RegexOptions.Compiled);

        // Libraries that force a manual sort order prefix folder names with a zero padded number
        // ("000 仙逆", "010 Fate"). Only zero-leading runs are stripped, so "20 Century Boys" and
        // "5 Centimeters per Second" survive untouched.
        private static readonly Regex SortOrderPrefix =
            new Regex(@"^0\d*\s+(?=\S)", RegexOptions.Compiled);

        // ani-rss / AutoBangumi style library folders are named after the broadcast season:
        // "2026年7月 穹庐下的魔女", "2025年10月新番 罪恶之渊". The prefix is never part of the
        // real title and wrecks Bangumi search, so it is removed before anything else.
        private static readonly Regex AirSeasonPrefix =
            new Regex(@"^\s*(?:19|20)\d{2}\s*年\s*\d{1,2}\s*月(?:新番|番)?\s*", RegexOptions.Compiled);

        // Decorative separators fansub groups sprinkle around titles: ★Title★, ♪Title♪ ...
        private static readonly Regex Decoration =
            new Regex(@"[★☆♥♡♪◆◇■□●○※◎→←▼▲‡†]", RegexOptions.Compiled);

        // Season markers, most specific first. Group "n" holds the number.
        private static readonly Regex[] SeasonPatterns =
        {
            new Regex(@"\s*第\s*(?<n>[0-9０-９一二三四五六七八九十百IVXⅠ-Ⅹ]+)\s*[季期部]\s*", RegexOptions.Compiled),
            new Regex(@"\s+(?<n>\d{1,2})(?:st|nd|rd|th)\s+season\b\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"\s+season\s*(?<n>\d{1,2})\b\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"\s+(?<n>\d{1,2})(?:st|nd|rd|th)\s+シーズン\s*", RegexOptions.Compiled),
            new Regex(@"\s+S(?<n>\d{1,2})(?![0-9A-Za-z])\s*", RegexOptions.Compiled),
        };

        // Trailing roman numerals ("Clevatess II"). Only matched at the very end so that
        // titles such as "Vivy -Fluorite Eye's Song-" are untouched.
        private static readonly Regex TrailingRoman =
            new Regex(@"\s+(?<r>X|IX|VIII|VII|VI|V|IV|III|II)\s*$", RegexOptions.Compiled);

        private static readonly Regex TrailingFullWidthRoman =
            new Regex(@"\s*(?<r>[ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ])\s*$", RegexOptions.Compiled);

        // A roman numeral that introduces a subtitle rather than ending the title:
        // "Clevatess II-魔兽之王与虚假的勇者传承-", "Kara no Kyoukai II: ...". Single letter
        // numerals (I, V, X) are excluded on purpose - "X" and "V" are real anime titles - and the
        // numeral must be preceded by text and followed by a separator or a CJK character, so
        // "Vivy -Fluorite Eye's Song-" and "IS <Infinite Stratos>" are never touched.
        private static readonly Regex InnerRoman = new Regex(
            @"(?<=\S)\s+(?<r>IX|VIII|VII|VI|IV|III|II)(?=\s*[-–—:：~～]|\s*[\u3040-\u30FF\u4E00-\u9FFF])",
            RegexOptions.Compiled);

        private static readonly Regex InnerFullWidthRoman = new Regex(
            @"(?<=\S)\s*(?<r>[ⅡⅢⅣⅥⅦⅧⅨ])(?=\s*[-–—:：~～]|\s*[\u3040-\u30FF\u4E00-\u9FFF])",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, int> RomanValues =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "I", 1 }, { "II", 2 }, { "III", 3 }, { "IV", 4 }, { "V", 5 },
                { "VI", 6 }, { "VII", 7 }, { "VIII", 8 }, { "IX", 9 }, { "X", 10 },
                { "Ⅰ", 1 }, { "Ⅱ", 2 }, { "Ⅲ", 3 }, { "Ⅳ", 4 }, { "Ⅴ", 5 },
                { "Ⅵ", 6 }, { "Ⅶ", 7 }, { "Ⅷ", 8 }, { "Ⅸ", 9 }, { "Ⅹ", 10 },
            };

        // Bangumi spells these out ("乱马1/2"), release names use the glyph ("乱马½").
        private static readonly Dictionary<char, string> VulgarFractions = new Dictionary<char, string>
        {
            { '½', "1/2" }, { '⅓', "1/3" }, { '⅔', "2/3" }, { '¼', "1/4" },
            { '¾', "3/4" }, { '⅕', "1/5" }, { '⅙', "1/6" }, { '⅛', "1/8" },
        };

        // Episode markers left at the end of a media file name once the bracket blocks are gone:
        // "罪恶之渊 S01E01", "Harem Camp! - 03.5", "あかね噺 第03話", "Title EP12v2". Only ever applied
        // to file derived titles, and only at the end, so a title that genuinely ends in a number
        // ("Persona 5", "Steins;Gate 0") is safe as long as a separator is not in front of it.
        private static readonly Regex[] EpisodeMarkers =
        {
            new Regex(@"\s*[Ss]\d{1,2}\s*[Ee]\d{1,4}(?:\s*[-~]\s*[Ee]?\d{1,4})?\s*$", RegexOptions.Compiled),
            new Regex(@"\s*第\s*\d{1,4}(?:\s*[-~]\s*\d{1,4})?\s*[话話集回]\s*$", RegexOptions.Compiled),
            new Regex(@"\s*[-–—]\s*(?:[Ee][Pp]?)?\d{1,4}(?:\.\d)?(?:[vV]\d)?\s*$", RegexOptions.Compiled),
            new Regex(@"\s+[Ee][Pp]\s*\d{1,4}(?:[vV]\d)?\s*$", RegexOptions.Compiled),
            new Regex(@"\s*[-–—]\s*\d{1,4}\s*[-~]\s*\d{1,4}\s*$", RegexOptions.Compiled),
        };

        // A bare number at the very end. Emby reads this fine on its own in most layouts, so it is
        // only reached for names it already gave up on, e.g. "【2月】名侦探光之美少女！ 29".
        private static readonly Regex TrailingBareNumber = new Regex(
            @"(?:^|[\s\.\-_\[\(【])(?<n>\d{1,4})(?:[vV]\d)?\s*$", RegexOptions.Compiled);

        // Bracket-heavy release names where Emby's own resolver gives up entirely, e.g.
        // "[GM-Team][国漫][诛仙 第4季][Jade Dynasty Ⅳ][2026][01][HEVC][GB][4K]". The episode number is
        // a bracket block that contains nothing but the number.
        private static readonly Regex BracketedNumber = new Regex(
            @"[\[【]\s*(?<n>\d{1,4})\s*[\]】]", RegexOptions.Compiled);

        // Applied in order, and only to a name Emby failed to number. Anchored loosely on purpose:
        // unlike EpisodeMarkers these do not have to sit at the end of the name.
        private static readonly Regex[] EpisodeNumberPatterns =
        {
            new Regex(@"[Ss]\d{1,2}\s*[Ee](?<n>\d{1,4})(?![\d])", RegexOptions.Compiled),
            new Regex(@"第\s*(?<n>\d{1,4})\s*[话話集回]", RegexOptions.Compiled),
            new Regex(@"(?:^|[\s\.\-_\[\(])[Ee][Pp]?\s*(?<n>\d{1,4})(?:[vV]\d)?(?![\d])", RegexOptions.Compiled),
            new Regex(@"[\-–—]\s*(?<n>\d{1,4})(?:[vV]\d)?\s*(?:$|[\s\.\[\(])", RegexOptions.Compiled),
        };

        // A bracket block holding one of these is a resolution / colour depth / year, not an episode.
        private static readonly int[] NotEpisodeNumbers = { 480, 576, 720, 1080, 1440, 2160, 4320 };

        // OP / ED / preview / bonus material. Numbering these would push them into the main run.
        private static readonly Regex SpecialMarker = new Regex(
            @"(?:^|[^A-Za-z])(?:NC(?:OP|ED)|OP|ED|SP|PV|CM|MENU|TRAILER|PREVIEW|CREDITLESS)\d{0,2}(?:$|[^A-Za-z])|" +
            @"特典|映像特典|番外編|番外篇|預告|预告|予告|图集|圖集|画集|畫集|舞蹈|变身|變身|技能集|花絮|幕后|幕後|SPECIAL|ノンクレジット|メニュー",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<char, int> CjkDigits = new Dictionary<char, int>
        {
            { '零', 0 }, { '一', 1 }, { '二', 2 }, { '三', 3 }, { '四', 4 },
            { '五', 5 }, { '六', 6 }, { '七', 7 }, { '八', 8 }, { '九', 9 },
        };

        public static NormalizedTitle Normalize(string rawTitle)
        {
            var result = new NormalizedTitle();
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                result.Keyword = string.Empty;
                result.KeywordWithSeason = string.Empty;
                return result;
            }

            var text = Clean(rawTitle);
            result.KeywordWithSeason = text;

            int? season;
            var stripped = StripSeason(text, out season);
            result.SeasonNumber = season;
            result.Keyword = string.IsNullOrWhiteSpace(stripped) ? text : stripped;
            return result;
        }

        /// <summary>
        /// Same as <see cref="Normalize"/> but also drops the trailing episode marker, for titles
        /// derived from a media file name rather than from a folder or from Emby's own item name.
        /// </summary>
        public static NormalizedTitle NormalizeFileName(string fileName)
        {
            return Normalize(StripEpisodeMarker(Clean(fileName)));
        }

        /// <summary>
        /// Removes one trailing episode marker. Returns the input unchanged when no marker is
        /// present or when removing it would leave nothing behind.
        /// </summary>
        public static string StripEpisodeMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            foreach (var pattern in EpisodeMarkers)
            {
                var match = pattern.Match(text);
                if (!match.Success) continue;

                var candidate = text.Substring(0, match.Index).Trim();
                if (candidate.Length == 0) continue;

                return candidate;
            }

            return text;
        }

        /// <summary>
        /// Half episodes ("[15.5]", "- 03.5"). <c>IndexNumber</c> is an int, so the fraction is
        /// unrepresentable in a regular season; the number is only usable as a specials index.
        /// </summary>
        private static readonly Regex FractionalEpisode = new Regex(
            @"(?:^|[\s\.\-_\[\(])(?<n>\d{1,4})\.5(?:$|[\s\.\-_\]\)vV])",
            RegexOptions.Compiled);

        /// <summary>
        /// Last-resort episode number for a file Emby's own resolver could not number.
        ///
        /// Emby decides <c>IndexNumber</c> long before any provider runs, and when it fails the
        /// episode has no number at all - not a wrong one - so every number based lookup is dead and
        /// the file also drops out of its season in the UI. Bracket-only release names are the usual
        /// cause: "[GM-Team][国漫][诛仙 第4季][Jade Dynasty Ⅳ][2026][01][HEVC][GB][4K]" carries the
        /// number in a bracket block, which Emby does not read as an episode number.
        ///
        /// Deliberately conservative: OP / ED / bonus material returns null rather than a guess,
        /// half episodes ("- 03.5") return null because <c>IndexNumber</c> is an int and rounding
        /// would collide with a real episode, and a bracket block that looks like a year or a
        /// resolution is skipped.
        /// </summary>
        public static int? ParseEpisodeNumber(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var text = ToHalfWidth(KnownExtension.Replace(fileName.Trim(), string.Empty));
            if (text.Length == 0) return null;
            if (SpecialMarker.IsMatch(text)) return null;

            // A fractional episode cannot be represented, and guessing would overwrite a real one.
            // BangumiEpisodeProvider picks these up separately via ParseFractionalEpisodeBase.
            if (FractionalEpisode.IsMatch(text)) return null;

            foreach (var pattern in EpisodeNumberPatterns)
            {
                var match = pattern.Match(text);
                if (!match.Success) continue;

                var value = ParsePositiveInt(match.Groups["n"].Value);
                if (value.HasValue) return value;
            }

            var trailing = TrailingBareNumber.Match(text);
            if (trailing.Success)
            {
                var raw = trailing.Groups["n"].Value;
                var value = ParsePositiveInt(raw);
                if (IsPlausibleEpisodeNumber(raw, value)) return value;
            }

            // Bracket blocks last: they are the weakest signal, so anything that could be a year or
            // a resolution is discarded instead of being treated as episode 1080.
            foreach (Match match in BracketedNumber.Matches(text))
            {
                var raw = match.Groups["n"].Value;
                var value = ParsePositiveInt(raw);
                if (IsPlausibleEpisodeNumber(raw, value)) return value;
            }

            return null;
        }

        /// <summary>
        /// Integer part of a half episode ("[15.5]" -> 15), or null when the name carries no
        /// fraction. Only meaningful together with <c>ParentIndexNumber = 0</c>: filed as special
        /// 15 it cannot collide with the regular episode 15 that the fraction sits between.
        /// </summary>
        public static int? ParseFractionalEpisodeBase(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var text = ToHalfWidth(KnownExtension.Replace(fileName.Trim(), string.Empty));
            if (text.Length == 0) return null;

            var match = FractionalEpisode.Match(text);
            if (!match.Success) return null;

            return ParsePositiveInt(match.Groups["n"].Value);
        }

        /// <summary>
        /// Guard for the two weak sources - a bare trailing number and a bracket block - where a
        /// release year or a vertical resolution is just as likely as an episode number.
        /// </summary>
        private static bool IsPlausibleEpisodeNumber(string raw, int? value)
        {
            if (!value.HasValue) return false;
            if (raw.Length == 4 && value.Value >= 1900 && value.Value <= 2099) return false;
            return Array.IndexOf(NotEpisodeNumbers, value.Value) < 0;
        }

        private static int? ParsePositiveInt(string text)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
            if (value < 0 || value > 9999) return null;
            return value;
        }

        /// <summary>Cleaning only, no season handling. Exposed for tests and logging.</summary>
        public static string Clean(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle)) return string.Empty;

            var text = ToHalfWidth(rawTitle.Trim());
            text = KnownExtension.Replace(text, string.Empty);

            var withoutAirSeason = AirSeasonPrefix.Replace(text, string.Empty);
            if (!string.IsNullOrWhiteSpace(withoutAirSeason)) text = withoutAirSeason;

            var withoutBrackets = BracketBlock.Replace(text, " ");
            if (!string.IsNullOrWhiteSpace(withoutBrackets)) text = withoutBrackets;

            // Parenthesised blocks are only dropped when they look technical or like a year;
            // "Fate/kaleid liner Prisma Illya (2013)" keeps its title, "(BD 1080p)" does not.
            text = ParenBlock.Replace(text, delegate(Match m)
            {
                var inner = m.Value.Trim('(', ')', '（', '）').Trim();
                if (inner.Length == 0) return " ";
                if (Regex.IsMatch(inner, @"^(19|20)\d{2}$")) return " ";
                if (JunkTokens.IsMatch(inner)) return " ";
                return m.Value;
            });

            text = TrailingYear.Replace(text, string.Empty);

            var withoutJunk = JunkTokens.Replace(text, " ");
            if (!string.IsNullOrWhiteSpace(withoutJunk)) text = withoutJunk;

            // Separators used by release names. Dots are only treated as separators when the
            // string has no spaces at all, otherwise "Re:Zero -Starting Life..." style titles
            // with legitimate punctuation get mangled.
            if (text.IndexOf(' ') < 0)
            {
                text = text.Replace('.', ' ').Replace('_', ' ');
            }
            else
            {
                text = text.Replace('_', ' ');
            }

            text = text.Replace('\t', ' ');
            text = Decoration.Replace(text, " ");
            text = Whitespace.Replace(text, " ").Trim();

            // Leading junk can be trimmed freely, but a trailing dash is often part of the title
            // itself ("Vivy -Fluorite Eye's Song-"), so it is only dropped when unbalanced.
            var withoutSortPrefix = SortOrderPrefix.Replace(text, string.Empty);
            if (!string.IsNullOrWhiteSpace(withoutSortPrefix)) text = withoutSortPrefix;

            text = text.TrimStart('-', '_', '~', '/', '|', '+', '.', ' ');
            text = text.TrimEnd('_', '/', '|', '+', '.', ' ');
            if (text.EndsWith("-", StringComparison.Ordinal) &&
                text.IndexOf('-') == text.Length - 1)
            {
                text = text.Substring(0, text.Length - 1);
            }

            return Whitespace.Replace(text, " ").Trim();
        }

        /// <summary>
        /// Removes a season marker from <paramref name="text"/> and reports the number it carried.
        /// </summary>
        public static string StripSeason(string text, out int? seasonNumber)
        {
            seasonNumber = null;
            if (string.IsNullOrWhiteSpace(text)) return text;

            foreach (var pattern in SeasonPatterns)
            {
                var match = pattern.Match(text);
                if (!match.Success) continue;

                var parsed = ParseNumber(match.Groups["n"].Value);
                if (!parsed.HasValue) continue;

                var candidate = (text.Substring(0, match.Index) + " " +
                                 text.Substring(match.Index + match.Length)).Trim();
                candidate = Whitespace.Replace(candidate, " ").Trim();
                if (candidate.Length == 0) continue;

                seasonNumber = parsed;
                return candidate;
            }

            var roman = TrailingFullWidthRoman.Match(text);
            if (!roman.Success) roman = TrailingRoman.Match(text);
            if (roman.Success)
            {
                int value;
                if (RomanValues.TryGetValue(roman.Groups["r"].Value, out value) && value > 1)
                {
                    var candidate = text.Substring(0, roman.Index).Trim();
                    if (candidate.Length > 0)
                    {
                        seasonNumber = value;
                        return candidate;
                    }
                }
            }

            var inner = InnerFullWidthRoman.Match(text);
            if (!inner.Success) inner = InnerRoman.Match(text);
            if (inner.Success)
            {
                int value;
                if (RomanValues.TryGetValue(inner.Groups["r"].Value, out value) && value > 1)
                {
                    // "Clevatess II-魔兽之王…" must collapse back to "Clevatess-魔兽之王…", not leave a
                    // dangling space in front of the subtitle separator.
                    var tail = text.Substring(inner.Index + inner.Length);
                    var joiner = tail.Length > 0 && "-–—:：~～".IndexOf(tail[0]) >= 0 ? string.Empty : " ";
                    var candidate = (text.Substring(0, inner.Index) + joiner + tail).Trim();
                    candidate = Whitespace.Replace(candidate, " ").Trim();
                    if (candidate.Length > 0)
                    {
                        seasonNumber = value;
                        return candidate;
                    }
                }
            }

            return text;
        }

        /// <summary>Parses an ASCII, full-width or CJK numeral. Returns null when unparsable.</summary>
        public static int? ParseNumber(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = ToHalfWidth(raw).Trim();

            // "第Ⅱ期" / "第II期" are as common as "第2期" on Bangumi.
            int roman;
            if (RomanValues.TryGetValue(text, out roman)) return roman;

            int direct;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out direct))
            {
                return direct >= 0 && direct <= 999 ? (int?)direct : null;
            }

            // CJK numerals up to 999: 十, 十二, 二十, 二十三, 百二十 ...
            var total = 0;
            var section = 0;
            var sawDigit = false;
            foreach (var ch in text)
            {
                int digit;
                if (CjkDigits.TryGetValue(ch, out digit))
                {
                    section = digit;
                    sawDigit = true;
                }
                else if (ch == '十')
                {
                    total += (section == 0 ? 1 : section) * 10;
                    section = 0;
                    sawDigit = true;
                }
                else if (ch == '百')
                {
                    total += (section == 0 ? 1 : section) * 100;
                    section = 0;
                    sawDigit = true;
                }
                else
                {
                    return null;
                }
            }

            if (!sawDigit) return null;
            return total + section;
        }

        /// <summary>Full-width ASCII and the ideographic space folded to their half-width forms.</summary>
        public static string ToHalfWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var builder = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                string fraction;
                if (ch >= '\uFF01' && ch <= '\uFF5E') builder.Append((char)(ch - 0xFEE0));
                else if (ch == '\u3000') builder.Append(' ');
                else if (VulgarFractions.TryGetValue(ch, out fraction)) builder.Append(fraction);
                else builder.Append(ch);
            }

            return builder.ToString();
        }
    }
}