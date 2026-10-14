using System.Globalization;
using System.Text.RegularExpressions;
using CampusPulse.Domain.Timetable;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>
/// Reads the AC faculty's timetable workbooks (CampusPulse-ro/api#5). Every sheet has the same grid:
/// <list type="bullet">
/// <item>Rows: day blocks of hourly rows labelled <c>08-09</c> … <c>20-21</c> in column B, the day's name
/// spelled down column A; blank rows separate the days.</item>
/// <item>Columns: one per subgroup (<c>1.1</c>, <c>1.2</c>, …) in a header row above the grid, under labels
/// naming the series or specialisation. Master sheets have no group columns: one group.</item>
/// <item>A merged cell is one activity for every subgroup it covers, for as many hours as it spans. The
/// text is the subject, a mark like <c>(c)</c> or <c>(l)</c>, the teacher, the room and the weeks, often
/// split over a second cell just below with the same width (the room and weeks).</item>
/// <item>A two-hour activity every other week is written in one row of its two-hour block, alternating
/// with what's in the other row (by default the top row is odd weeks).</item>
/// <item>Columns beside the grid list optional labs, facultative subjects or master labs with the times
/// they're offered at, written out ("Marti 16-18 R418a"); others hold notes.</item>
/// </list>
/// What the layout leaves open (which programme a header means, abbreviations, corrections) comes from
/// the overrides file. Everything uncertain becomes a warning naming its cell; what can't be read at all
/// is an error, and an import with errors writes nothing.
/// </summary>
public sealed partial class SheetParser(ImportOverrides overrides)
{
    /// <summary>Bump when a change would read the same file differently: a workbook is only imported again
    /// when its contents, the overrides or this version change. A test pins it to what the parser reads
    /// from the test workbook, so a change in what it reads fails until this is bumped.</summary>
    public const int Version = 2;

    private readonly int _weekCount = overrides.Calendar.Weeks;
    private readonly HashSet<string> _usedCells = [];
    private readonly HashSet<string> _warnedCells = [];

    /// <summary>Reads one workbook of <paramref name="source"/> (its id in the overrides' sources).</summary>
    public ParsedTimetable Parse(string source, string fileName, IReadOnlyList<SheetGrid> sheets)
    {
        var result = new ParsedTimetable(source, fileName);
        var listed = overrides.Sources[source].Sheets;
        var present = sheets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        result.MissingSheets.AddRange(listed.Where(s => !present.Contains(s)));
        foreach (var sheet in sheets)
        {
            if (!overrides.Sheets.TryGetValue(sheet.Name, out var config))
            {
                result.Warnings.Add($"{sheet.Name}: not in the overrides file (sheets), skipped");
                continue;
            }

            if (config.Skip)
            {
                continue;
            }

            if (!listed.Contains(sheet.Name))
            {
                result.Warnings.Add($"{sheet.Name}: not in sources.{source}.sheets, skipped");
                continue;
            }

            result.Sheets.Add(sheet.Name);
            new SheetReader(this, sheet, config, result).Read();
        }

        foreach (var key in overrides.Cells.Keys.Where(k => result.Sheets.Contains(k.Split('!')[0]) && !_usedCells.Contains(k)))
        {
            result.Warnings.Add($"overrides: cells.\"{key}\" matches no cell that has text");
        }

        return result;
    }

    private sealed class SheetReader(SheetParser parser, SheetGrid sheet, SheetOverrides config, ParsedTimetable result)
    {
        private static readonly HashSet<string> StopWords =
            ["si", "de", "a", "in", "and", "of", "the", "for", "cu", "la", "pe", "din", "to", "al", "ale", "e", "o"];

        private readonly SortedDictionary<int, (int Start, int End)> _hours = [];
        private readonly Dictionary<int, DayOfWeek> _dayOfRow = [];
        private readonly Dictionary<int, int> _blockEnd = [];
        private readonly Dictionary<int, GroupKey> _groupColumns = [];
        private readonly Dictionary<int, ColumnRole> _roles = [];
        private readonly HashSet<GridCell> _consumed = [];
        private readonly Dictionary<string, Dictionary<string, string>> _courseNames = [];
        private readonly Dictionary<(string Programme, string Abbreviation), string> _ownAbbreviations = [];
        private readonly Dictionary<string, string> _aliases =
            config.Aliases.ToDictionary(a => CellText.NameKey(a.Key), a => a.Value.Trim());
        private readonly HashSet<string> _usedAliases = [];
        private readonly HashSet<string> _optional = config.OptionalSubjects.Select(CellText.NameKey).ToHashSet();
        private readonly HashSet<ParsedOffer> _gridOffers = [];
        private readonly HashSet<(GridCell, string)> _warnedOptional = [];
        private GroupKey? _singleGroup;
        private int _firstHourRow;
        private int _lastHourRow;

        private int WeekCount => parser._weekCount;

        private string Name => sheet.Name;

        private void Warn(string message) => result.Warnings.Add($"{Name}: {message}");

        private void Warn(GridCell cell, string message) => result.Warnings.Add($"{cell.Address(Name)}: {message}");

        public void Read()
        {
            if (!ReadLayout())
            {
                return;
            }

            var activities = ReadGridActivities();
            foreach (var activity in activities.Where(a => a.Kinds.HasFlag(ActivityKinds.Course)))
            {
                foreach (var programme in activity.Groups.Select(g => g.Programme).Distinct())
                {
                    RememberCourse(programme, activity.Name, activity.Cell);
                }
            }

            var oneHour = activities.Where(a => a.LastRow == a.Cell.Row && a.Override is not { Start: not null } and not { End: not null } and not { Weeks: not null }).ToList();
            foreach (var activity in activities)
            {
                AddSlots(activity, oneHour);
            }

            foreach (var (column, role) in _roles.OrderBy(r => r.Key))
            {
                if (role is ColumnRole.Optional or ColumnRole.Facultative or ColumnRole.PickSlot)
                {
                    ReadOffers(column, role);
                }
            }

            ReadNotes();

            foreach (var alias in config.Aliases.Keys.Where(a => !_usedAliases.Contains(CellText.NameKey(a))))
            {
                Warn($"overrides: sheets.\"{Name}\".aliases.\"{alias}\" matched nothing");
            }

            foreach (var optional in config.OptionalSubjects.Where(o => !result.Subjects.Keys.Any(k => CellText.NameKey(k.Name) == CellText.NameKey(o))))
            {
                Warn($"overrides: sheets.\"{Name}\".optionalSubjects \"{optional}\" matched no subject");
            }
        }

        /// <summary>Hour rows, day blocks and their weekdays, group columns and column roles.</summary>
        private bool ReadLayout()
        {
            foreach (var cell in sheet.Cells.Where(c => c.Column == 2))
            {
                if (HourLabelPattern().Match(cell.Text) is { Success: true } match)
                {
                    _hours[cell.Row] = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                }
            }

            if (_hours.Count == 0)
            {
                result.Errors.Add($"{Name}: no hour rows (08-09 … in column B)");
                return false;
            }

            var blocks = new List<List<int>>();
            foreach (var row in _hours.Keys)
            {
                if (blocks.Count == 0 || row != blocks[^1][^1] + 1)
                {
                    blocks.Add([]);
                }

                blocks[^1].Add(row);
            }

            // The day each block is: its name spelled down column A (LUNI, MARTI …; MONDAY …).
            var days = blocks
                .Select(block => CellText.Day(string.Concat(block.Select(row => sheet.Text(row, 1).Trim()))))
                .ToList();
            if (days.Any(d => d is null) || days.Distinct().Count() != days.Count)
            {
                if (blocks.Count != 5)
                {
                    result.Errors.Add($"{Name}: {blocks.Count} day blocks, and their names in column A don't say which days they are");
                    return false;
                }

                Warn("the day names in column A can't be read; the five day blocks are read as Monday to Friday, in order");
                days = [.. Enumerable.Range(0, 5).Select(i => (DayOfWeek?)(DayOfWeek.Monday + i))];
            }

            for (var i = 0; i < blocks.Count; i++)
            {
                foreach (var row in blocks[i])
                {
                    _dayOfRow[row] = days[i]!.Value;
                    _blockEnd[row] = blocks[i][^1];
                }
            }

            _firstHourRow = _hours.Keys.First();
            _lastHourRow = _hours.Keys.Last();

            foreach (var (letter, role) in config.ColumnRoles)
            {
                _roles[GridCell.ColumnNumber(letter)] = ImportOverrides.ColumnRoleParse(role, Name);
            }

            var headerRow = Enumerable.Range(1, Math.Max(0, _firstHourRow - 1))
                .Select(row => (Row: row, Count: sheet.Cells.Count(c => c.Row == row && c.Column >= 3 && GroupCodePattern().IsMatch(c.Text))))
                .Where(r => r.Count > 0)
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Row)
                .Select(r => (int?)r.Row)
                .FirstOrDefault();

            if (headerRow is null)
            {
                if (string.IsNullOrWhiteSpace(config.Programme))
                {
                    result.Errors.Add($"{Name}: no group header (1.1, 1.2 …) and no programme for it in the overrides");
                    return false;
                }

                _singleGroup = new GroupKey(config.Programme, config.Year, config.Series ?? "", 1, 0);
                result.Groups.Add(_singleGroup);
                return true;
            }

            var labels = config.Columns.ToDictionary(c => CellText.Collapse(c.Key).ToLowerInvariant(), c => c.Value);
            string? carried = null;
            foreach (var header in sheet.Cells.Where(c => c.Row == headerRow && c.Column >= 3 && GroupCodePattern().IsMatch(c.Text)))
            {
                var above = headerRow > 1 ? sheet.Covering(headerRow.Value - 1, header.Column) : null;
                var label = above is not null && !GroupCodePattern().IsMatch(above.Text) ? CellText.Collapse(above.Text) : carried;
                carried = label;

                string programme, series;
                if (label is not null && labels.TryGetValue(label.ToLowerInvariant(), out var mapped))
                {
                    (programme, series) = (mapped.Programme, mapped.Series ?? config.Series ?? "");
                }
                else if (!string.IsNullOrWhiteSpace(config.Programme))
                {
                    (programme, series) = (config.Programme, config.Series ?? "");
                }
                else
                {
                    Warn(header, $"group column under \"{label}\": that label isn't in sheets.\"{Name}\".columns, skipped");
                    continue;
                }

                var code = GroupCodePattern().Match(header.Text);
                var key = new GroupKey(programme, config.Year, series,
                    int.Parse(code.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(code.Groups[2].Value, CultureInfo.InvariantCulture));
                if (_groupColumns.ContainsValue(key))
                {
                    Warn(header, $"repeats group {key}; both columns are read as that group");
                }

                _groupColumns[header.Column] = key;
                _roles[header.Column] = ColumnRole.Grid;
                result.Groups.Add(key);
            }

            if (_groupColumns.Count == 0)
            {
                result.Errors.Add($"{Name}: no group column could be read");
                return false;
            }

            return true;
        }

        private ColumnRole RoleOf(int column) =>
            _roles.TryGetValue(column, out var role) ? role : _singleGroup is not null ? ColumnRole.Grid : ColumnRole.Notes;

        private bool InGrid(GridCell cell) => _dayOfRow.ContainsKey(cell.Row) && cell.Column >= 3;

        private IReadOnlyList<GroupKey> GroupsOf(GridCell cell)
        {
            if (_singleGroup is not null)
            {
                return RoleOf(cell.Column) == ColumnRole.Grid ? [_singleGroup] : [];
            }

            var groups = new List<GroupKey>();
            for (var column = cell.Column; column <= cell.LastColumn; column++)
            {
                if (_groupColumns.TryGetValue(column, out var group) && !groups.Contains(group))
                {
                    groups.Add(group);
                }
            }

            return groups;
        }

        private sealed record Activity(
            GridCell Cell,
            int LastRow,
            ActivityKinds Kinds,
            string Name,
            string AfterMark,
            string FullText,
            IReadOnlyList<GroupKey> Groups,
            CellOverride? Override);

        /// <summary>
        /// Every activity on the grid: each marked cell, with the plain cells just below it that continue it.
        /// A cell with several marks on separate lines ("Esperanto (s) … ⏎ Volapuk (s) …") is that many
        /// activities; one the overrides give a kind is an activity without a mark.
        /// </summary>
        private List<Activity> ReadGridActivities()
        {
            var activities = new List<Activity>();
            foreach (var cell in sheet.Cells.Where(InGrid))
            {
                if (_consumed.Contains(cell))
                {
                    continue;
                }

                var groups = GroupsOf(cell);
                if (groups.Count == 0)
                {
                    continue;
                }

                parser.Cell(Name, cell, out var cellOverride, Warn);
                if (cellOverride is { Skip: true })
                {
                    _consumed.Add(cell);
                    continue;
                }

                var marks = CellText.FindAllKinds(cell.Text);
                var givenKinds = cellOverride?.Kind is { } kind ? ImportOverrides.KindsParse(kind) : null;
                if (cellOverride is { AsNote: true } || (marks.Count == 0 && givenKinds is null))
                {
                    continue; // a note, unless it continues the cell above (handled from that cell)
                }

                _consumed.Add(cell);
                var lastRow = Math.Min(cell.LastRow, _blockEnd[cell.Row]);
                if (cell.LastRow > lastRow)
                {
                    Warn(cell, "runs past the end of its day; cut at the day's last hour");
                }

                var continuation = "";
                while (sheet.At(lastRow + 1, cell.Column) is { } below
                       && below.LastColumn == cell.LastColumn
                       && _blockEnd.GetValueOrDefault(below.Row) == _blockEnd[cell.Row]
                       && IsContinuation(below))
                {
                    _consumed.Add(below);
                    continuation += "\n" + below.Text;
                    lastRow = Math.Min(below.LastRow, _blockEnd[cell.Row]);
                }

                foreach (var part in Parts(cell, marks, givenKinds, cellOverride))
                {
                    activities.Add(new Activity(cell, lastRow, part.Kinds, part.Name, part.After + continuation,
                        part.Text + continuation, groups, cellOverride));
                }
            }

            return activities;
        }

        /// <summary>The activities in one cell's text: one per mark when each mark starts its own line,
        /// else the first mark's (a warning).</summary>
        private List<(ActivityKinds Kinds, string Name, string After, string Text)> Parts(GridCell cell,
            IReadOnlyList<(ActivityKinds Kinds, int Index, int Length)> marks, ActivityKinds? givenKinds, CellOverride? cellOverride)
        {
            var text = cell.Text;
            if (marks.Count == 0)
            {
                // No mark, but the overrides say what it is.
                var name = cellOverride?.Subject ?? CellText.NameWithoutDetails(text.Split('\n')[0]);
                return name.Length == 0 ? [] : [(givenKinds!.Value, name, text, text)];
            }

            var pieces = new List<string>();
            if (marks.Count > 1)
            {
                foreach (var line in text.Split('\n'))
                {
                    var count = CellText.FindAllKinds(line).Count;
                    if (count > 1)
                    {
                        pieces.Clear();
                        break;
                    }

                    if (count == 1 || pieces.Count == 0)
                    {
                        pieces.Add(line);
                    }
                    else
                    {
                        pieces[^1] += "\n" + line;
                    }
                }

                if (pieces.Count == marks.Count && pieces.All(p => CellText.FindKinds(p) is not null))
                {
                    Warn(cell, $"{pieces.Count} activities in one cell (one per line); read as {pieces.Count}");
                }
                else
                {
                    Warn(cell, $"{marks.Count} activity marks in one cell; only the first is read (split it in cells)");
                    pieces = [text];
                }
            }
            else
            {
                pieces.Add(text);
            }

            var parts = new List<(ActivityKinds, string, string, string)>();
            foreach (var piece in pieces)
            {
                var mark = CellText.FindKinds(piece)!.Value;
                var name = pieces.Count == 1 && cellOverride?.Subject is { } subject ? subject : CellText.SubjectName(piece[..mark.Index]);
                if (name.Length == 0)
                {
                    Warn(cell, "an activity mark with no subject before it; skipped");
                    continue;
                }

                parts.Add((givenKinds ?? mark.Kinds, name, piece[(mark.Index + mark.Length)..], piece));
            }

            if (parts.Count == 0)
            {
                _consumed.Remove(cell);
            }

            return parts;
        }

        private static bool IsContinuation(GridCell below) =>
            CellText.FindKinds(below.Text) is null
            && !CellText.NamesDayAndTime(below.Text)
            && below.Text.Length <= 120
            && !NoteWordPattern().IsMatch(below.Text);

        private bool IsTopRow(int row) => _hours[row].Start % 2 == 0;

        /// <summary>The one-hour activity in the other row of this one's two-hour block that it alternates
        /// with: in an overlapping column, or in the same room. Null when there is none.</summary>
        private Activity? PartnerOf(Activity activity, IReadOnlyList<Activity> oneHour)
        {
            var otherRow = IsTopRow(activity.Cell.Row) ? activity.Cell.Row + 1 : activity.Cell.Row - 1;
            if (!_dayOfRow.TryGetValue(otherRow, out var day) || day != _dayOfRow[activity.Cell.Row])
            {
                return null;
            }

            var room = CellText.Room(activity.AfterMark);
            return oneHour.FirstOrDefault(other => other.Cell.Row == otherRow
                && ((other.Cell.Column <= activity.Cell.LastColumn && activity.Cell.Column <= other.Cell.LastColumn)
                    || (room is not null && CellText.Room(other.AfterMark) == room)));
        }

        private void AddSlots(Activity activity, IReadOnlyList<Activity> oneHour)
        {
            var cell = activity.Cell;
            var start = _hours[cell.Row].Start;
            var end = _hours[activity.LastRow].End;
            var cellOverride = activity.Override;
            var startsAt = cellOverride?.Start is { } s ? ParseTime(s, cell) ?? new TimeOnly(start, 0) : new TimeOnly(start, 0);
            var endsAt = cellOverride?.End is { } e ? ParseTime(e, cell) ?? new TimeOnly(end, 0) : new TimeOnly(end, 0);
            var weeks = cellOverride?.Weeks is { } w
                ? OverrideWeeks(w, cell)
                : CellText.Weeks(activity.FullText, WeekCount) ?? WeekSet.All(WeekCount);
            var room = cellOverride?.Room is { } r ? CellText.NormalizeRoom(r) : CellText.Room(activity.AfterMark);
            var teacher = cellOverride?.Teacher ?? CellText.Teacher(activity.AfterMark);
            var note = cellOverride?.Note;
            if (note is null && CellText.NamesDayAndTime(activity.FullText))
            {
                note = CellText.Collapse(activity.FullText);
                Warn(cell, "the text also names a day and time; kept as the slot's note");
            }

            if (oneHour.Contains(activity))
            {
                if (config.Alternate && PartnerOf(activity, oneHour) is { } partner)
                {
                    // Two hours every other week: the block's two rows, in the row's parity.
                    var top = IsTopRow(cell.Row);
                    var odd = top == (config.TopRowWeeks == "odd");
                    (startsAt, endsAt) = top ? (startsAt, startsAt.AddHours(2)) : (startsAt.AddHours(-1), endsAt);
                    var stated = CellText.Parity(activity.FullText);
                    if (stated is null)
                    {
                        weeks = weeks.Intersect(odd ? WeekSet.Odd(WeekCount) : WeekSet.Even(WeekCount));
                    }
                    else if (stated != odd)
                    {
                        Warn(cell, $"its text says {(stated.Value ? "odd" : "even")} weeks, its row says {(odd ? "odd" : "even")}; kept the text's");
                    }

                    Warn(cell, $"one hour alternating with {partner.Cell.Address(Name)}: read as 2 h, {startsAt:HH\\:mm}-{endsAt:HH\\:mm}, "
                        + $"{(odd ? "odd" : "even")} weeks ({(top ? "top" : "bottom")} row); cells.\"{cell.Key(Name)}\" weeks/end change it");
                }
                else
                {
                    Warn(cell, $"one hour ({activity.Name} {Marks(activity.Kinds)}), alternating with nothing in its block: imported as "
                        + "written, every week; cells can set its end and weeks");
                }
            }

            if (endsAt <= startsAt)
            {
                Warn(cell, $"ends at {endsAt:HH\\:mm}, not after {startsAt:HH\\:mm}; skipped");
                return;
            }

            if (weeks.IsEmpty)
            {
                Warn(cell, $"{activity.Name}: its weeks come to none (\"{CellText.Collapse(activity.FullText)}\"); skipped");
                return;
            }

            var weekday = _dayOfRow[cell.Row];
            foreach (var byProgramme in activity.Groups.GroupBy(g => g.Programme))
            {
                var (subjectName, shortName) = Resolve(byProgramme.Key, activity.Name, cell, cellOverride?.Subject is not null);
                var subject = new SubjectKey(byProgramme.Key, config.Year, subjectName);
                if (_optional.Contains(CellText.NameKey(subjectName)))
                {
                    // An optional course whose groups aren't the timetable's: an offer to the whole year.
                    var offer = new ParsedOffer(subject, activity.Kinds, weekday, startsAt, endsAt, weeks, room, cell.Address(Name), activity.FullText);
                    AddSubject(subject, shortName, SubjectChoice.Optional, cell);
                    if (_gridOffers.Add(offer with { SourceCell = "", RawText = "" }))
                    {
                        result.Offers.Add(offer);
                    }

                    continue;
                }

                AddSubject(subject, shortName, SubjectChoice.Fixed, cell);
                result.Slots.Add(new ParsedSlot(subject, activity.Kinds, weekday, startsAt, endsAt, weeks, room, teacher, note,
                    [.. byProgramme], cell.Address(Name), activity.FullText));
            }
        }

        /// <summary>Adds the subject; warns when one with classes on the grid becomes optional (its grid classes
        /// then show only to students who choose it).</summary>
        private void AddSubject(SubjectKey subject, string? shortName, SubjectChoice choice, GridCell cell)
        {
            var before = result.Subjects.GetValueOrDefault(subject)?.Choice;
            result.AddSubject(subject, shortName, choice);
            if (choice == SubjectChoice.Optional && before is SubjectChoice.Fixed or SubjectChoice.PickSlot
                && result.Slots.Any(s => s.Subject == subject) && _warnedOptional.Add((cell, CellText.NameKey(subject.Name))))
            {
                Warn(cell, $"{subject.Name} has classes on the grid and is offered here as optional: its grid classes show only to "
                    + "students who choose it");
            }
        }

        /// <summary>
        /// A column of offered subjects: a marked cell starts a subject, the plain cells below it (until
        /// the next marked one) continue it, and the days and times written in them are its offers.
        /// </summary>
        private void ReadOffers(int column, ColumnRole role)
        {
            var cells = sheet.Cells.Where(c => c.Column == column && c.Row >= _firstHourRow && c.Row <= _lastHourRow).ToList();
            GridCell? header = null;
            var continuation = new List<GridCell>();
            foreach (var cell in cells)
            {
                parser.Cell(Name, cell, out var cellOverride, Warn);
                if (cellOverride is { Skip: true })
                {
                    _consumed.Add(cell);
                    continue;
                }

                if (cellOverride is not { AsNote: true } && CellText.FindKinds(cell.Text) is not null)
                {
                    FlushOffers(header, continuation, role);
                    header = cell;
                    continuation = [];
                }
                else if (header is not null && cellOverride is not { AsNote: true })
                {
                    continuation.Add(cell);
                }
            }

            FlushOffers(header, continuation, role);
        }

        private void FlushOffers(GridCell? header, List<GridCell> continuation, ColumnRole role)
        {
            if (header is null)
            {
                return;
            }

            var mark = CellText.FindKinds(header.Text)!.Value;
            var name = CellText.SubjectName(header.Text[..mark.Index]);
            var after = string.Join("\n", [header.Text[(mark.Index + mark.Length)..], .. continuation.Select(c => c.Text)]);
            var raw = string.Join("\n", [header.Text, .. continuation.Select(c => c.Text)]);
            var defaultWeeks = CellText.Weeks(CellText.Collapse(after), WeekCount) ?? WeekSet.All(WeekCount);
            var written = CellText.Offers(after, null, WeekCount, defaultWeeks);
            if (name.Length > 0 && written.Count == 0 && role != ColumnRole.PickSlot && _dayOfRow.ContainsKey(header.Row))
            {
                // An optional subject with no time written, on a grid row: the rows it covers are its time.
                var lastRow = Math.Min(header.LastRow, _blockEnd[header.Row]);
                written = [new WrittenOffer(_dayOfRow[header.Row], new TimeOnly(_hours[header.Row].Start, 0),
                    new TimeOnly(_hours[lastRow].End, 0), CellText.Room(after), defaultWeeks)];
                Warn(header, $"{name}: offered with no day and time written; placed at its grid position ({written[0].Weekday} "
                    + $"{written[0].StartsAt:HH\\:mm}-{written[0].EndsAt:HH\\:mm})");
            }

            if (name.Length == 0 || written.Count == 0)
            {
                Warn(header, name.Length == 0 ? "an offer with no subject; kept as a note" : $"{name}: offered, but no day and time written; kept as a note");
                return; // not consumed: the notes pass picks these cells up
            }

            foreach (var empty in written.Where(o => o.Weeks.IsEmpty))
            {
                Warn(header, $"{name}: an offer ({empty.Weekday} {empty.StartsAt:HH\\:mm}) whose weeks come to none; skipped");
            }

            _consumed.Add(header);
            _consumed.UnionWith(continuation);
            var kinds = role == ColumnRole.Facultative ? mark.Kinds | ActivityKinds.Facultative : mark.Kinds;
            var choice = role == ColumnRole.PickSlot ? SubjectChoice.PickSlot : SubjectChoice.Optional;
            foreach (var programme in AudienceOf(header))
            {
                var (subjectName, shortName) = Resolve(programme, name, header, explicitName: false);
                var subject = new SubjectKey(programme, config.Year, subjectName);
                AddSubject(subject, shortName, choice, header);
                foreach (var offer in written.Where(o => !o.Weeks.IsEmpty).Distinct())
                {
                    result.Offers.Add(new ParsedOffer(subject, kinds, offer.Weekday, offer.StartsAt, offer.EndsAt, offer.Weeks,
                        offer.Room, header.Address(Name), raw));
                }
            }
        }

        /// <summary>The programmes an offered subject is for: its tags ([R], [E], [R+E]) through the
        /// overrides, or every programme of the sheet.</summary>
        private IReadOnlyList<string> AudienceOf(GridCell header)
        {
            var all = _singleGroup is not null ? [_singleGroup.Programme] : _groupColumns.Values.Select(g => g.Programme).Distinct().ToList();
            var tags = CellText.Tags(header.Text);
            if (tags.Count == 0)
            {
                return all;
            }

            var byTag = config.Tags.ToDictionary(t => t.Key.ToUpperInvariant(), t => t.Value);
            var programmes = new List<string>();
            foreach (var tag in tags)
            {
                if (byTag.TryGetValue(tag, out var programme))
                {
                    programmes.Add(programme);
                }
                else
                {
                    Warn(header, $"tag [{tag}] isn't in sheets.\"{Name}\".tags; offered to the whole sheet");
                    return all;
                }
            }

            return programmes.Distinct().ToList();
        }

        /// <summary>Every cell nothing else took, from the first hour row down (titles and headers above
        /// the grid are skipped): sport, tutoring hours, footnotes, per-subgroup rotas written out. One in a
        /// group column on the grid warns: it might be a class without a mark.</summary>
        private void ReadNotes()
        {
            var programmes = _singleGroup is not null ? [_singleGroup.Programme] : _groupColumns.Values.Select(g => g.Programme).Distinct().ToList();
            var seen = new HashSet<(string, string)>();
            foreach (var cell in sheet.Cells.Where(c => c.Row >= _firstHourRow && c.Column >= 3 && !_consumed.Contains(c)))
            {
                var text = CellText.Collapse(parser.Cell(Name, cell, out var cellOverride, Warn) ?? cell.Text);
                if (cellOverride is { Skip: true } || text.Trim('`', '*', '-', ' ', '.').Length < 2)
                {
                    continue;
                }

                var groups = InGrid(cell) ? GroupsOf(cell) : [];
                if (groups.Count > 0 && cellOverride is not { AsNote: true })
                {
                    Warn(cell, $"in a group column but no activity mark: kept as a note (\"{Shorten(text)}\"); cells can give it a kind");
                }

                var forProgrammes = groups.Count > 0 ? groups.Select(g => g.Programme).Distinct().ToList() : programmes;
                foreach (var programme in forProgrammes)
                {
                    if (seen.Add((programme, CellText.NameKey(text))))
                    {
                        result.Notes.Add(new ParsedNote(programme, config.Year, text, cell.Address(Name)));
                    }
                }
            }
        }

        private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "…";

        /// <summary>
        /// A course name of this sheet, as later cells' subjects are matched against: written with a typo
        /// or two of one already seen (long names, the same numbers), it's that one; "Programare Kotlin - PK"
        /// is "Programare Kotlin", and PK an abbreviation of it.
        /// </summary>
        private void RememberCourse(string programme, string name, GridCell cell)
        {
            if (!_courseNames.TryGetValue(programme, out var names))
            {
                _courseNames[programme] = names = [];
            }

            if (OwnAbbreviationPattern().Match(name) is { Success: true } own && LooksAbbreviated(own.Groups["abbr"].Value))
            {
                var full = own.Groups["name"].Value.Trim();
                names.TryAdd(CellText.NameKey(name), full);
                names.TryAdd(CellText.NameKey(full), full);
                _ownAbbreviations.TryAdd((programme, Compact(own.Groups["abbr"].Value)), full);
                return;
            }

            var key = CellText.NameKey(name);
            if (names.ContainsKey(key))
            {
                return;
            }

            var close = Close(names, key);
            if (close.Count == 1)
            {
                Warn(cell, $"read \"{name}\" as \"{close[0]}\" (a typo?)");
                names[key] = close[0];
                return;
            }

            names[key] = name;
        }

        /// <summary>Names within two edits of this one (both long), with the same numbers and
        /// numerals: "Proiect 1" and "Proiect 2" are different subjects, not a typo.</summary>
        private static List<string> Close(Dictionary<string, string> names, string key) =>
            key.Length < 10
                ? []
                : names.Where(n => n.Key.Length >= 10 && Distance(n.Key, key) <= 2 && Numerals(n.Key) == Numerals(key))
                    .Select(n => n.Value).Distinct().ToList();

        private static string Numerals(string key) =>
            string.Join(" ", key.Split(' ').Where(word => word.All(char.IsDigit) || RomanPattern().IsMatch(word)));

        /// <summary>
        /// The subject's full name and the abbreviation the sheet used, if any, in this order:
        /// <list type="number">
        /// <item>an alias from the overrides;</item>
        /// <item>a course of the same programme on this sheet with that name (ignoring case, diacritics, a
        /// typo or two in a long name);</item>
        /// <item>a course that names its own abbreviation ("Programare Kotlin - PK");</item>
        /// <item>a course with those initials (LSV = Logica senzorilor virtuali; acronym words count whole;
        /// a trailing number may be left out) or that beginning (OPT = Optica);</item>
        /// <item>a guess, which warns: the only course whose name has those letters in that order, starting
        /// with the first.</item>
        /// </list>
        /// Otherwise the name as written; an abbreviation that matches nothing warns.
        /// </summary>
        private (string Name, string? ShortName) Resolve(string programme, string written, GridCell cell, bool explicitName)
        {
            var key = CellText.NameKey(written);
            if (_aliases.TryGetValue(key, out var alias))
            {
                _usedAliases.Add(key);
                return (alias, written);
            }

            var courses = _courseNames.GetValueOrDefault(programme) ?? [];
            if (explicitName || courses.ContainsKey(key))
            {
                return (courses.GetValueOrDefault(key) ?? written, null);
            }

            var close = Close(courses, key);
            if (close.Count == 1)
            {
                Warn(cell, $"read \"{written}\" as \"{close[0]}\" (a typo?)");
                return (close[0], null);
            }

            if (!LooksAbbreviated(written))
            {
                return (written, null);
            }

            var compact = Compact(written);
            if (_ownAbbreviations.TryGetValue((programme, compact), out var ownName))
            {
                return (ownName, written);
            }

            var distinct = courses.Values.Distinct().ToList();
            var matches = distinct
                .Where(course => Initials(course) is var initials
                    && (initials == compact || initials.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') == compact
                        || (compact.Length >= 3 && Compact(course).StartsWith(compact, StringComparison.Ordinal))))
                .ToList();
            if (matches.Count == 1)
            {
                return (matches[0], written);
            }

            if (matches.Count == 0)
            {
                // A course that names its own abbreviation isn't guessed as another.
                var named = _ownAbbreviations.Where(a => a.Key.Programme == programme).Select(a => a.Value).ToHashSet();
                var guesses = distinct.Where(course => !named.Contains(course) && InOrder(compact, Compact(course))).ToList();
                if (guesses.Count == 1)
                {
                    Warn(cell, $"guessed abbreviation \"{written}\" = \"{guesses[0]}\" (its letters in order); if wrong, add it to sheets.\"{Name}\".aliases");
                    return (guesses[0], written);
                }
            }

            Warn(cell, matches.Count == 0
                ? $"abbreviation \"{written}\" matches no course on this sheet; add it to sheets.\"{Name}\".aliases"
                : $"abbreviation \"{written}\" matches {string.Join(", ", matches.Select(m => $"\"{m}\""))}; add it to sheets.\"{Name}\".aliases");
            return (written, written);
        }

        /// <summary>Mostly capitals, at most one more word that is a numeral (II, 3): LSD, PC,
        /// TN1, OPT, IoTXY, AB:CD, "SX I". "Optica" or "Acustica" is a name.</summary>
        private static bool LooksAbbreviated(string name)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is 0 or > 2 || (words.Length == 2 && !NumeralPattern().IsMatch(words[1])) || name.Length > 14)
            {
                return false;
            }

            var letters = words[0].Where(char.IsLetter).ToList();
            var capitals = letters.Count(char.IsUpper);
            return capitals >= 2 && capitals * 2 >= letters.Count;
        }

        /// <summary>Upper case, no diacritics, letters and digits only.</summary>
        private static string Compact(string text) =>
            string.Concat(CellText.RemoveDiacritics(text).ToUpperInvariant().Where(char.IsLetterOrDigit));

        /// <summary>The first letters of the words (stop words left out); an acronym word (two capitals or
        /// more, like IoT) counts whole, a number as itself.</summary>
        private static string Initials(string name) =>
            string.Concat(CellText.RemoveDiacritics(name)
                .Split([' ', '-', '.', ',', '/', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !StopWords.Contains(word.ToLowerInvariant()))
                .Select(word => char.IsDigit(word[0]) ? word
                    : word.Count(char.IsUpper) >= 2 ? word.ToUpperInvariant()
                    : char.ToUpperInvariant(word[0]).ToString()));

        /// <summary>True when every character of <paramref name="abbreviation"/> appears in
        /// <paramref name="name"/> in that order, starting with the first.</summary>
        private static bool InOrder(string abbreviation, string name)
        {
            if (abbreviation.Length < 2 || name.Length == 0 || abbreviation[0] != name[0])
            {
                return false;
            }

            var at = 0;
            foreach (var c in abbreviation)
            {
                at = name.IndexOf(c, at);
                if (at < 0)
                {
                    return false;
                }

                at++;
            }

            return true;
        }

        private static int Distance(string a, string b)
        {
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var current = new int[b.Length + 1];
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                }

                previous = current;
            }

            return previous[b.Length];
        }

        /// <summary>An override's weeks: "odd", "even", "all", or anything a cell could say ("1-7",
        /// "3, 5, 7", "s. 1-7 s. impara").</summary>
        private WeekSet OverrideWeeks(string value, GridCell cell)
        {
            var trimmed = value.Trim().ToLowerInvariant();
            var weeks = trimmed switch
            {
                "odd" or "impar" or "impare" => WeekSet.Odd(WeekCount),
                "even" or "par" or "pare" => WeekSet.Even(WeekCount),
                "all" => WeekSet.All(WeekCount),
                _ => CellText.Weeks(value, WeekCount)
                    ?? (trimmed.Length > 0 && trimmed.All(c => char.IsDigit(c) || c is ',' or '-' or ' ' or '&') ? CellText.ParseList(trimmed) : (WeekSet?)null),
            };
            if (weeks is { IsEmpty: false } found)
            {
                return found.Intersect(WeekSet.All(WeekCount));
            }

            Warn(cell, $"overrides: weeks \"{value}\" isn't readable; every week");
            return WeekSet.All(WeekCount);
        }

        private TimeOnly? ParseTime(string value, GridCell cell)
        {
            if (TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return time;
            }

            Warn(cell, $"overrides: \"{value}\" isn't a time (HH:mm); ignored");
            return null;
        }

        private static string Marks(ActivityKinds kinds) =>
            "(" + string.Join("+", kinds.List().Select(k => k switch
            {
                ActivityKind.Course => "c",
                ActivityKind.Lab => "l",
                ActivityKind.Seminar => "s",
                ActivityKind.Project => "p",
                _ => "f",
            })) + ")";
    }

    /// <summary>
    /// The cell's override, if the overrides file has one and its text guard (if any) matches; returns its
    /// note text when it turns the cell into a note with its own text. A guard that doesn't match warns
    /// once and the override is ignored.
    /// </summary>
    private string? Cell(string sheet, GridCell cell, out CellOverride? cellOverride, Action<GridCell, string> warn)
    {
        var key = cell.Key(sheet);
        cellOverride = overrides.Cells.GetValueOrDefault(key);
        if (cellOverride is null)
        {
            return null;
        }

        _usedCells.Add(key);
        if (cellOverride.Text is { } expected
            && !CellText.Collapse(cell.Text).StartsWith(CellText.Collapse(expected), StringComparison.OrdinalIgnoreCase))
        {
            if (_warnedCells.Add(key))
            {
                warn(cell, $"overrides: cells.\"{key}\" expects text starting \"{expected}\", the cell has \"{CellText.Collapse(cell.Text)}\"; ignored");
            }

            cellOverride = null;
            return null;
        }

        return cellOverride is { AsNote: true, Note: { } note } ? note : null;
    }

    [GeneratedRegex(@"^\s*(\d{1,2})\s*[-–]\s*(\d{1,2})\s*$")]
    private static partial Regex HourLabelPattern();

    /// <summary>"Programare Kotlin - PK", "Sisteme Orbitale -SO": a course name followed by its abbreviation.</summary>
    [GeneratedRegex(@"^(?<name>.+?\S)\s*[-–]\s*(?<abbr>[A-Z][A-Za-z0-9]{1,9})$")]
    private static partial Regex OwnAbbreviationPattern();

    [GeneratedRegex(@"^(?:I{1,3}|IV|V|\d{1,2})$")]
    private static partial Regex NumeralPattern();

    [GeneratedRegex(@"^(?:i{1,3}|iv|v|vi{1,3}|ix|x)$")]
    private static partial Regex RomanPattern();

    [GeneratedRegex(@"^\s*(\d)\.(\d)\s*$")]
    private static partial Regex GroupCodePattern();

    [GeneratedRegex(@"tutoriere|tutoring|\bsport|recuperare|preg[aă]tire|legend|nota[tț]ii|observa[tț]ii|vezi", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteWordPattern();
}
