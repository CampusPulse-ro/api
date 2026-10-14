using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusPulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTimetable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "programmes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_programmes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    building = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    floor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rooms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "semesters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_year = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_semesters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "non_teaching_days",
                columns: table => new
                {
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_non_teaching_days", x => new { x.semester_id, x.date });
                    table.ForeignKey(
                        name: "fk_non_teaching_days_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "study_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    programme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    series = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    group_number = table.Column<int>(type: "integer", nullable: false),
                    subgroup = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_study_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_study_groups_programmes_programme_id",
                        column: x => x.programme_id,
                        principalTable: "programmes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_study_groups_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subjects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    programme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    short_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    choice = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subjects", x => x.id);
                    table.ForeignKey(
                        name: "fk_subjects_programmes_programme_id",
                        column: x => x.programme_id,
                        principalTable: "programmes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subjects_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teaching_weeks",
                columns: table => new
                {
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teaching_weeks", x => new { x.semester_id, x.number });
                    table.ForeignKey(
                        name: "fk_teaching_weeks_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timetable_import_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sheets = table.Column<List<string>>(type: "text[]", nullable: false),
                    file_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    overrides_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    parser_version = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    warnings = table.Column<List<string>>(type: "text[]", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timetable_import_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_timetable_import_runs_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timetable_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    programme_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_cell = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timetable_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_timetable_notes_programmes_programme_id",
                        column: x => x.programme_id,
                        principalTable: "programmes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_timetable_notes_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_settings",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    study_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student_settings", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_student_settings_study_groups_study_group_id",
                        column: x => x.study_group_id,
                        principalTable: "study_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_student_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offered_slots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kinds = table.Column<int>(type: "integer", nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: false),
                    starts_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    ends_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    weeks = table.Column<int>(type: "integer", nullable: false),
                    source_cell = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offered_slots", x => x.id);
                    table.ForeignKey(
                        name: "fk_offered_slots_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_offered_slots_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timetable_slots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kinds = table.Column<int>(type: "integer", nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: false),
                    starts_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    ends_at = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    weeks = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: true),
                    teacher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_cell = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    raw_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timetable_slots", x => x.id);
                    table.ForeignKey(
                        name: "fk_timetable_slots_rooms_room_id",
                        column: x => x.room_id,
                        principalTable: "rooms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_timetable_slots_semesters_semester_id",
                        column: x => x.semester_id,
                        principalTable: "semesters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_timetable_slots_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "student_subjects",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offered_slot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student_subjects", x => new { x.user_id, x.subject_id });
                    table.ForeignKey(
                        name: "fk_student_subjects_offered_slots_offered_slot_id",
                        column: x => x.offered_slot_id,
                        principalTable: "offered_slots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_student_subjects_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_student_subjects_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timetable_slot_groups",
                columns: table => new
                {
                    slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    study_group_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_timetable_slot_groups", x => new { x.slot_id, x.study_group_id });
                    table.ForeignKey(
                        name: "fk_timetable_slot_groups_study_groups_study_group_id",
                        column: x => x.study_group_id,
                        principalTable: "study_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_timetable_slot_groups_timetable_slots_slot_id",
                        column: x => x.slot_id,
                        principalTable: "timetable_slots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_offered_slots_room_id",
                table: "offered_slots",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_offered_slots_subject_id",
                table: "offered_slots",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_programmes_code",
                table: "programmes",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rooms_code",
                table: "rooms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_semesters_academic_year_number",
                table: "semesters",
                columns: new[] { "academic_year", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_settings_study_group_id",
                table: "student_settings",
                column: "study_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_subjects_offered_slot_id",
                table: "student_subjects",
                column: "offered_slot_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_subjects_subject_id",
                table: "student_subjects",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_study_groups_programme_id",
                table: "study_groups",
                column: "programme_id");

            migrationBuilder.CreateIndex(
                name: "ix_study_groups_semester_id_programme_id_year_series_group_num",
                table: "study_groups",
                columns: new[] { "semester_id", "programme_id", "year", "series", "group_number", "subgroup" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subjects_programme_id",
                table: "subjects",
                column: "programme_id");

            migrationBuilder.CreateIndex(
                name: "ix_subjects_semester_id_programme_id_year_name",
                table: "subjects",
                columns: new[] { "semester_id", "programme_id", "year", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_timetable_import_runs_semester_id_source_imported_at",
                table: "timetable_import_runs",
                columns: new[] { "semester_id", "source", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_timetable_notes_programme_id",
                table: "timetable_notes",
                column: "programme_id");

            migrationBuilder.CreateIndex(
                name: "ix_timetable_notes_semester_id_programme_id_year",
                table: "timetable_notes",
                columns: new[] { "semester_id", "programme_id", "year" });

            migrationBuilder.CreateIndex(
                name: "ix_timetable_slot_groups_study_group_id",
                table: "timetable_slot_groups",
                column: "study_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_timetable_slots_room_id",
                table: "timetable_slots",
                column: "room_id");

            migrationBuilder.CreateIndex(
                name: "ix_timetable_slots_semester_id_source",
                table: "timetable_slots",
                columns: new[] { "semester_id", "source" });

            migrationBuilder.CreateIndex(
                name: "ix_timetable_slots_subject_id",
                table: "timetable_slots",
                column: "subject_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "non_teaching_days");

            migrationBuilder.DropTable(
                name: "student_settings");

            migrationBuilder.DropTable(
                name: "student_subjects");

            migrationBuilder.DropTable(
                name: "teaching_weeks");

            migrationBuilder.DropTable(
                name: "timetable_import_runs");

            migrationBuilder.DropTable(
                name: "timetable_notes");

            migrationBuilder.DropTable(
                name: "timetable_slot_groups");

            migrationBuilder.DropTable(
                name: "offered_slots");

            migrationBuilder.DropTable(
                name: "study_groups");

            migrationBuilder.DropTable(
                name: "timetable_slots");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "subjects");

            migrationBuilder.DropTable(
                name: "programmes");

            migrationBuilder.DropTable(
                name: "semesters");
        }
    }
}
