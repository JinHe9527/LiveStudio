using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStudio.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultipartSnapshotUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "SnapshotUploads";
                """);
            migrationBuilder.AddColumn<string>(
                name: "MultipartUploadId",
                table: "SnapshotUploads",
                type: "text",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MultipartUploadId",
                table: "SnapshotUploads");
        }
    }
}
