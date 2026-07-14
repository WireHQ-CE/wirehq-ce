-- =============================================================================
-- WireHQ — Row-Level Security (Layer 2 tenant isolation).  ADR-027.
-- Authoritative script. Applied automatically on boot by
-- ApplicationDbContextInitializer AFTER migrations; embedded in the Infrastructure
-- assembly. Idempotent — safe to re-run on every boot.  See docs/03-multi-tenancy.md.
--
-- Data-driven: every BASE TABLE carrying an `organization_id` column in the tenant
-- schemas gets the `tenant_isolation` policy, so a NEW tenant table is covered
-- automatically — no hand-maintained list to forget (the gap that left rls.sql
-- stale at 4 of 16 tables; see ADR-027 / HANDOFF gap #10).
--
-- Fail-closed: with neither `app.bypass_rls='on'` nor a matching `app.current_org`
-- the policy matches no rows. The TenantConnectionInterceptor sets both GUCs per
-- connection from ITenantContext (org-scoped requests set current_org; trusted
-- cross-tenant paths — platform handlers, session minting, provisioning, the
-- dispatcher/reconciler claim, boot seeders — set bypass).
--
-- ENFORCEMENT comes from the app connecting as the non-privileged `wirehq_app` role
-- below (NOT a superuser, NOT the table owner → always subject to RLS). No FORCE is
-- needed (and would be inert anyway: a superuser/owner bypasses RLS regardless).
-- Migrations, this script, and the boot seeders run on the owner/admin connection.
-- =============================================================================

-- 1. The non-privileged RUNTIME role the app connects as (ConnectionStrings:Default).
--    NOBYPASSRLS + non-owner is the whole point — it is always subject to the policies
--    and cannot DDL. The password is synced from the Default connection string on boot
--    by ApplicationDbContextInitializer (kept out of this committed script).
DO $role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'wirehq_app') THEN
        CREATE ROLE wirehq_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END$role$;

GRANT USAGE ON SCHEMA core, identity, audit, wg, orch TO wirehq_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA core, identity, wg, orch TO wirehq_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA core, identity, audit, wg, orch TO wirehq_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA core, identity, wg, orch
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO wirehq_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA core, identity, audit, wg, orch
    GRANT USAGE, SELECT ON SEQUENCES TO wirehq_app;

-- The audit schema is append-only for the app role: SELECT + INSERT, never UPDATE/DELETE — by default too,
-- so every monthly partition (existing or created later by the owner) is born immutable without a per-table
-- REVOKE. The audit_logs hash chain (ADR-031) relies on this; only the owner (migrations, backfill, the
-- retention sweeper's partition drops) may mutate or remove audit rows.
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO wirehq_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT ON TABLES TO wirehq_app;

-- The marketplace schema (licensing/orders/licences — SaaS-only; docs/19-marketplace-licensing.md §7). It is
-- platform-global (no organization_id), so it is deliberately NOT covered by the tenant_isolation loop below;
-- access is authorised at the application layer (platform operators + the anonymous licensing endpoints).
-- Guarded on schema existence so this core, always-re-run script is a clean no-op on a self-hosted install,
-- which never has the schema. Grants must live here (not in the migration) because wirehq_app is created above,
-- after migrations run.
DO $marketplace$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'marketplace') THEN
        GRANT USAGE ON SCHEMA marketplace TO wirehq_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA marketplace TO wirehq_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA marketplace TO wirehq_app;
        ALTER DEFAULT PRIVILEGES IN SCHEMA marketplace GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO wirehq_app;
        ALTER DEFAULT PRIVILEGES IN SCHEMA marketplace GRANT USAGE, SELECT ON SEQUENCES TO wirehq_app;
    END IF;
END$marketplace$;

-- The status schema (uptime samples + daily rollups — SaaS-only; docs/20-status-page.md §4). Like the
-- marketplace schema it is platform-global (no organization_id — status describes the service, not a tenant),
-- so it is NOT covered by the tenant_isolation loop below; the self-probe writes it and the public read is
-- anonymous. Guarded on schema existence so this core, always-re-run script is a clean no-op on a self-hosted
-- install, which never has the schema.
DO $status$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'status') THEN
        GRANT USAGE ON SCHEMA status TO wirehq_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA status TO wirehq_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA status TO wirehq_app;
        ALTER DEFAULT PRIVILEGES IN SCHEMA status GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO wirehq_app;
        ALTER DEFAULT PRIVILEGES IN SCHEMA status GRANT USAGE, SELECT ON SEQUENCES TO wirehq_app;
    END IF;
END$status$;

-- The modules schema (CE Marketplace install identity + activated module licences — CE-ONLY;
-- docs/29-ce-marketplace-modules.md M-5). Platform-global: install-scoped, NOT tenant-scoped (no
-- organization_id — a CE licence is per install, not per org), so it is deliberately NOT covered by the
-- tenant_isolation loop below. This is the INVERSE of the marketplace/status schemas (present on a CE install,
-- absent in SaaS) but the schema-existence guard makes the same block correct either way — a clean no-op on the
-- SaaS build, which never carries the schema. The grant MUST live here (not in the CE's InitialCreate) because
-- wirehq_app is created above, after migrations run — omit it and the non-owner runtime role 500s every gated
-- request that touches the activation store.
DO $modules$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'modules') THEN
        GRANT USAGE ON SCHEMA modules TO wirehq_app;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA modules TO wirehq_app;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA modules TO wirehq_app;
        ALTER DEFAULT PRIVILEGES IN SCHEMA modules GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO wirehq_app;
        ALTER DEFAULT PRIVILEGES IN SCHEMA modules GRANT USAGE, SELECT ON SEQUENCES TO wirehq_app;
    END IF;
END$modules$;

-- 2. The tenant_isolation policy on every base table carrying organization_id.
DO $policies$
DECLARE r record;
BEGIN
    FOR r IN
        SELECT c.table_schema AS s, c.table_name AS t
        FROM information_schema.columns c
        JOIN information_schema.tables tb
          ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
        WHERE c.column_name = 'organization_id'
          AND c.table_schema IN ('core', 'identity', 'wg', 'orch')
          AND tb.table_type = 'BASE TABLE'
    LOOP
        EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY;', r.s, r.t);
        EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I.%I;', r.s, r.t);
        EXECUTE format($pol$
            CREATE POLICY tenant_isolation ON %I.%I
              USING (current_setting('app.bypass_rls', true) = 'on'
                     OR organization_id = NULLIF(current_setting('app.current_org', true), '')::uuid)
              WITH CHECK (current_setting('app.bypass_rls', true) = 'on'
                     OR organization_id = NULLIF(current_setting('app.current_org', true), '')::uuid)
        $pol$, r.s, r.t);
    END LOOP;
END$policies$;

-- 3. The tenant root (core.organizations) is keyed by `id`, not organization_id — scope by id.
ALTER TABLE core.organizations ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation ON core.organizations;
CREATE POLICY tenant_isolation ON core.organizations
  USING (current_setting('app.bypass_rls', true) = 'on'
         OR id = NULLIF(current_setting('app.current_org', true), '')::uuid)
  WITH CHECK (current_setting('app.bypass_rls', true) = 'on'
         OR id = NULLIF(current_setting('app.current_org', true), '')::uuid);

-- 4. The audit log is append-only for the application role: no UPDATE/DELETE on the partitioned parent or
--    any of its partitions (defence-in-depth on top of the append-only default privileges above — covers any
--    table that an earlier boot's broad grant may have touched).
REVOKE UPDATE, DELETE ON ALL TABLES IN SCHEMA audit FROM wirehq_app;
