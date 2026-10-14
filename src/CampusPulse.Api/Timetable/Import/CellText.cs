using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CampusPulse.Domain.Timetable;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>
/// Reads the text of one timetable cell, in the shapes the faculty writes them (examples invented):
/// <c>Geometria fractalilor (c) A. Testescu ⏎ saptamanile 1-10, sala K210 - NORD</c>,
/// <c>LSV (l) R426</c>, <c>XT (s) s. 1-7 s. impara, sala Q110</c>,
/// <c>ITSR (l) [R+E] sala R521 Marti 10-12, Joi 8-10, 10-12</c>.
/// Every pattern here was taken from the 2026-27 semester I files (CampusPulse-ro/api#5).
/// </summary>
public static partial class CellText
{
    /// <summary>The activity mark and where it is: everything before it is the subject, everything
    /// after it the teacher, room and weeks. Null when the cell has none (a note).</summary>
    public static (ActivityKinds Kinds, int Index, int Length)? FindKinds(string text)
    {
        var match = KindsPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var kinds = ActivityKinds.None;
        foreach (var part in match.Groups["kinds"].Value.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries))
        {
            kinds |= part switch
            {
                "c" or "curs" => ActivityKinds.Course,
                "l" or "lab" => ActivityKinds.Lab,
                "s" or "seminar" => ActivityKinds.Seminar,
                "p" or "proiect" => ActivityKinds.Project,
                "c.f." => ActivityKinds.Course | ActivityKinds.Facultative,
                _ => ActivityKinds.None,
            };
        }

        return (kinds, match.Index, match.Length);
    }

    /// <summary>Every activity mark in the text, in order: a cell can hold two activities.</summary>
    public static IReadOnlyList<(ActivityKinds Kinds, int Index, int Length)> FindAllKinds(string text)
    {
        var marks = new List<(ActivityKinds, int, int)>();
        for (var at = 0; at < text.Length && FindKinds(text[at..]) is { } mark; at += mark.Index + mark.Length)
        {
            marks.Add((mark.Kinds, at + mark.Index, mark.Length));
        }

        return marks;
    }

    /// <summary>A subject written without a mark ("Tehnici avansate 707CP"): the line without its room,
    /// teacher and weeks.</summary>
    public static string NameWithoutDetails(string line)
    {
        var name = WithoutWeeks(line);
        name = NamedRoomPattern().Replace(name, " ");
        name = BareRoomPattern().Replace(name, " ");
        name = TeacherPattern().Replace(name, " ");
        return SubjectName(name);
    }

    /// <summary>The subject as written before the mark: tags like <c>[R+E]</c> and stray dashes and
    /// stars removed, whitespace collapsed.</summary>
    public static string SubjectName(string beforeMark)
    {
        var name = TagsPattern().Replace(beforeMark, " ");
        name = Collapse(name).Trim(' ', '-', '–', ':', '*', ',', '.', '`');
        return name;
    }

    /// <summary>The audience tags in a cell (<c>[R]</c>, <c>[E]</c>, <c>[R+E]</c>): which specialisations an
    /// optional lab is for.</summary>
    public static IReadOnlyList<string> Tags(string text) =>
        TagsPattern().Matches(text)
            .SelectMany(m => m.Groups["tags"].Value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>The first teacher name: initials and a surname (<c>A. Testescu</c>, <c>R. E. Mockanu</c>,
    /// <c>I.Fictivu</c>).</summary>
    public static string? Teacher(string afterMark) =>
        TeacherPattern().Match(afterMark) is { Success: true } match ? Collapse(match.Value) : null;

    /// <summary>The room: the word after <c>sala</c>/<c>room</c>, or else the first thing shaped like a
    /// room code. Upper case, no spaces (<c>R418A</c>, <c>707CP</c>, <c>SPM909</c>).</summary>
    public static string? Room(string afterMark)
    {
        var named = NamedRoomPattern().Match(afterMark);
        var code = named.Success ? named.Groups["code"].Value : BareRoomPattern().Match(afterMark) is { Success: true } bare
            ? bare.Value
            : null;
        return code is null ? null : NormalizeRoom(code);
    }

    public static string NormalizeRoom(string code)
    {
        var normalized = WhitespacePattern().Replace(code, "").ToUpperInvariant();
        return normalized.Length > Domain.Timetable.Room.CodeMaxLength ? normalized[..Domain.Timetable.Room.CodeMaxLength] : normalized;
    }

    /// <summary>
    /// The weeks a cell restricts itself to, or null when it names none (then: every week). Lists and
    /// ranges (<c>sapt 1-10</c>, <c>(saptamanile 3, 5, 7, 9)</c>, <c>weeks 5-14</c>, <c>s. 1-7</c>),
    /// a start (<c>începand cu a 2-a săptămână</c>, <c>starting week 2</c>), <c>2h/12sapt</c> (weeks 1-12),
    /// and parity (<c>s. impara</c>, <c>sapt. pare</c>, <c>odd weeks</c>), which narrows the rest:
    /// "s. 1-7 s. impara" is weeks 1, 3, 5, 7.
    /// </summary>
    public static WeekSet? Weeks(string text, int weekCount)
    {
        WeekSet? listed = null;
        foreach (Match match in WeekListPattern().Matches(text))
        {
            listed = (listed ?? WeekSet.None).Union(ParseList(match.Groups["list"].Value));
        }

        if (StartingWeekPattern().Match(text) is { Success: true } start)
        {
            var from = int.Parse(start.Groups["n"].Value, CultureInfo.InvariantCulture);
            listed = (listed ?? WeekSet.None).Union(WeekSet.Range(from, weekCount));
        }

        if (HoursOverWeeksPattern().Match(text) is { Success: true } span)
        {
            listed = (listed ?? WeekSet.None).Union(WeekSet.Range(1, int.Parse(span.Groups["n"].Value, CultureInfo.InvariantCulture)));
        }

        var odd = OddPattern().IsMatch(text);
        var even = EvenPattern().IsMatch(text);
        if (listed is null && odd == even)
        {
            return null;
        }

        var weeks = listed ?? WeekSet.All(weekCount);
        weeks = weeks.Intersect(WeekSet.All(weekCount));
        if (odd && !even)
        {
            weeks = weeks.Intersect(WeekSet.Odd(weekCount));
        }
        else if (even && !odd)
        {
            weeks = weeks.Intersect(WeekSet.Even(weekCount));
        }

        return weeks;
    }

    /// <summary>Whether the text states a parity itself: true for odd weeks, false for even, null for
    /// neither (or both).</summary>
    public static bool? Parity(string text)
    {
        var odd = OddPattern().IsMatch(text);
        var even = EvenPattern().IsMatch(text);
        return odd == even ? null : odd;
    }

    /// <summary>
    /// The text split at commas, semicolons, slashes and line breaks, except inside a week expression:
    /// "(saptamanile 3, 5, 7, 9)" and "2h/12sapt" stay whole.
    /// </summary>
    public static IReadOnlyList<string> Segments(string text)
    {
        var protectedSpans = WeekListPattern().Matches(text).Cast<Match>()
            .Concat(StartingWeekPattern().Matches(text))
            .Concat(HoursOverWeeksPattern().Matches(text))
            .Select(m => (Start: m.Index, End: m.Index + m.Length))
            .ToList();
        var segments = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is ',' or ';' or '\n' or '/' && !protectedSpans.Any(span => i >= span.Start && i < span.End))
            {
                segments.Add(text[start..i]);
                start = i + 1;
            }
        }

        segments.Add(text[start..]);
        return segments;
    }

    /// <summary>The text with every week expression blanked out, so "sapt 1-10" isn't read as 01:00-10:00.</summary>
    public static string WithoutWeeks(string text)
    {
        text = WeekListPattern().Replace(text, m => new string(' ', m.Length));
        text = StartingWeekPattern().Replace(text, m => new string(' ', m.Length));
        return HoursOverWeeksPattern().Replace(text, m => new string(' ', m.Length));
    }

    /// <summary>"1-7", "3, 5, 7, 9", "2 și 4": weeks.</summary>
    public static WeekSet ParseList(string list)
    {
        var weeks = WeekSet.None;
        foreach (var part in ListSeparatorPattern().Split(list))
        {
            var range = part.Split(['-', '–'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (range.Length == 1 && int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var single))
            {
                weeks = weeks.Union(WeekSet.Of([single]));
            }
            else if (range.Length == 2
                && int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                && int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last))
            {
                weeks = weeks.Union(WeekSet.Range(first, last));
            }
        }

        return weeks;
    }

    /// <summary>The weekday a word names (Romanian or English, full or short), or null.</summary>
    public static DayOfWeek? Day(string word) =>
        RemoveDiacritics(word).ToLowerInvariant().TrimEnd('.') switch
        {
            "luni" or "monday" or "mon" => DayOfWeek.Monday,
            "marti" or "tuesday" or "tue" or "tues" => DayOfWeek.Tuesday,
            "miercuri" or "wednesday" or "wed" => DayOfWeek.Wednesday,
            "joi" or "thursday" or "thu" or "thurs" => DayOfWeek.Thursday,
            "vineri" or "friday" or "fri" => DayOfWeek.Friday,
            "sambata" or "saturday" or "sat" => DayOfWeek.Saturday,
            _ => null,
        };

    /// <summary>True when the text names a weekday followed by a time: a schedule written out in words
    /// ("Marti 16-18 R418a"), not a cell placed on the grid.</summary>
    public static bool NamesDayAndTime(string text) => DayAndTimePattern().IsMatch(text);

    /// <summary>
    /// The day/time/room offers written out in a cell: <c>sala R520 Luni 16-18, Miercuri 10-12, Joi 8-10, 10-12</c>
    /// or <c>Tuesday 16-18, 18-20 Wednesday 8-10</c>. Segments are split at commas, semicolons and line
    /// breaks; a room in a segment applies to its times, otherwise the last room seen does (starting from
    /// <paramref name="defaultRoom"/>); a time takes the last day named before it, in or before its segment.
    /// </summary>
    public static IReadOnlyList<WrittenOffer> Offers(string text, string? defaultRoom, int weekCount, WeekSet defaultWeeks)
    {
        var offers = new List<WrittenOffer>();
        var room = defaultRoom;
        DayOfWeek? day = null;
        foreach (var segment in Segments(text))
        {
            var withoutWeeks = WithoutWeeks(segment);
            var segmentRoom = NamedRoomPattern().Match(withoutWeeks) is { Success: true } named
                ? NormalizeRoom(named.Groups["code"].Value)
                : BareRoomPattern().Match(withoutWeeks) is { Success: true } bare ? NormalizeRoom(bare.Value) : null;
            room = segmentRoom ?? room;
            var weeks = Weeks(segment, weekCount) ?? defaultWeeks;
            foreach (Match token in DayOrTimePattern().Matches(withoutWeeks))
            {
                if (token.Groups["day"].Success)
                {
                    day = Day(token.Groups["day"].Value);
                    continue;
                }

                if (day is null || !TryTime(token, out var startsAt, out var endsAt))
                {
                    continue;
                }

                offers.Add(new WrittenOffer(day.Value, startsAt, endsAt, room, weeks));
            }
        }

        return offers;
    }

    private static bool TryTime(Match token, out TimeOnly startsAt, out TimeOnly endsAt)
    {
        startsAt = endsAt = default;
        var h1 = int.Parse(token.Groups["h1"].Value, CultureInfo.InvariantCulture);
        var h2 = int.Parse(token.Groups["h2"].Value, CultureInfo.InvariantCulture);
        var m1 = token.Groups["m1"].Success ? int.Parse(token.Groups["m1"].Value, CultureInfo.InvariantCulture) : 0;
        var m2 = token.Groups["m2"].Success ? int.Parse(token.Groups["m2"].Value, CultureInfo.InvariantCulture) : 0;
        if (h1 is < 7 or > 22 || h2 is < 8 or > 23 || m1 > 59 || m2 > 59)
        {
            return false;
        }

        startsAt = new TimeOnly(h1, m1);
        endsAt = new TimeOnly(h2 == 24 ? 0 : h2, m2);
        return endsAt > startsAt;
    }

    public static string Collapse(string text) => WhitespacePattern().Replace(text, " ").Trim();

    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        // ş/ţ (cedilla) decompose too; this is just a comparison key.
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A comparison key for subject names: no diacritics, lower case, single spaces.</summary>
    public static string NameKey(string name) => Collapse(RemoveDiacritics(name).ToLowerInvariant().Replace('.', ' '));

    [GeneratedRegex(@"\(\s*(?<kinds>(?:c\.f\.|seminar|curs|lab|proiect|c|l|s|p)(?:\s*\+\s*(?:c|l|s|p))*)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KindsPattern();

    [GeneratedRegex(@"\[(?<tags>[A-Za-z]+(?:\s*\+\s*[A-Za-z]+)*)\]")]
    private static partial Regex TagsPattern();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:\p{Lu}\.\s*){1,3}\p{Lu}[\p{L}'’]+(?:-\p{Lu}[\p{L}'’]+)?")]
    private static partial Regex TeacherPattern();

    [GeneratedRegex(@"\b(?:sala|room)\s+(?<code>(?:\d{3}\s?CP|SPM\s?\d{3}[A-Za-z]?|ASPC\s?\d?|[A-Za-z]+-?[A-Za-z]?\d*[A-Za-z]?(?:-CP)?))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedRoomPattern();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:\d{3}\s?CP|P\d{2}-CP|ASPC\s?\d?(?!\d)|SPM\s?\d{3}[a-z]?|MEC-N\d{3}|[A-Z]{1,2}\d{1,3}[A-Za-z]?)(?![\p{L}\d])")]
    private static partial Regex BareRoomPattern();

    [GeneratedRegex(@"(?:\bs[aă]pt(?:[aă]m[aâ]n(?:ile|a|ii))?\.?|\bweeks?|\bwk\.?|\bs\.)\s*(?<list>\d{1,2}(?:\s*(?:[-–,&]|\bși\b|\bsi\b|\band\b)\s*\d{1,2})*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeekListPattern();

    [GeneratedRegex(@"(?:[iî]ncep[aâ]nd\s+cu\s+(?:a\s*)?(?:s[aă]pt\w*\.?\s*)?|din\s+s[aă]pt[aă]m[aâ]na\s+a\s*-?\s*|(?:starting|from)\s+(?:with\s+)?week\s+)(?<n>\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartingWeekPattern();

    [GeneratedRegex(@"\d+\s*h\s*/\s*(?<n>\d{1,2})\s*s[aă]pt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HoursOverWeeksPattern();

    [GeneratedRegex(@"\b(?:impar[ae]?\b|imp\.|odd\s+weeks?\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OddPattern();

    [GeneratedRegex(@"(?:\bs[aă]pt\w*\.?\s*|\bs\.\s*)par[ae]?\b|\beven\s+weeks?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EvenPattern();

    [GeneratedRegex(@"\s*(?:,|;|&|\bși\b|\bsi\b|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListSeparatorPattern();

    [GeneratedRegex(@"\b(?<day>luni|mar[tțţ]i|miercuri|joi|vineri|s[aâ]mb[aă]t[aă]|monday|tuesday|wednesday|thursday|friday|saturday|mon|tues|tue|wed|thurs|thu|fri|sat)\b\.?|(?<![\d:])(?<h1>\d{1,2})(?:[:.](?<m1>\d{2}))?\s*[-–]\s*(?<h2>\d{1,2})(?:[:.](?<m2>\d{2}))?(?![\d:])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayOrTimePattern();

    [GeneratedRegex(@"\b(?:luni|mar[tțţ]i|miercuri|joi|vineri|monday|tuesday|wednesday|thursday|friday|mon|tue|wed|thu|fri)\b\.?,?\s*\d{1,2}(?:[:.]\d{2})?\s*[-–]\s*\d{1,2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayAndTimePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

/// <summary>A day, time and room written out in a cell (an offered slot).</summary>
public sealed record WrittenOffer(DayOfWeek Weekday, TimeOnly StartsAt, TimeOnly EndsAt, string? Room, WeekSet Weeks);
