using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PoolManager.Migrations
{
    /// <inheritdoc />
    public partial class AddStartTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Matches_StartTime",
                table: "Matches",
                column: "StartTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Matches_StartTime",
                table: "Matches");
        }
    }
}
