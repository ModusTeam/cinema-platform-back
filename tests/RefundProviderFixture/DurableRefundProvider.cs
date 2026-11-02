using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Npgsql;
namespace Cinema.RefundProviderFixture;

// TEST ONLY: PostgreSQL stands in for a provider's independently durable store.
public sealed class DurableRefundProvider(string connection, RefundOutcomeKind mode = RefundOutcomeKind.Succeeded, bool loseResponse = false) : IRefundProvider
{
    public bool SupportsStableIdempotency => true;
    public bool SupportsAuthoritativeLookup => true;
    public int Submits { get; private set; }
    public int Lookups { get; private set; }
    public static async Task InitializeAsync(string connection, CancellationToken ct)
    {
        NpgsqlConnectionStringBuilder target = new(connection);
        if (target.Host != "127.0.0.1" && target.Host != "localhost" || target.Database?.StartsWith("cinema_orders_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Disposable provider fixture required");
        await using NpgsqlConnection db = new(connection); await db.OpenAsync(ct);
        await using NpgsqlCommand command = new("CREATE TABLE IF NOT EXISTS refund_mock_outcomes (provider_key text PRIMARY KEY, operation_id uuid UNIQUE NOT NULL, request text NOT NULL, outcome text NOT NULL)", db);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<RefundProviderOutcome> SubmitAsync(RefundProviderRequest request, CancellationToken ct)
    {
        Submits++;
        if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.Amount <= 0 || string.IsNullOrWhiteSpace(request.ChargeReference))
            throw new ArgumentException("Invalid fixture request");
        RefundProviderOutcome candidate = new(mode, mode == RefundOutcomeKind.Succeeded ? "mock_refund_" + request.OperationId.ToString("N") : null, "MOCK_" + mode);
        await using NpgsqlConnection db = new(connection); await db.OpenAsync(ct);
        // Fails immediately if the executor incorrectly holds its operation row lock during provider I/O.
        await using (NpgsqlCommand probe = new("SELECT id FROM refund_operations WHERE id=@id FOR UPDATE NOWAIT", db))
        { probe.Parameters.AddWithValue("id", request.OperationId); await probe.ExecuteNonQueryAsync(ct); }
        await using NpgsqlCommand insert = new("INSERT INTO refund_mock_outcomes VALUES(@key,@id,@request,@outcome) ON CONFLICT DO NOTHING", db);
        insert.Parameters.AddWithValue("key", request.IdempotencyKey); insert.Parameters.AddWithValue("id", request.OperationId);
        insert.Parameters.AddWithValue("request", JsonSerializer.Serialize(request)); insert.Parameters.AddWithValue("outcome", JsonSerializer.Serialize(candidate));
        await insert.ExecuteNonQueryAsync(ct);
        RefundLookup stored = await LookupAsync(request, ct);
        if (loseResponse) throw new IOException("Injected provider success with lost response");
        return stored.Outcome ?? throw new InvalidOperationException("Provider result missing");
    }
    public async Task<RefundLookup> LookupAsync(RefundProviderRequest request, CancellationToken ct)
    {
        Lookups++;
        await using NpgsqlConnection db = new(connection); await db.OpenAsync(ct);
        await using NpgsqlCommand query = new("SELECT request,outcome FROM refund_mock_outcomes WHERE provider_key=@key OR operation_id=@id", db);
        query.Parameters.AddWithValue("key", request.IdempotencyKey); query.Parameters.AddWithValue("id", request.OperationId);
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(true, null);
        if (reader.GetString(0) != JsonSerializer.Serialize(request)) throw new InvalidOperationException("Provider key or operation payload conflict");
        return new(false, JsonSerializer.Deserialize<RefundProviderOutcome>(reader.GetString(1)));
    }
}
