using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusPulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    platform = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_push_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "push_receipts_pending",
                columns: table => new
                {
                    ticket_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_receipts_pending", x => x.ticket_id);
                    table.ForeignKey(
                        name: "fk_push_receipts_pending_push_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "push_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_push_devices_token",
                table: "push_devices",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_devices_user_id_last_registered_at",
                table: "push_devices",
                columns: new[] { "user_id", "last_registered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_push_receipts_pending_created_at",
                table: "push_receipts_pending",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_push_receipts_pending_device_id",
                table: "push_receipts_pending",
                column: "device_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_receipts_pending");

            migrationBuilder.DropTable(
                name: "push_devices");
        }
    }
}
