using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZettaTrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCommission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Commission",
                table: "Orders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commission",
                table: "Orders");
        }
    }
}
