using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireHQ.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CeAgentCertificateRotationGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "previous_certificate_fingerprint",
                schema: "orch",
                table: "agents",
                type: "character varying(95)",
                maxLength: 95,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_previous_certificate_fingerprint",
                schema: "orch",
                table: "agents",
                column: "previous_certificate_fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agents_previous_certificate_fingerprint",
                schema: "orch",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "previous_certificate_fingerprint",
                schema: "orch",
                table: "agents");
        }
    }
}
