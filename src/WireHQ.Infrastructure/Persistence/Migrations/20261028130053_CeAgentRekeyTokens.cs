using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireHQ.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CeAgentRekeyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "rekey_agent_id",
                schema: "orch",
                table: "agent_enrollment_tokens",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rekey_agent_id",
                schema: "orch",
                table: "agent_enrollment_tokens");
        }
    }
}
