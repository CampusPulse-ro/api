using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusPulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImportRunVersionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_timetable_import_runs_imported_at_id",
                table: "timetable_import_runs",
                columns: new[] { "imported_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_timetable_import_runs_imported_at_id",
                table: "timetable_import_runs");
        }
    }
}
