using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestResultGitIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckpointCommitId",
                table: "RequestResults",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestBranch",
                table: "RequestResults",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointCommitId",
                table: "RequestResults");

            migrationBuilder.DropColumn(
                name: "RequestBranch",
                table: "RequestResults");
        }
    }
}
