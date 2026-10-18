using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusPulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSubjectCampusVirtualCourse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "campus_virtual_course_id",
                table: "subjects",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "campus_virtual_course_id",
                table: "subjects");
        }
    }
}
