using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveStudio.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobExecutionOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobEvents_JobId",
                table: "JobEvents");

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionId",
                table: "RemoteJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastEventSequence",
                table: "RemoteJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionId",
                table: "JobEvents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "JobEvents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "VerificationDetail",
                table: "JobEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobEvents_JobId_ExecutionId_Sequence",
                table: "JobEvents",
                columns: new[] { "JobId", "ExecutionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobEvents_JobId_ExecutionId_Sequence",
                table: "JobEvents");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "RemoteJobs");

            migrationBuilder.DropColumn(
                name: "LastEventSequence",
                table: "RemoteJobs");

            migrationBuilder.DropColumn(
                name: "ExecutionId",
                table: "JobEvents");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "JobEvents");

            migrationBuilder.DropColumn(
                name: "VerificationDetail",
                table: "JobEvents");

            migrationBuilder.CreateIndex(
                name: "IX_JobEvents_JobId",
                table: "JobEvents",
                column: "JobId");
        }
    }
}
