using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FuelUp.Infrastructure;

/// <summary>
/// The company the current request acts for. Set by the API from the signed-in user's database record
/// (never from anything the client sends). Two layers use it:
///   1. EF Core global query filters + SaveChanges checks (application layer).
///   2. PostgreSQL row-level security via the session variable app.company_id (database layer),
///      so a forgotten filter or a hand-written query still cannot read another company's rows.
/// </summary>
public sealed class TenantContext
{
    public Guid? CompanyId { get; private set; }
    public void Set(Guid companyId) => CompanyId = companyId;
    public void Clear() => CompanyId = null;
}

public sealed class TenantViolationException(string message) : Exception(message);

/// <summary>Pushes the current company into the PostgreSQL session every time EF opens a connection.</summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = Build(connection, eventData.Context);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await using var cmd = Build(connection, eventData.Context);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static DbCommand Build(DbConnection connection, DbContext? context)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.company_id', @cid, false)";   // parameterised, never concatenated
        var p = cmd.CreateParameter();
        p.ParameterName = "cid";
        p.Value = (context as FuelUpDb)?.CurrentCompanyId?.ToString() ?? "";
        cmd.Parameters.Add(p);
        return cmd;
    }
}

/// <summary>
/// Database-level protection, applied idempotently at start-up with the owner (migration) connection:
///   • row-level security on every table that has company_id (FORCE, so even the table owner is filtered);
///   • append-only audit log and meter ledger (UPDATE / DELETE / TRUNCATE raise an error, for every role except superusers);
///   • least privilege for the runtime role: no DDL, no DELETE except where the app needs it, no UPDATE on append-only tables.
/// </summary>
public static class DbHardening
{
    // Tables reached before the company is known (sign-in, token refresh). Protected by EF filters and code instead.
    private static readonly string[] PreTenantTables = ["users", "user_sessions", "refresh_tokens"];
    private static readonly string[] AppendOnly = ["audit", "meter_readings", "part_movements", "fleet_document_versions", "document_alerts"];
    private static readonly string[] AppDeletes = ["user_permissions"];

    public static async Task ApplyAsync(FuelUpDb db, string? appRole, CancellationToken ct = default)
    {
        if (!db.IsPostgres) return;
        var exempt = string.Join(",", PreTenantTables.Select(t => $"'{t}'"));
        var sql = $$"""
        DO $$
        DECLARE t text;
        BEGIN
          FOR t IN
            SELECT c.table_name FROM information_schema.columns c
            JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name
            WHERE c.table_schema = 'public' AND c.column_name = 'company_id' AND tb.table_type = 'BASE TABLE'
              AND c.table_name NOT IN ({{exempt}})
          LOOP
            EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
            EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
            EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON %I', t);
            EXECUTE format($p$CREATE POLICY tenant_isolation ON %I
                USING (company_id IS NOT DISTINCT FROM nullif(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id IS NOT DISTINCT FROM nullif(current_setting('app.company_id', true), '')::uuid)$p$, t);
          END LOOP;
        END $$;

        CREATE OR REPLACE FUNCTION fu_append_only() RETURNS trigger LANGUAGE plpgsql AS $f$
        BEGIN
          RAISE EXCEPTION 'table % is append-only', TG_TABLE_NAME USING ERRCODE = '42501';
        END $f$;
        """;
        foreach (var t in AppendOnly)
            sql += $"""

            DROP TRIGGER IF EXISTS append_only ON {t};
            CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON {t} FOR EACH ROW EXECUTE FUNCTION fu_append_only();
            DROP TRIGGER IF EXISTS append_only_truncate ON {t};
            CREATE TRIGGER append_only_truncate BEFORE TRUNCATE ON {t} FOR EACH STATEMENT EXECUTE FUNCTION fu_append_only();
            """;
        await db.Database.ExecuteSqlRawAsync(sql, ct);

        if (string.IsNullOrWhiteSpace(appRole)) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(appRole, "^[a-z_][a-z0-9_]{0,62}$")) throw new InvalidOperationException("Invalid Database:AppRole");
        var exists = await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_roles WHERE rolname = {0}", appRole).AnyAsync(ct);
        if (!exists) return;
        var grants = $"""
            GRANT USAGE ON SCHEMA public TO {appRole};
            REVOKE CREATE ON SCHEMA public FROM {appRole};
            GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO {appRole};
            REVOKE DELETE, TRUNCATE ON ALL TABLES IN SCHEMA public FROM {appRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {appRole};
            REVOKE ALL ON "__EFMigrationsHistory" FROM {appRole};
            GRANT SELECT ON "__EFMigrationsHistory" TO {appRole};   -- read-only: lets the readiness check confirm migrations
            {string.Join("\n", AppendOnly.Select(t => $"REVOKE UPDATE ON {t} FROM {appRole};"))}
            {string.Join("\n", AppDeletes.Select(t => $"GRANT DELETE ON {t} TO {appRole};"))}
            """;
        await db.Database.ExecuteSqlRawAsync(grants, ct);
    }
}
