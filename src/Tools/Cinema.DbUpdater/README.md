# Cinema.DbUpdater

Standalone local tool for applying the existing `ApplicationDbContext` migrations. It does not seed data or start API services.

Set `ConnectionStrings:DefaultConnection` for the database you intend to update. For local development, use an environment variable or user secrets:

```powershell
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=your_disposable_db;Username=postgres;Password=..."
```

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=your_disposable_db;Username=postgres;Password=..." --project src/Tools/Cinema.DbUpdater
```

`appsettings.json`, `appsettings.{DOTNET_ENVIRONMENT}.json`, user secrets, and environment variables are loaded in that order. Environment variables take precedence. The committed settings contain no connection string.

Run from the repository root:

```powershell
dotnet run --project src/Tools/Cinema.DbUpdater
dotnet run --project src/Tools/Cinema.DbUpdater -- --check
```

In Visual Studio, set **Cinema.DbUpdater** as the startup project and select **Development** or **Development (check)**, then use F5 or Ctrl+F5. Configure the connection string before launching.

Normal mode lists pending migrations and calls `MigrateAsync` only when needed. `--check` reads migration history without applying migrations. Exit codes: `0` means success or up to date, `1` means pending migrations in check mode, and `2` means invalid arguments, missing configuration, cancellation, or failure.
