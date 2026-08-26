using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStudio.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedDeviceRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                table: "Devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OrganizationId_RevokedAt",
                table: "Devices",
                columns: new[] { "OrganizationId", "RevokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_OrganizationId_RevokedAt",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "Devices");
        }
    }
}
