using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireHQ.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CeSmsInboundTokenLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cleared_keyword",
                schema: "identity",
                table: "sms_opt_outs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "identity",
                table: "sms_opt_outs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inbound_current_token_verified_at_utc",
                schema: "identity",
                table: "notification_channel_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inbound_last_rejected_at_utc",
                schema: "identity",
                table: "notification_channel_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inbound_previous_token_seen_at_utc",
                schema: "identity",
                table: "notification_channel_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "inbound_rejected_count",
                schema: "identity",
                table: "notification_channel_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inbound_webhook_token_minted_at_utc",
                schema: "identity",
                table: "notification_channel_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inbound_webhook_token_prefix",
                schema: "identity",
                table: "notification_channel_configs",
                type: "character varying(6)",
                maxLength: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "previous_inbound_webhook_token_expires_at_utc",
                schema: "identity",
                table: "notification_channel_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_inbound_webhook_token_hash",
                schema: "identity",
                table: "notification_channel_configs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_channel_configs_inbound_token",
                schema: "identity",
                table: "notification_channel_configs",
                column: "inbound_webhook_token_hash",
                filter: "inbound_webhook_token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_channel_configs_prev_inbound_token",
                schema: "identity",
                table: "notification_channel_configs",
                column: "previous_inbound_webhook_token_hash",
                filter: "previous_inbound_webhook_token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_channel_configs_inbound_token",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropIndex(
                name: "ix_notification_channel_configs_prev_inbound_token",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "cleared_keyword",
                schema: "identity",
                table: "sms_opt_outs");

            migrationBuilder.DropColumn(
                name: "reason",
                schema: "identity",
                table: "sms_opt_outs");

            migrationBuilder.DropColumn(
                name: "inbound_current_token_verified_at_utc",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "inbound_last_rejected_at_utc",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "inbound_previous_token_seen_at_utc",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "inbound_rejected_count",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "inbound_webhook_token_minted_at_utc",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "inbound_webhook_token_prefix",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "previous_inbound_webhook_token_expires_at_utc",
                schema: "identity",
                table: "notification_channel_configs");

            migrationBuilder.DropColumn(
                name: "previous_inbound_webhook_token_hash",
                schema: "identity",
                table: "notification_channel_configs");
        }
    }
}
