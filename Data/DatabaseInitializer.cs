using Npgsql;

namespace Nomba_Hackathon.Data;

// Applies SQL/Data.sql on startup when the ledger tables are absent, so a fresh
// database (or a recreated Docker volume) is provisioned without a manual psql
// step. Idempotent: once the tables exist the bootstrap is skipped.
public static class DatabaseInitializer
{
    public static async Task EnsureSchemaAsync(
        string connectionString, IHostEnvironment env, ILogger logger)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Core schema — only applied when transactions table is absent (first run).
        if (!await TableExistsAsync(connection, "transactions"))
        {
            var script = ResolveScript(env, "Data.sql");
            if (script is null)
            {
                logger.LogWarning("SQL/Data.sql not found; skipping schema bootstrap");
            }
            else
            {
                logger.LogInformation("Ledger tables missing; applying {Script}", script);
                await RunScriptAsync(connection, script);
                logger.LogInformation("Ledger schema created");
            }
        }
        else
        {
            logger.LogInformation("Ledger schema present; bootstrap skipped");
        }

        // Additive migrations — applied independently so they run even on existing DBs.
        await ApplyIfAbsentAsync(connection, env, logger, "virtual_accounts", "AddVirtualAccounts.sql");
        await ApplyIfAbsentAsync(connection, env, logger, "reconciliation_exceptions", "AddReconciliationExceptions.sql");
    }

    private static async Task ApplyIfAbsentAsync(
        NpgsqlConnection connection, IHostEnvironment env, ILogger logger,
        string guardTable, string scriptFile)
    {
        if (await TableExistsAsync(connection, guardTable)) return;

        var script = ResolveScript(env, scriptFile);
        if (script is null)
        {
            logger.LogWarning("SQL/{Script} not found; skipping migration", scriptFile);
            return;
        }

        logger.LogInformation("{Table} absent; applying {Script}", guardTable, script);
        await RunScriptAsync(connection, script);
        logger.LogInformation("{Table} migration applied", guardTable);
    }

    private static async Task RunScriptAsync(NpgsqlConnection connection, string scriptPath)
    {
        var sql = await File.ReadAllTextAsync(scriptPath);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@name) IS NOT NULL;";
        cmd.Parameters.AddWithValue("name", $"public.{table}");
        return (bool)(await cmd.ExecuteScalarAsync() ?? false);
    }

    private static string? ResolveScript(IHostEnvironment env, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(env.ContentRootPath, "SQL", fileName),
            Path.Combine(AppContext.BaseDirectory, "SQL", fileName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
