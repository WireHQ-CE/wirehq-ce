using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireHQ.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CeCatchUpFrom0901 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phone_e164",
                schema: "identity",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "phone_verified",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "phone_verified_at_utc",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sms_alerts",
                schema: "identity",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "a2p_attested_at_utc",
                schema: "identity",
                table: "notification_channel_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "a2p_attested_by",
                schema: "identity",
                table: "notification_channel_configs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "a2p_reference",
                schema: "identity",
                table: "notification_channel_configs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inbound_webhook_token_hash",
                schema: "identity",
                table: "notification_channel_configs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "phone_verifications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_phone_verifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sms_consents",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consented = table.Column<bool>(type: "boolean", nullable: false),
                    consented_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disclosure_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sms_opt_outs",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sender = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    keyword = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    suppressed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_opt_outs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_phone_e164",
                schema: "identity",
                table: "users",
                column: "phone_e164",
                unique: true,
                filter: "phone_verified = true");

            migrationBuilder.CreateIndex(
                name: "ix_phone_verifications_organization_id",
                schema: "identity",
                table: "phone_verifications",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_phone_verifications_phone_created",
                schema: "identity",
                table: "phone_verifications",
                columns: new[] { "phone_e164", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_phone_verifications_user_created",
                schema: "identity",
                table: "phone_verifications",
                columns: new[] { "user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_sms_consents_organization_id_user_id",
                schema: "identity",
                table: "sms_consents",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sms_opt_outs_organization_id_phone",
                schema: "identity",
                table: "sms_opt_outs",
                columns: new[] { "organization_id", "phone_e164" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "phone_verifications",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "sms_consents",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "sms_opt_outs",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ix_users_phone_e164",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone_e164",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone_verified",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone_verified_at_utc",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "sms_alerts",
                schema: "identity",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "a2p_attested_at_utc",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "a2p_attested_by",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "a2p_reference",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "inbound_webhook_token_hash",
                schema: "identity",
                table: "notification_channel_configs");
        }
    }
}
