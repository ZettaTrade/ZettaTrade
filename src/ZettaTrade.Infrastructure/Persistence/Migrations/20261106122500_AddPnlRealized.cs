using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZettaTrade.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPnlRealized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PnlRealized",
                columns: table => new
                {
                    ExchangeId = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Side = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<string>(type: "TEXT", nullable: false),
                    Commission = table.Column<string>(type: "TEXT", nullable: false),
                    RealizedAmount = table.Column<string>(type: "TEXT", nullable: false),
                    Effect = table.Column<string>(type: "TEXT", nullable: false),
                    RealizedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PnlRealized", x => new { x.ExchangeId, x.OrderId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PnlRealized");
        }
    }
}
