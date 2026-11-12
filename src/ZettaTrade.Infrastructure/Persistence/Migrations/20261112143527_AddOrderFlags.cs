using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZettaTrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PostOnly",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReduceOnly",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostOnly",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ReduceOnly",
                table: "Orders");
        }
    }
}
