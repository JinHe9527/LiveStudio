using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStudio.Cloud.Data.Migrations
{
    public partial class SnapshotObjectLifecycle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ObjectDeletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectDeletions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotAssets",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotAssets", x => new { x.OrganizationId, x.SnapshotId, x.Sha256 });
                    table.ForeignKey(
                        name: "FK_SnapshotAssets_Assets_OrganizationId_Sha256",
                        columns: x => new { x.OrganizationId, x.Sha256 },
                        principalTable: "Assets",
                        principalColumns: new[] { "OrganizationId", "Sha256" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SnapshotAssets_Snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "Snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ObjectDeletions_NextAttemptAt",
                table: "ObjectDeletions",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectDeletions_ObjectKey",
                table: "ObjectDeletions",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotAssets_OrganizationId_Sha256",
                table: "SnapshotAssets",
                columns: new[] { "OrganizationId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotAssets_SnapshotId",
                table: "SnapshotAssets",
                column: "SnapshotId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ObjectDeletions");
            migrationBuilder.DropTable(name: "SnapshotAssets");
        }
    }
}
