using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireHQ.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CeSmsDeliveryIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider_message_id",
                schema: "identity",
                table: "notification_deliveries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "provider_message_id",
                schema: "identity",
                table: "notification_deliveries");
        }
    }
}
