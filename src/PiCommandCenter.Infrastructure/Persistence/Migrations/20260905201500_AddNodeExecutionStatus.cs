using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeExecutionStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionStatusJson",
                table: "FleetNodes",
                type: "TEXT",
                maxLength: 131072,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionStatusJson",
                table: "FleetNodes");
        }
    }
}
