using CampusPulse.Domain.Timetable;
using CampusPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>One workbook to import: its source id (what it contributes is kept under it), the file name it
/// has today (only reported), its contents' hash and what the parser read from it.</summary>
public sealed record ImportFile(string Source, string FileName, string Sha256, ParsedTimetable Parsed);

/// <summary>Exit codes of an import run.</summary>
public static class ImportExit
{
    public const int Done = 0;
    public const int Failed = 1;

    /// <summary>Students would lose something (or a listed sheet is missing): nothing was written; --force
    /// writes it anyway.</summary>
    public const int Refused = 2;
}

/// <summary>
/// Writes parsed workbooks to the database, all of a run in one transaction. A dry run does exactly the
/// same, then rolls back, so it checks everything a real run would. Per source (workbook):
/// <list type="bullet">
/// <item>slots and notes: deleted and written again (nothing a student owns points at them);</item>
/// <item>study groups and subjects: matched by what identifies them and kept; new ones added; those the
/// workbook no longer has are retired, never deleted, so a student's group and chosen subjects stay
/// (shown as no longer offered);</item>
/// <item>offered slots: kept while their day, times, room and weeks stay; changed or gone ones are deleted,
/// which clears the picks of them;</item>
/// <item>the semester's calendar, programmes and rooms: from the overrides, upserted.</item>
/// </list>
/// Before anything is kept, the run counts what students would lose: groups and chosen subjects no longer
/// offered, picks cleared. Any of those, or a sheet the overrides list that the workbook lacks, refuses
/// the run unless forced. A source whose sheets an import under another source id already covered is
/// refused always (the workbooks would double). A source whose contents, overrides and parser version
/// match its last import is skipped unless forced.
/// </summary>
public sealed class TimetableImporter(CampusPulseDbContext db, TimeProvider time, TextWriter output)
{
    private sealed record Impact(int GroupsNoLongerOffered, int ChoicesNoLongerOffered, int PicksCleared)
    {
        public bool Any => GroupsNoLongerOffered > 0 || ChoicesNoLongerOffered > 0 || PicksCleared > 0;
    }

    public async Task<int> RunAsync(ImportOverrides overrides, string overridesSha256, IReadOnlyList<ImportFile> files,
        bool dryRun, bool force, CancellationToken cancellationToken)
    {
        if (files.Any(f => f.Parsed.Errors.Count > 0))
        {
            foreach (var error in files.SelectMany(f => f.Parsed.Errors))
            {
                output.WriteLine($"ERROR {error}");
            }

            output.WriteLine("Not imported: fix the errors above (they name a sheet whose grid can't be read).");
            return ImportExit.Failed;
        }

        var academicYear = overrides.Semester.AcademicYear;
        var semester = await db.Semesters.AsNoTracking()
            .SingleOrDefaultAsync(s => s.AcademicYear == academicYear && s.Number == overrides.Semester.Number, cancellationToken);
        var runs = semester is null
            ? []
            : await db.TimetableImportRuns.AsNoTracking().Where(r => r.SemesterId == semester.Id).ToListAsync(cancellationToken);
        var latest = runs.GroupBy(r => r.Source).Select(g => g.MaxBy(r => r.ImportedAt)!).ToList();

        var toImport = new List<ImportFile>();
        foreach (var file in files)
        {
            var overlap = latest.Where(r => r.Source != file.Source && r.Sheets.Intersect(file.Parsed.Sheets).Any()).ToList();
            if (overlap.Count > 0)
            {
                foreach (var run in overlap)
                {
                    output.WriteLine($"ERROR {file.FileName} (source {file.Source}): its sheets {string.Join(", ", run.Sheets.Intersect(file.Parsed.Sheets))} "
                        + $"were imported under source {run.Source} ({run.FileName}, {run.ImportedAt:yyyy-MM-dd}). Importing it as {file.Source} would "
                        + $"double every class: give it the id {run.Source} in the overrides' sources.");
                }

                return ImportExit.Failed;
            }

            var last = latest.SingleOrDefault(r => r.Source == file.Source);
            if (!force && last is not null && last.FileSha256 == file.Sha256 && last.OverridesSha256 == overridesSha256
                && last.ParserVersion == SheetParser.Version)
            {
                output.WriteLine($"{file.FileName} (source {file.Source}): already imported on {last.ImportedAt:yyyy-MM-dd HH:mm}Z with the "
                    + $"same file (sha256 {file.Sha256}), overrides and parser; skipped (--force imports it again)");
                continue;
            }

            toImport.Add(file);
        }

        if (toImport.Count == 0)
        {
            return ImportExit.Done;
        }

        foreach (var file in toImport)
        {
            var before = semester is null ? Snapshot.Empty : await SnapshotAsync(semester.Id, file.Source, cancellationToken);
            PrintDiff(before, file, output);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var current = await EnsureSemesterAsync(overrides, now, cancellationToken);
        var programmes = await EnsureProgrammesAsync(overrides, toImport, now, cancellationToken);
        var rooms = await EnsureRoomsAsync(overrides, toImport, now, cancellationToken);
        // One list per run, so a second workbook sees the first one's new groups and subjects.
        var groups = await db.StudyGroups.Where(g => g.SemesterId == current.Id).ToListAsync(cancellationToken);
        var subjects = await db.Subjects.Where(s => s.SemesterId == current.Id).ToListAsync(cancellationToken);

        var refused = false;
        foreach (var file in toImport)
        {
            var impact = await WriteFileAsync(current, programmes, rooms, groups, subjects, file, overridesSha256, now, cancellationToken);
            output.WriteLine($"-- {file.FileName} (source {file.Source}) for students: {impact.GroupsNoLongerOffered} whose study group is no "
                + $"longer offered, {impact.ChoicesNoLongerOffered} chosen subjects no longer offered (both kept, shown as such), "
                + $"{impact.PicksCleared} picked offered slots cleared (the offer changed or went)");
            foreach (var missing in file.Parsed.MissingSheets)
            {
                output.WriteLine($"-- {file.FileName}: sheet \"{missing}\" (in sources.{file.Source}) isn't in the workbook: its groups and "
                    + "subjects would be no longer offered");
            }

            refused |= impact.Any || file.Parsed.MissingSheets.Count > 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (refused && !force)
        {
            await transaction.RollbackAsync(cancellationToken);
            output.WriteLine($"{(dryRun ? "Dry run: this would be refused" : "Refused")}: students would lose what's counted above, or a "
                + "listed sheet is missing. Nothing was written. Check it, then run again with --force.");
            return ImportExit.Refused;
        }

        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
            output.WriteLine("Dry run: everything was checked and rolled back; nothing was written.");
            return ImportExit.Done;
        }

        await transaction.CommitAsync(cancellationToken);
        output.WriteLine($"Imported {toImport.Count} workbook(s) into {current.AcademicYear} semester {current.Number}.");
        return ImportExit.Done;
    }

    private async Task<Semester> EnsureSemesterAsync(ImportOverrides overrides, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var academicYear = overrides.Semester.AcademicYear;
        var semester = await db.Semesters
            .SingleOrDefaultAsync(s => s.AcademicYear == academicYear && s.Number == overrides.Semester.Number, cancellationToken);
        if (semester is null)
        {
            semester = new Semester(academicYear, overrides.Semester.Number, now);
            db.Semesters.Add(semester);
        }
        else
        {
            // Replaced, not merged: the old rows go first (same keys); the semester was loaded without them.
            await db.Set<TeachingWeek>().Where(w => w.SemesterId == semester.Id).ExecuteDeleteAsync(cancellationToken);
            await db.Set<NonTeachingDay>().Where(d => d.SemesterId == semester.Id).ExecuteDeleteAsync(cancellationToken);
            await db.Set<SemesterBreak>().Where(b => b.SemesterId == semester.Id).ExecuteDeleteAsync(cancellationToken);
        }

        semester.SetCalendar(overrides.Calendar.WeekStarts(), overrides.Calendar.Days(), overrides.Calendar.BreakDates());
        return semester;
    }

    private async Task<Dictionary<string, Programme>> EnsureProgrammesAsync(ImportOverrides overrides, IReadOnlyList<ImportFile> files,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var codes = files.SelectMany(f => f.Parsed.Programmes).Concat(overrides.Programmes.Keys).Distinct().ToList();
        var programmes = await db.Programmes.Where(p => codes.Contains(p.Code)).ToDictionaryAsync(p => p.Code, cancellationToken);
        foreach (var code in codes)
        {
            if (!programmes.TryGetValue(code, out var programme))
            {
                programme = new Programme(code, overrides.Programmes.GetValueOrDefault(code) ?? code, now);
                db.Programmes.Add(programme);
                programmes[code] = programme;
            }
            else if (overrides.Programmes.TryGetValue(code, out var name))
            {
                programme.Rename(name);
            }
        }

        return programmes;
    }

    private async Task<Dictionary<string, Room>> EnsureRoomsAsync(ImportOverrides overrides, IReadOnlyList<ImportFile> files,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var described = overrides.Rooms.ToDictionary(r => CellText.NormalizeRoom(r.Key), r => r.Value);
        var codes = files.SelectMany(f => f.Parsed.Slots.Select(s => s.Room).Concat(f.Parsed.Offers.Select(o => o.Room)))
            .OfType<string>()
            .Concat(described.Keys)
            .Distinct()
            .ToList();
        var rooms = await db.Rooms.Where(r => codes.Contains(r.Code)).ToDictionaryAsync(r => r.Code, cancellationToken);
        foreach (var code in codes)
        {
            if (!rooms.TryGetValue(code, out var room))
            {
                room = new Room(code, now);
                db.Rooms.Add(room);
                rooms[code] = room;
            }

            if (described.TryGetValue(code, out var about))
            {
                room.Describe(about.Building, about.Floor, about.Capacity);
            }
        }

        return rooms;
    }

    private async Task<Impact> WriteFileAsync(Semester semester, Dictionary<string, Programme> programmes, Dictionary<string, Room> rooms,
        List<StudyGroup> groups, List<Subject> subjects, ImportFile file, string overridesSha256, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var parsed = file.Parsed;

        // Study groups: by programme, year, series, group and subgroup. The source's others are retired.
        var groupIds = new Dictionary<GroupKey, Guid>();
        foreach (var key in parsed.Groups)
        {
            var programme = programmes[key.Programme];
            var group = groups.FirstOrDefault(g => g.ProgrammeId == programme.Id && g.Year == key.Year && g.Series == key.Series
                && g.Group == key.Group && g.Subgroup == key.Subgroup);
            if (group is null)
            {
                group = new StudyGroup(semester.Id, programme.Id, key.Year, key.Series, key.Group, key.Subgroup, file.Source, now);
                db.StudyGroups.Add(group);
                groups.Add(group);
            }
            else
            {
                group.Offer(file.Source);
            }

            groupIds[key] = group.Id;
        }

        var keptGroups = groupIds.Values.ToHashSet();
        var retiredGroups = groups.Where(g => g.Source == file.Source && g.RetiredAt is null && !keptGroups.Contains(g.Id)).ToList();
        foreach (var group in retiredGroups)
        {
            group.Retire(now);
        }

        var retiredGroupIds = retiredGroups.Select(g => g.Id).ToList();
        var groupsNoLongerOffered = retiredGroupIds.Count == 0
            ? 0
            : await db.StudentSettings.CountAsync(s => s.StudyGroupId != null && retiredGroupIds.Contains(s.StudyGroupId.Value), cancellationToken);

        // Subjects: by programme, year and name (ignoring case and diacritics). The source's others are retired.
        var subjectIds = new Dictionary<SubjectKey, Guid>();
        foreach (var (key, parsedSubject) in parsed.Subjects)
        {
            var programme = programmes[key.Programme];
            var subject = subjects.FirstOrDefault(s => s.ProgrammeId == programme.Id && s.Year == key.Year
                && CellText.NameKey(s.Name) == CellText.NameKey(key.Name));
            if (subject is null)
            {
                subject = new Subject(semester.Id, programme.Id, key.Year, key.Name, file.Source, now);
                db.Subjects.Add(subject);
                subjects.Add(subject);
            }

            subject.Update(parsedSubject.ShortName, parsedSubject.Choice, file.Source, parsedSubject.CampusVirtualCourseId);
            subjectIds[key] = subject.Id;
        }

        var keptSubjects = subjectIds.Values.ToHashSet();
        var retiredSubjects = subjects.Where(s => s.Source == file.Source && s.RetiredAt is null && !keptSubjects.Contains(s.Id)).ToList();
        foreach (var subject in retiredSubjects)
        {
            subject.Retire(now);
        }

        var retiredSubjectIds = retiredSubjects.Select(s => s.Id).ToList();
        var choicesNoLongerOffered = retiredSubjectIds.Count == 0
            ? 0
            : await db.StudentSubjects.CountAsync(c => retiredSubjectIds.Contains(c.SubjectId), cancellationToken);

        // Slots: replaced. Their group links go with them (ON DELETE CASCADE).
        await db.TimetableSlots.Where(s => s.SemesterId == semester.Id && s.Source == file.Source).ExecuteDeleteAsync(cancellationToken);
        foreach (var parsedSlot in parsed.Slots)
        {
            var slot = new TimetableSlot(semester.Id, subjectIds[parsedSlot.Subject], parsedSlot.Kinds, parsedSlot.Weekday,
                parsedSlot.StartsAt, parsedSlot.EndsAt, parsedSlot.Weeks, file.Source, parsedSlot.SourceCell, parsedSlot.RawText, now);
            slot.Describe(parsedSlot.Room is null ? null : rooms[parsedSlot.Room].Id, parsedSlot.Teacher, parsedSlot.Note);
            foreach (var group in parsedSlot.Groups)
            {
                slot.AddGroup(groupIds[group]);
            }

            db.TimetableSlots.Add(slot);
        }

        // Offered slots: kept while day, times, room and weeks are the same (students' picks with them);
        // the rest of the source's subjects' offers (retired subjects' included) go, clearing their picks.
        var sourceSubjectIds = subjects.Where(s => s.Source == file.Source).Select(s => s.Id).ToList();
        var offers = await db.OfferedSlots.Where(o => sourceSubjectIds.Contains(o.SubjectId)).ToListAsync(cancellationToken);
        var keptOffers = new HashSet<Guid>();
        foreach (var parsedOffer in parsed.Offers)
        {
            var subjectId = subjectIds[parsedOffer.Subject];
            var roomId = parsedOffer.Room is null ? (Guid?)null : rooms[parsedOffer.Room].Id;
            var offer = offers.FirstOrDefault(o => o.SubjectId == subjectId && !keptOffers.Contains(o.Id)
                && o.SameAs(parsedOffer.Weekday, parsedOffer.StartsAt, parsedOffer.EndsAt, roomId, parsedOffer.Weeks));
            if (offer is null)
            {
                offer = new OfferedSlot(subjectId, parsedOffer.Kinds, parsedOffer.Weekday, parsedOffer.StartsAt, parsedOffer.EndsAt, roomId,
                    parsedOffer.Weeks, parsedOffer.SourceCell, parsedOffer.RawText, now);
                db.OfferedSlots.Add(offer);
                offers.Add(offer);
            }
            else
            {
                offer.Refresh(parsedOffer.Kinds, parsedOffer.SourceCell, parsedOffer.RawText);
            }

            keptOffers.Add(offer.Id);
        }

        var droppedOffers = offers.Where(o => !keptOffers.Contains(o.Id)).ToList();
        var droppedOfferIds = droppedOffers.Select(o => o.Id).ToList();
        var picksCleared = droppedOfferIds.Count == 0
            ? 0
            : await db.StudentSubjects.CountAsync(c => c.OfferedSlotId != null && droppedOfferIds.Contains(c.OfferedSlotId.Value), cancellationToken);
        db.OfferedSlots.RemoveRange(droppedOffers);

        // Notes: replaced.
        await db.TimetableNotes.Where(n => n.SemesterId == semester.Id && n.Source == file.Source).ExecuteDeleteAsync(cancellationToken);
        foreach (var note in parsed.Notes)
        {
            db.TimetableNotes.Add(new TimetableNote(semester.Id, programmes[note.Programme].Id, note.Year, note.Text, file.Source,
                note.SourceCell, now));
        }

        var summary = $"{parsed.Groups.Count} groups, {parsed.Subjects.Count} subjects, {parsed.Slots.Count} slots, "
            + $"{parsed.Offers.Count} offered slots, {parsed.Notes.Count} notes, {parsed.Warnings.Count} warnings; "
            + $"{retiredGroups.Count} groups and {retiredSubjects.Count} subjects no longer offered"
            + (parsed.CampusVirtualListed
                ? $"; {parsed.Subjects.Values.Count(s => s.CampusVirtualCourseId is null)} subjects without a Campus Virtual course"
                : "");
        db.TimetableImportRuns.Add(new TimetableImportRun(semester.Id, file.Source, file.FileName, parsed.Sheets, file.Sha256, overridesSha256,
            SheetParser.Version, summary, parsed.Warnings, now));
        output.WriteLine($"{file.FileName} (source {file.Source}): {summary}.");
        return new Impact(groupsNoLongerOffered, choicesNoLongerOffered, picksCleared);
    }

    /// <summary>
    /// What importing the workbook would change, line by line: slots, offered slots, groups, subjects and
    /// notes that would be added (+) or removed (-), compared by what a student sees (subject, kind, day,
    /// time, room, weeks, teacher, groups). Then every warning.
    /// </summary>
    public static void PrintDiff(Snapshot before, ImportFile file, TextWriter output)
    {
        var parsed = file.Parsed;
        var after = new Snapshot(
            parsed.Slots.Select(SlotLine).ToList(),
            parsed.Offers.Select(OfferLine).ToList(),
            parsed.Groups.Select(g => g.ToString()).ToList(),
            parsed.Subjects.Values.Select(s => SubjectLine(s.Key, s.Choice, s.CampusVirtualCourseId)).ToList(),
            parsed.Notes.Select(n => $"{n.Programme} y{n.Year}: {n.Text}").ToList());

        output.WriteLine($"== {file.FileName} (source {file.Source}, sha256 {file.Sha256})");
        PrintSection(output, "study groups", before.Groups, after.Groups);
        PrintSection(output, "subjects", before.Subjects, after.Subjects);
        PrintSection(output, "slots", before.Slots, after.Slots);
        PrintSection(output, "offered slots", before.Offers, after.Offers);
        PrintSection(output, "notes", before.Notes, after.Notes);
        if (parsed.CampusVirtualListed)
        {
            var without = parsed.Subjects.Values.Where(s => s.CampusVirtualCourseId is null).Select(s => s.Key.ToString()).Order(StringComparer.Ordinal).ToList();
            output.WriteLine($"-- Campus Virtual: {parsed.Subjects.Count - without.Count} of {parsed.Subjects.Count} subjects have a course, {without.Count} have none");
            foreach (var subject in without)
            {
                output.WriteLine($"   ? {subject}");
            }
        }

        output.WriteLine($"-- {parsed.Warnings.Count} warning(s)");
        foreach (var warning in parsed.Warnings)
        {
            output.WriteLine($"   ! {warning}");
        }

        foreach (var error in parsed.Errors)
        {
            output.WriteLine($"   ERROR {error}");
        }
    }

    private static void PrintSection(TextWriter output, string title, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var removed = Subtract(before, after);
        var added = Subtract(after, before);
        output.WriteLine($"-- {title}: {after.Count} after, +{added.Count} -{removed.Count}, {after.Count - added.Count} unchanged");
        foreach (var line in removed.Order(StringComparer.Ordinal))
        {
            output.WriteLine($"   - {line}");
        }

        foreach (var line in added.Order(StringComparer.Ordinal))
        {
            output.WriteLine($"   + {line}");
        }
    }

    /// <summary>The lines of <paramref name="a"/> not in <paramref name="b"/>, counting duplicates.</summary>
    private static List<string> Subtract(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var remaining = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var line in a)
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>What a source contributed, as the lines the diff compares.</summary>
    public sealed record Snapshot(List<string> Slots, List<string> Offers, List<string> Groups, List<string> Subjects, List<string> Notes)
    {
        public static Snapshot Empty => new([], [], [], [], []);
    }

    /// <summary>Read without tracking: the write that follows deletes rows in bulk, and a tracked copy of
    /// one would make EF try to delete it again (cascading from a deleted offer).</summary>
    private async Task<Snapshot> SnapshotAsync(Guid semesterId, string source, CancellationToken cancellationToken)
    {
        var programmes = await db.Programmes.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var rooms = await db.Rooms.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Code, cancellationToken);
        var groups = await db.StudyGroups.AsNoTracking().Where(g => g.SemesterId == semesterId).ToDictionaryAsync(g => g.Id, cancellationToken);
        var subjects = await db.Subjects.AsNoTracking().Where(s => s.SemesterId == semesterId).ToDictionaryAsync(s => s.Id, cancellationToken);
        GroupKey Key(StudyGroup g) => new(programmes[g.ProgrammeId], g.Year, g.Series, g.Group, g.Subgroup);
        SubjectKey SubjectOf(Guid id) => new(programmes[subjects[id].ProgrammeId], subjects[id].Year, subjects[id].Name);

        var slots = await db.TimetableSlots.AsNoTracking().Include(s => s.Groups)
            .Where(s => s.SemesterId == semesterId && s.Source == source).ToListAsync(cancellationToken);
        var sourceSubjects = subjects.Values.Where(s => s.Source == source).Select(s => s.Id).ToList();
        var offers = await db.OfferedSlots.AsNoTracking().Where(o => sourceSubjects.Contains(o.SubjectId)).ToListAsync(cancellationToken);
        var notes = await db.TimetableNotes.AsNoTracking().Where(n => n.SemesterId == semesterId && n.Source == source).ToListAsync(cancellationToken);

        return new Snapshot(
            slots.Select(s => SlotLine(new ParsedSlot(SubjectOf(s.SubjectId), s.Kinds, s.Weekday, s.StartsAt, s.EndsAt, s.Weeks,
                s.RoomId is { } room ? rooms[room] : null, s.Teacher, s.Note, [.. s.Groups.Select(g => Key(groups[g.StudyGroupId]))],
                s.SourceCell, s.RawText))).ToList(),
            offers.Select(o => OfferLine(new ParsedOffer(SubjectOf(o.SubjectId), o.Kinds, o.Weekday, o.StartsAt, o.EndsAt, o.Weeks,
                o.RoomId is { } room ? rooms[room] : null, o.SourceCell, o.RawText))).ToList(),
            groups.Values.Where(g => g.Source == source && g.RetiredAt is null).Select(g => Key(g).ToString()).ToList(),
            subjects.Values.Where(s => s.Source == source && s.RetiredAt is null)
                .Select(s => SubjectLine(SubjectOf(s.Id), s.Choice, s.CampusVirtualCourseId)).ToList(),
            notes.Select(n => $"{programmes[n.ProgrammeId]} y{n.Year}: {n.Text}").ToList());
    }

    private static string SubjectLine(SubjectKey subject, SubjectChoice choice, int? campusVirtualCourseId) =>
        $"{subject} [{choice}]{(campusVirtualCourseId is { } id ? $" cv:{id}" : "")}";

    private static string SlotLine(ParsedSlot slot) =>
        $"{string.Join(" ", slot.Groups.Select(g => g.ToString()).Order(StringComparer.Ordinal))} | {slot.Weekday} "
        + $"{slot.StartsAt:HH\\:mm}-{slot.EndsAt:HH\\:mm} | {slot.Subject.Name} {slot.Kinds} | room {slot.Room ?? "-"} | "
        + $"weeks {slot.Weeks} | {slot.Teacher ?? "-"}{(slot.Note is null ? "" : " | note: " + slot.Note)}";

    private static string OfferLine(ParsedOffer offer) =>
        $"{offer.Subject} {offer.Kinds} | {offer.Weekday} {offer.StartsAt:HH\\:mm}-{offer.EndsAt:HH\\:mm} | "
        + $"room {offer.Room ?? "-"} | weeks {offer.Weeks}";
}
