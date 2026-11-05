using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotebookApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchedFor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchedFor",
                table: "Messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchedFor",
                table: "Messages");
        }
    }
}
