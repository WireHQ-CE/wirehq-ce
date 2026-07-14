using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WireHQ.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orch");

            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.EnsureSchema(
                name: "wg");

            migrationBuilder.EnsureSchema(
                name: "modules");

            migrationBuilder.CreateTable(
                name: "agent_enrollment_tokens",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(95)", maxLength: 95, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_enrollment_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    certificate_fingerprint = table.Column<string>(type: "character varying(95)", maxLength: 95, nullable: false),
                    certificate_pem = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    enrolled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_chain_anchors",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    boundary_prev_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_chain_anchors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    impersonator_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    changes = table.Column<string>(type: "json", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    request_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    prev_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    entry_hash = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "brand_assets",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "config_versions",
                schema: "wg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    content_encrypted = table.Column<string>(type: "text", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_config_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deployment_jobs",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    desired_config_version = table.Column<int>(type: "integer", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deployment_targets",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ssh_target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key_custody = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    auto_reconverge = table.Column<bool>(type: "boolean", nullable: false),
                    interface_name = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_targets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_verification_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_verification_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "enrollment_batches",
                schema: "wg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_filename = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: false),
                    valid_rows = table.Column<int>(type: "integer", nullable: false),
                    error_rows = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "install_identity",
                schema: "modules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_install_identity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "instance_runtime_status",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    desired_config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    actual_config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    has_drift = table.Column<bool>(type: "boolean", nullable: false),
                    drift_detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    observed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_runtime_status", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "instances",
                schema: "wg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    slug = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    provider_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    listen_port = table.Column<int>(type: "integer", nullable: false),
                    interface_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    public_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    private_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dns = table.Column<string>(type: "jsonb", nullable: false),
                    endpoint_host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    mtu = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    provider_settings = table.Column<string>(type: "jsonb", nullable: false),
                    last_status_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "key_material",
                schema: "wg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ciphertext = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    public_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_key_material", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    joined_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mfa_credentials",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    secret = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "module_licences",
                schema: "modules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    licence_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    licence_key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    activation_token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    grace_ends_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_module_licences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "networks",
                schema: "wg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    cidr = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dns = table.Column<string>(type: "jsonb", nullable: false),
                    default_allowed_ips = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_networks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channel_configs",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    destination_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    credential_ciphertext = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    from_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channel_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channel_usage",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    day_utc = table.Column<DateOnly>(type: "date", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channel_usage", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    required_feature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    rendered_subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    rendered_body = table.Column<string>(type: "text", nullable: false),
                    dedup_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_response_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_jobs",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audit_log_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    summary_snapshot = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    vpn_status_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    product_announcements = table.Column<bool>(type: "boolean", nullable: false),
                    billing_notifications = table.Column<bool>(type: "boolean", nullable: false),
                    marketing_emails = table.Column<bool>(type: "boolean", nullable: false),
                    service_status_alerts = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_rules",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_pattern = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    channel_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    audience = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    audience_ref = table.Column<Guid>(type: "uuid", nullable: true),
                    required_feature = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "onboarding_profiles",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    company_website = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    industry = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    team_size = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    vpn_users = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    current_vpn_solution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    use_case = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    skipped_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_onboarding_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "org_certificate_authorities",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_pem = table.Column<string>(type: "text", nullable: false),
                    private_key_ciphertext = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_certificate_authorities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organization_settings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    require_mfa = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    session_idle_timeout_minutes = table.Column<int>(type: "integer", nullable: false),
                    enabled_modules = table.Column<string>(type: "jsonb", nullable: false),
                    flags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    edition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    data_region = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    website = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    company_size = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    country = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_reset_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "peers",
                schema: "wg",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    device_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    public_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    private_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preshared_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    allowed_ips = table.Column<string>(type: "jsonb", nullable: false),
                    persistent_keepalive = table.Column<int>(type: "integer", nullable: true),
                    last_handshake_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rx_bytes = table.Column<long>(type: "bigint", nullable: false),
                    tx_bytes = table.Column<long>(type: "bigint", nullable: false),
                    last_endpoint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enrollment_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_peers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_settings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    turnstile_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    turnstile_site_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    turnstile_secret_ciphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    smtp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    smtp_host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: false),
                    smtp_username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    smtp_password_ciphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    smtp_from_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    smtp_from_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    smtp_use_ssl = table.Column<bool>(type: "boolean", nullable: false),
                    analytics_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    matomo_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    matomo_site_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    stripe_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    stripe_publishable_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    stripe_secret_ciphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    stripe_webhook_secret_ciphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    stripe_pro_monthly_price_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    stripe_pro_annual_price_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    marketplace_commerce_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    stripe_marketplace_webhook_secret_ciphertext = table.Column<string>(type: "text", nullable: true),
                    pricing_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "GBP"),
                    pro_monthly_price = table.Column<decimal>(type: "numeric(10,2)", nullable: false, defaultValue: 29m),
                    pro_annual_price = table.Column<decimal>(type: "numeric(10,2)", nullable: false, defaultValue: 290m),
                    product_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    brand_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    logo_light_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    logo_dark_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    favicon_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    brand_revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recovery_codes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recovery_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ssh_targets",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    auth_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    credential_ciphertext = table.Column<string>(type: "text", nullable: false),
                    host_key_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ssh_targets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    slug = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_avatars",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_avatars", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    impersonated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    mfa_satisfied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    first_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    terms_accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    username = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: true),
                    job_title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    phone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    timezone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    avatar_updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    password_updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    platform_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    mfa_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    failed_sign_in_attempts = table.Column<int>(type: "integer", nullable: false),
                    lockout_ends_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sign_in_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_response_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    signing_secret_ciphertext = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_key_scopes",
                schema: "identity",
                columns: table => new
                {
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key_scopes", x => new { x.api_key_id, x.permission_key });
                    table.ForeignKey(
                        name: "fk_api_key_scopes_api_keys_api_key_id",
                        column: x => x.api_key_id,
                        principalSchema: "identity",
                        principalTable: "api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_events",
                schema: "orch",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_events_deployment_job_deployment_job_id",
                        column: x => x.deployment_job_id,
                        principalSchema: "orch",
                        principalTable: "deployment_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "membership_roles",
                schema: "identity",
                columns: table => new
                {
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_roles", x => new { x.membership_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_membership_roles_memberships_membership_id",
                        column: x => x.membership_id,
                        principalSchema: "core",
                        principalTable: "memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "identity",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                schema: "core",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_members", x => new { x.team_id, x.membership_id });
                    table.ForeignKey(
                        name: "fk_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "core",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_event_subscriptions",
                schema: "identity",
                columns: table => new
                {
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pattern = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_event_subscriptions", x => new { x.endpoint_id, x.pattern });
                    table.ForeignKey(
                        name: "fk_webhook_event_subscriptions_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "identity",
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_enrollment_tokens_organization_id_id",
                schema: "orch",
                table: "agent_enrollment_tokens",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_enrollment_tokens_token_hash",
                schema: "orch",
                table: "agent_enrollment_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_certificate_fingerprint",
                schema: "orch",
                table: "agents",
                column: "certificate_fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_organization_id_id",
                schema: "orch",
                table: "agents",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key_hash",
                schema: "identity",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_organization_id",
                schema: "identity",
                table: "api_keys",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_chain_anchors_organization_id_boundary_prev_hash",
                schema: "audit",
                table: "audit_chain_anchors",
                columns: new[] { "organization_id", "boundary_prev_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_user_id",
                schema: "audit",
                table: "audit_logs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_id_occurred_at_utc",
                schema: "audit",
                table: "audit_logs",
                columns: new[] { "organization_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_target_type_target_id",
                schema: "audit",
                table: "audit_logs",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_config_versions_target_type_target_id_version",
                schema: "wg",
                table: "config_versions",
                columns: new[] { "target_type", "target_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_events_deployment_job_id",
                schema: "orch",
                table: "deployment_events",
                column: "deployment_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_jobs_organization_id_instance_id",
                schema: "orch",
                table: "deployment_jobs",
                columns: new[] { "organization_id", "instance_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_jobs_status_created_at_utc",
                schema: "orch",
                table: "deployment_jobs",
                columns: new[] { "status", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_targets_instance_id",
                schema: "orch",
                table: "deployment_targets",
                column: "instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_tokens_token_hash",
                schema: "identity",
                table: "email_verification_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_tokens_user_id",
                schema: "identity",
                table: "email_verification_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_batches_organization_id_instance_id",
                schema: "wg",
                table: "enrollment_batches",
                columns: new[] { "organization_id", "instance_id" });

            migrationBuilder.CreateIndex(
                name: "ix_instance_runtime_status_instance_id",
                schema: "orch",
                table: "instance_runtime_status",
                column: "instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instances_network_id",
                schema: "wg",
                table: "instances",
                column: "network_id");

            migrationBuilder.CreateIndex(
                name: "ix_instances_organization_id_id",
                schema: "wg",
                table: "instances",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_key_material_owner_type_owner_id",
                schema: "wg",
                table: "key_material",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_roles_role_id",
                schema: "identity",
                table: "membership_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_memberships_organization_id_user_id",
                schema: "core",
                table: "memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                schema: "core",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_credentials_user_id",
                schema: "identity",
                table: "mfa_credentials",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_modules_module_licences_module_slug",
                schema: "modules",
                table: "module_licences",
                column: "module_slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_networks_organization_id_id",
                schema: "wg",
                table: "networks",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_notification_channel_configs_org_channel",
                schema: "identity",
                table: "notification_channel_configs",
                columns: new[] { "organization_id", "channel_kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_channel_usage_org_channel_day",
                schema: "identity",
                table: "notification_channel_usage",
                columns: new[] { "organization_id", "channel_kind", "day_utc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_rule_id",
                schema: "identity",
                table: "notification_deliveries",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_status_next_attempt",
                schema: "identity",
                table: "notification_deliveries",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_jobs_status",
                schema: "identity",
                table: "notification_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_user_id",
                schema: "identity",
                table: "notification_preferences",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_rules_organization_id",
                schema: "identity",
                table: "notification_rules",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_onboarding_profiles_organization_id",
                schema: "core",
                table: "onboarding_profiles",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_org_certificate_authorities_organization_id",
                schema: "orch",
                table: "org_certificate_authorities",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_settings_organization_id",
                schema: "core",
                table: "organization_settings",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                schema: "core",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_token_hash",
                schema: "identity",
                table: "password_reset_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_user_id",
                schema: "identity",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_peers_instance_id_assigned_address",
                schema: "wg",
                table: "peers",
                columns: new[] { "instance_id", "assigned_address" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_peers_instance_id_last_handshake_at_utc",
                schema: "wg",
                table: "peers",
                columns: new[] { "instance_id", "last_handshake_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_peers_instance_id_public_key",
                schema: "wg",
                table: "peers",
                columns: new[] { "instance_id", "public_key" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_peers_membership_id",
                schema: "wg",
                table: "peers",
                column: "membership_id");

            migrationBuilder.CreateIndex(
                name: "ix_permissions_key",
                schema: "identity",
                table: "permissions",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recovery_codes_user_id",
                schema: "identity",
                table: "recovery_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_session_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission_id",
                schema: "identity",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_organization_id_name",
                schema: "identity",
                table: "roles",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ssh_targets_organization_id_id",
                schema: "orch",
                table: "ssh_targets",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_team_members_membership_id",
                schema: "core",
                table: "team_members",
                column: "membership_id");

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id_id",
                schema: "core",
                table: "teams",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_avatars_user_id",
                schema: "identity",
                table: "user_avatars",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id",
                schema: "identity",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                schema: "identity",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                schema: "identity",
                table: "users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_endpoint_id",
                schema: "identity",
                table: "webhook_deliveries",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_status_next_attempt",
                schema: "identity",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_organization_id",
                schema: "identity",
                table: "webhook_endpoints",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_enrollment_tokens",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "agents",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "api_key_scopes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "audit_chain_anchors",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "brand_assets",
                schema: "core");

            migrationBuilder.DropTable(
                name: "config_versions",
                schema: "wg");

            migrationBuilder.DropTable(
                name: "deployment_events",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "deployment_targets",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "email_verification_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "enrollment_batches",
                schema: "wg");

            migrationBuilder.DropTable(
                name: "install_identity",
                schema: "modules");

            migrationBuilder.DropTable(
                name: "instance_runtime_status",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "instances",
                schema: "wg");

            migrationBuilder.DropTable(
                name: "key_material",
                schema: "wg");

            migrationBuilder.DropTable(
                name: "membership_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "mfa_credentials",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "module_licences",
                schema: "modules");

            migrationBuilder.DropTable(
                name: "networks",
                schema: "wg");

            migrationBuilder.DropTable(
                name: "notification_channel_configs",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "notification_channel_usage",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "notification_jobs",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "notification_preferences",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "notification_rules",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "onboarding_profiles",
                schema: "core");

            migrationBuilder.DropTable(
                name: "org_certificate_authorities",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "organization_settings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "core");

            migrationBuilder.DropTable(
                name: "password_reset_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "peers",
                schema: "wg");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "platform_settings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "recovery_codes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "ssh_targets",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "team_members",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_avatars",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "webhook_event_subscriptions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "deployment_jobs",
                schema: "orch");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "core");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "core");

            migrationBuilder.DropTable(
                name: "webhook_endpoints",
                schema: "identity");
        }
    }
}
