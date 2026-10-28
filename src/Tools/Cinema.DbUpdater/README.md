# Cinema.DbUpdater

Standalone .NET 10 console tool for applying the existing `ApplicationDbContext` migrations during local development. It references `Cinema.Infrastructure` for the DbContext, PostgreSQL/pgvector options, and migration assembly. It does not start the API or its workers, messaging, caching, or SignalR services.

## Configuration

Set `ConnectionStrings:DefaultConnection` for the database you intend to update. For local development, use an environment variable or user secrets:

```powershell
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=your_disposable_db;Username=postgres;Password=..."
```

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=your_disposable_db;Username=postgres;Password=..." --project src/Tools/Cinema.DbUpdater
```

`appsettings.json`, `appsettings.{DOTNET_ENVIRONMENT}.json`, and environment variables are loaded in that order. User secrets are loaded in Development, before environment variables. Environment variables take precedence. The updater reads its own settings in its project directory, whether launched from Visual Studio, the repository root, or the updater directory. It does not read the API's settings. The committed updater settings contain no connection string. Specify the target Host and Database explicitly. PostgreSQL must support pgvector for the existing migrations.

## Run

Run from the repository root:

```powershell
dotnet run --project src/Tools/Cinema.DbUpdater
dotnet run --project src/Tools/Cinema.DbUpdater -- --check
dotnet run --project src/Tools/Cinema.DbUpdater -- --seed
dotnet run --project src/Tools/Cinema.DbUpdater -- --help
```

From the updater directory, use `dotnet run`, `dotnet run -- --check`, `dotnet run -- --seed`, or `dotnet run -- --help`.

In Visual Studio, set **Cinema.DbUpdater** as the startup project and select **Development**, **Development (check)**, or **Development (seed)**, then use F5 or Ctrl+F5. Configure the connection string before launching.

Normal mode lists pending migrations and calls `MigrateAsync` only when needed. On the first run against an empty database, it applies all existing migrations. On later runs with no pending migrations, it reports that the database is up to date. `--check` reads migration history without applying migrations. `--seed` applies pending migrations first, then runs updater seed operations. No updater seed operations are currently configured. The existing API seed routines create an administrator or demonstration cinema data and are intentionally not run by this tool. Flags cannot be combined.

Exit codes: `0` means success or up to date, `1` means pending migrations in check mode, and `2` means invalid arguments, configuration problems, cancellation, or database failure. The tool logs the environment, migration counts, pending migration names, and the stage of any failure. It never prints the connection string.

## Data safety and future work

The updater does not delete or recreate the database, seed sample data, or perform a rollback. Repeated runs preserve existing rows when no migration is pending. **A destructive EF migration can still delete or change existing data** when applied, so review new migrations and back up important databases before running them. This tool executes migrations as written.

`Cinema.DbUpdaterScripts` is deferred until there are real SQL scripts to support.

## Restage an authoritative customer fact

```powershell
dotnet run --project src/Tools/Cinema.DbUpdater -- --restage-customer-fact <eventId>
```

This separate mode does **not** check/apply migrations or run seeds. Apply the source schema through the normal approved migration process first. It reads one immutable `customer_fact_revisions` snapshot and stages that original v1 envelope and MessageId through the scoped EF Bus Outbox. User state, source revision, timestamps and history remain unchanged. It creates no legacy DOB bonus message. Missing evidence/schema returns exit code 2; it never invents a predecessor or a new revision.

Use the intended database connection and the **same** `RabbitMq:Host`, `RabbitMq:Username`, `RabbitMq:Password` and `RabbitMq:VirtualHost` settings as the API delivery host (`RabbitMq__Host`, etc. for environment variables). The destination is the existing `loyalty_ticket_purchased` queue. The tool does not start RabbitMQ delivery, Redis, SMTP or API workers; the running API's existing durable delivery service sends the new outbox record when the broker is available. With no RabbitMQ settings, the isolated local default is localhost with guest credentials; supply explicit matching settings outside that setup.

Repeated restaging intentionally permits duplicate delivery with the same eventId/revision; the passive consumer deduplicates it. It does not reset quarantine budgets or unblock contradictory history. Diagnose missing/blocked/conflicting facts before replay. Never use this tool against a shared/production database without explicit operational approval. Keep credentials and DOB payloads out of command output and logs.
