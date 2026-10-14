using CampusPulse.Domain.Push;
using CampusPulse.Domain.Timetable;
using CampusPulse.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

/// <summary>Entities come from CampusPulse.Domain; their mappings go here.</summary>
public sealed class CampusPulseDbContext(DbContextOptions<CampusPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<AccountDeletionCount> AccountDeletionCounts => Set<AccountDeletionCount>();

    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    public DbSet<PushReceiptPending> PushReceiptsPending => Set<PushReceiptPending>();

    public DbSet<Semester> Semesters => Set<Semester>();

    public DbSet<Programme> Programmes => Set<Programme>();

    public DbSet<StudyGroup> StudyGroups => Set<StudyGroup>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<Subject> Subjects => Set<Subject>();

    public DbSet<TimetableSlot> TimetableSlots => Set<TimetableSlot>();

    public DbSet<OfferedSlot> OfferedSlots => Set<OfferedSlot>();

    public DbSet<TimetableNote> TimetableNotes => Set<TimetableNote>();

    public DbSet<TimetableImportRun> TimetableImportRuns => Set<TimetableImportRun>();

    public DbSet<StudentSettings> StudentSettings => Set<StudentSettings>();

    public DbSet<StudentSubject> StudentSubjects => Set<StudentSubject>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.EntraObjectId).HasMaxLength(User.EntraObjectIdMaxLength).IsRequired();
            // One row per Microsoft account, enforced by the database: two first sign-ins at the
            // same moment can't both insert.
            user.HasIndex(u => u.EntraObjectId).IsUnique();
            user.Property(u => u.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
            user.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
            // Every table holding a student's data refers to this one with ON DELETE CASCADE, so
            // deleting the row deletes the account (AccountDeletion). A test fails otherwise.
        });

        modelBuilder.Entity<AccountDeletionCount>(count =>
        {
            count.ToTable("account_deletion_counts");
            count.HasKey(c => c.Day);
            count.Property(c => c.Day).ValueGeneratedNever();
        });

        modelBuilder.Entity<PushDevice>(device =>
        {
            device.ToTable("push_devices");
            device.HasKey(d => d.Id);
            device.Property(d => d.Id).ValueGeneratedNever();
            device.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            device.Property(d => d.Token).HasMaxLength(PushDevice.TokenMaxLength).IsRequired();
            // One row per app install: a token registered by another student moves to them.
            device.HasIndex(d => d.Token).IsUnique();
            // The per-student device cap drops the least recently registered.
            device.HasIndex(d => new { d.UserId, d.LastRegisteredAt });
            device.Property(d => d.Platform).HasMaxLength(10).HasConversion(
                platform => platform == PushPlatform.Ios ? "ios" : "android",
                code => code == "ios" ? PushPlatform.Ios : PushPlatform.Android);
        });

        modelBuilder.Entity<PushReceiptPending>(receipt =>
        {
            receipt.ToTable("push_receipts_pending");
            receipt.HasKey(r => r.TicketId);
            receipt.Property(r => r.TicketId).HasMaxLength(PushReceiptPending.TicketIdMaxLength).ValueGeneratedNever();
            receipt.HasOne<PushDevice>().WithMany().HasForeignKey(r => r.DeviceId).OnDelete(DeleteBehavior.Cascade);
            receipt.HasIndex(r => r.CreatedAt);
        });

        OnTimetableCreating(modelBuilder);
    }

    /// <summary>
    /// The timetable (CampusPulse-ro/api#5): what the import writes (semesters, programmes, study
    /// groups, rooms, subjects, slots, offers, notes, import runs) and what students own (their group
    /// and their optional subjects, both deleted with the account).
    /// </summary>
    private static void OnTimetableCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Semester>(semester =>
        {
            semester.ToTable("semesters");
            semester.HasKey(s => s.Id);
            semester.Property(s => s.Id).ValueGeneratedNever();
            semester.Property(s => s.AcademicYear).HasMaxLength(Semester.AcademicYearMaxLength).IsRequired();
            semester.HasIndex(s => new { s.AcademicYear, s.Number }).IsUnique();
            semester.HasMany(s => s.Weeks).WithOne().HasForeignKey(w => w.SemesterId).OnDelete(DeleteBehavior.Cascade);
            semester.HasMany(s => s.NonTeachingDays).WithOne().HasForeignKey(d => d.SemesterId).OnDelete(DeleteBehavior.Cascade);
            semester.Ignore(s => s.WeekCount);
            semester.Ignore(s => s.FirstDay);
            semester.Ignore(s => s.LastDay);
        });

        modelBuilder.Entity<TeachingWeek>(week =>
        {
            week.ToTable("teaching_weeks");
            week.HasKey(w => new { w.SemesterId, w.Number });
        });

        modelBuilder.Entity<NonTeachingDay>(day =>
        {
            day.ToTable("non_teaching_days");
            day.HasKey(d => new { d.SemesterId, d.Date });
            day.Property(d => d.Reason).HasMaxLength(NonTeachingDay.ReasonMaxLength).IsRequired();
        });

        modelBuilder.Entity<Programme>(programme =>
        {
            programme.ToTable("programmes");
            programme.HasKey(p => p.Id);
            programme.Property(p => p.Id).ValueGeneratedNever();
            programme.Property(p => p.Code).HasMaxLength(Programme.CodeMaxLength).IsRequired();
            programme.HasIndex(p => p.Code).IsUnique();
            programme.Property(p => p.Name).HasMaxLength(Programme.NameMaxLength).IsRequired();
        });

        modelBuilder.Entity<StudyGroup>(group =>
        {
            group.ToTable("study_groups");
            group.HasKey(g => g.Id);
            group.Property(g => g.Id).ValueGeneratedNever();
            group.HasOne<Semester>().WithMany().HasForeignKey(g => g.SemesterId).OnDelete(DeleteBehavior.Cascade);
            group.HasOne<Programme>().WithMany().HasForeignKey(g => g.ProgrammeId).OnDelete(DeleteBehavior.Restrict);
            group.Property(g => g.Series).HasMaxLength(StudyGroup.SeriesMaxLength).IsRequired();
            // "group" is an SQL keyword.
            group.Property(g => g.Group).HasColumnName("group_number");
            group.Property(g => g.Source).HasMaxLength(200).IsRequired();
            group.HasIndex(g => new { g.SemesterId, g.ProgrammeId, g.Year, g.Series, g.Group, g.Subgroup }).IsUnique();
            group.Ignore(g => g.Label);
        });

        modelBuilder.Entity<Room>(room =>
        {
            room.ToTable("rooms");
            room.HasKey(r => r.Id);
            room.Property(r => r.Id).ValueGeneratedNever();
            room.Property(r => r.Code).HasMaxLength(Room.CodeMaxLength).IsRequired();
            room.HasIndex(r => r.Code).IsUnique();
            room.Property(r => r.Building).HasMaxLength(100);
            room.Property(r => r.Floor).HasMaxLength(20);
        });

        modelBuilder.Entity<Subject>(subject =>
        {
            subject.ToTable("subjects");
            subject.HasKey(s => s.Id);
            subject.Property(s => s.Id).ValueGeneratedNever();
            subject.HasOne<Semester>().WithMany().HasForeignKey(s => s.SemesterId).OnDelete(DeleteBehavior.Cascade);
            subject.HasOne<Programme>().WithMany().HasForeignKey(s => s.ProgrammeId).OnDelete(DeleteBehavior.Restrict);
            subject.Property(s => s.Name).HasMaxLength(Subject.NameMaxLength).IsRequired();
            subject.Property(s => s.ShortName).HasMaxLength(Subject.ShortNameMaxLength);
            subject.Property(s => s.Choice).HasConversion<string>().HasMaxLength(10);
            subject.Property(s => s.Source).HasMaxLength(200).IsRequired();
            subject.HasIndex(s => new { s.SemesterId, s.ProgrammeId, s.Year, s.Name }).IsUnique();
        });

        modelBuilder.Entity<TimetableSlot>(slot =>
        {
            slot.ToTable("timetable_slots");
            slot.HasKey(s => s.Id);
            slot.Property(s => s.Id).ValueGeneratedNever();
            slot.HasOne<Semester>().WithMany().HasForeignKey(s => s.SemesterId).OnDelete(DeleteBehavior.Cascade);
            slot.HasOne<Subject>().WithMany().HasForeignKey(s => s.SubjectId).OnDelete(DeleteBehavior.Cascade);
            slot.HasOne<Room>().WithMany().HasForeignKey(s => s.RoomId).OnDelete(DeleteBehavior.SetNull);
            slot.Property(s => s.Weeks).HasConversion(w => w.Mask, mask => new WeekSet(mask));
            slot.Property(s => s.Teacher).HasMaxLength(TimetableSlot.TeacherMaxLength);
            slot.Property(s => s.Note).HasMaxLength(TimetableSlot.NoteMaxLength);
            slot.Property(s => s.Source).HasMaxLength(200).IsRequired();
            slot.Property(s => s.SourceCell).HasMaxLength(TimetableSlot.SourceCellMaxLength).IsRequired();
            slot.Property(s => s.RawText).HasMaxLength(TimetableSlot.RawTextMaxLength).IsRequired();
            slot.HasMany(s => s.Groups).WithOne().HasForeignKey(g => g.SlotId).OnDelete(DeleteBehavior.Cascade);
            // A re-import replaces one workbook's slots; the room view reads a room's.
            slot.HasIndex(s => new { s.SemesterId, s.Source });
            slot.HasIndex(s => s.RoomId);
        });

        modelBuilder.Entity<TimetableSlotGroup>(link =>
        {
            link.ToTable("timetable_slot_groups");
            link.HasKey(l => new { l.SlotId, l.StudyGroupId });
            link.HasOne<StudyGroup>().WithMany().HasForeignKey(l => l.StudyGroupId).OnDelete(DeleteBehavior.Cascade);
            link.HasIndex(l => l.StudyGroupId);
        });

        modelBuilder.Entity<OfferedSlot>(offer =>
        {
            offer.ToTable("offered_slots");
            offer.HasKey(o => o.Id);
            offer.Property(o => o.Id).ValueGeneratedNever();
            offer.HasOne<Subject>().WithMany().HasForeignKey(o => o.SubjectId).OnDelete(DeleteBehavior.Cascade);
            offer.HasOne<Room>().WithMany().HasForeignKey(o => o.RoomId).OnDelete(DeleteBehavior.SetNull);
            offer.Property(o => o.Weeks).HasConversion(w => w.Mask, mask => new WeekSet(mask));
            offer.Property(o => o.SourceCell).HasMaxLength(TimetableSlot.SourceCellMaxLength).IsRequired();
            offer.Property(o => o.RawText).HasMaxLength(TimetableSlot.RawTextMaxLength).IsRequired();
            offer.HasIndex(o => o.SubjectId);
            offer.HasIndex(o => o.RoomId);
        });

        modelBuilder.Entity<TimetableNote>(note =>
        {
            note.ToTable("timetable_notes");
            note.HasKey(n => n.Id);
            note.Property(n => n.Id).ValueGeneratedNever();
            note.HasOne<Semester>().WithMany().HasForeignKey(n => n.SemesterId).OnDelete(DeleteBehavior.Cascade);
            note.HasOne<Programme>().WithMany().HasForeignKey(n => n.ProgrammeId).OnDelete(DeleteBehavior.Restrict);
            note.Property(n => n.Text).HasMaxLength(TimetableNote.TextMaxLength).IsRequired();
            note.Property(n => n.Source).HasMaxLength(200).IsRequired();
            note.Property(n => n.SourceCell).HasMaxLength(TimetableSlot.SourceCellMaxLength).IsRequired();
            note.HasIndex(n => new { n.SemesterId, n.ProgrammeId, n.Year });
        });

        modelBuilder.Entity<TimetableImportRun>(run =>
        {
            run.ToTable("timetable_import_runs");
            run.HasKey(r => r.Id);
            run.Property(r => r.Id).ValueGeneratedNever();
            run.HasOne<Semester>().WithMany().HasForeignKey(r => r.SemesterId).OnDelete(DeleteBehavior.Cascade);
            run.Property(r => r.Source).HasMaxLength(200).IsRequired();
            run.Property(r => r.FileName).HasMaxLength(200).IsRequired();
            run.Property(r => r.FileSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            run.Property(r => r.OverridesSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            run.Property(r => r.Summary).IsRequired();
            run.HasIndex(r => new { r.SemesterId, r.Source, r.ImportedAt });
        });

        modelBuilder.Entity<StudentSettings>(settings =>
        {
            settings.ToTable("student_settings");
            settings.HasKey(s => s.UserId);
            settings.HasOne<User>().WithOne().HasForeignKey<StudentSettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            // Not under users. The import retires groups instead of deleting them; if a group row is ever
            // deleted, the student's choice is cleared, not their settings.
            settings.HasOne<StudyGroup>().WithMany().HasForeignKey(s => s.StudyGroupId).OnDelete(DeleteBehavior.SetNull);
            settings.HasIndex(s => s.StudyGroupId);
        });

        modelBuilder.Entity<StudentSubject>(choice =>
        {
            choice.ToTable("student_subjects");
            choice.HasKey(c => new { c.UserId, c.SubjectId });
            choice.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            // The import never deletes a subject (it retires it), so a choice outlives its subject's
            // disappearance from the faculty's files; this only matters if a subject row is ever deleted.
            choice.HasOne<Subject>().WithMany().HasForeignKey(c => c.SubjectId).OnDelete(DeleteBehavior.Cascade);
            choice.HasOne<OfferedSlot>().WithMany().HasForeignKey(c => c.OfferedSlotId).OnDelete(DeleteBehavior.SetNull);
            choice.HasIndex(c => c.SubjectId);
            choice.HasIndex(c => c.OfferedSlotId);
        });
    }
}
