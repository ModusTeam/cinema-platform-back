using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Cinema.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Services;

// A session lock spans the individually committed journal writes, never a retried
// transaction containing remote side effects. Process death releases the lock.
internal sealed class CheckoutOrderLock(IApplicationDbContext context, long key, bool relational) : IAsyncDisposable
{
    private readonly CancellationTokenSource _connectionLost = new();
    public CancellationToken ConnectionLost => _connectionLost.Token;
    public bool IsAcquired { get; private set; }

    private void ConnectionStateChanged(object? sender, StateChangeEventArgs args)
    {
        if (args.CurrentState != ConnectionState.Open)
            _connectionLost.Cancel();
    }

    public async Task EnsureHeldAsync(CancellationToken ct)
    {
        ConnectionLost.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        if (!relational) return;
        // Probe the original connection directly, without EF reconnect/retry.
        await using DbCommand command = Command("SELECT 1");
        await command.ExecuteScalarAsync(ct);
        ConnectionLost.ThrowIfCancellationRequested();
    }

    public static async Task<CheckoutOrderLock> AcquireAsync(IApplicationDbContext context, Guid orderId, CancellationToken ct)
    {
        bool relational = context.Database.IsRelational();
        long key = BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(orderId.ToByteArray()));
        CheckoutOrderLock handle = new(context, key, relational);
        if (!relational)
        {
            handle.IsAcquired = true;
            return handle;
        }
        await context.Database.OpenConnectionAsync(ct);
        context.Database.GetDbConnection().StateChange += handle.ConnectionStateChanged;
        try
        {
            await using DbCommand command = handle.Command("SELECT pg_try_advisory_lock(@key)");
            handle.IsAcquired = await command.ExecuteScalarAsync(ct) is true;
            return handle;
        }
        catch
        {
            context.Database.GetDbConnection().StateChange -= handle.ConnectionStateChanged;
            await context.Database.CloseConnectionAsync();
            handle._connectionLost.Dispose();
            throw;
        }
    }

    private DbCommand Command(string sql)
    {
        DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        return command;
    }

    public async ValueTask DisposeAsync()
    {
        if (!relational)
        {
            _connectionLost.Dispose();
            return;
        }
        context.Database.GetDbConnection().StateChange -= ConnectionStateChanged;
        try
        {
            if (IsAcquired && !ConnectionLost.IsCancellationRequested)
            {
                await using DbCommand command = Command("SELECT pg_advisory_unlock(@key)");
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
            _connectionLost.Dispose();
        }
    }
}
