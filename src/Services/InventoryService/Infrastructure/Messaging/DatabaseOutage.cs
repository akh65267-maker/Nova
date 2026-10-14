using System.Net.Sockets;
using MassTransit;
using Npgsql;

namespace InventoryService.Infrastructure.Messaging;

/// <summary>
/// Telling "the database is not there right now" apart from every other failure, and the retry
/// that rides it out. The same idea, and the same code, as OrderService's DatabaseOutage (the
/// two services share no library, so it is copied; keep them in step).
///
/// The bus-level retry is about seven seconds long, which a database restart outlasts. A
/// ReserveStock or ReleaseStock parked in an _error queue then takes no effect until someone
/// replays it, by which time the order it belongs to has usually been cancelled, so replaying
/// it is the wrong thing to do. Riding the outage out avoids parking it at all, and only an
/// outage gets this: a message that fails for any other reason must not occupy a consumer for
/// minutes.
/// </summary>
public static class DatabaseOutage
{
    // Waits between attempts, in seconds: dense at first, so a database that is back after a
    // few seconds is noticed within a few seconds, then every 30 s. About 5.7 minutes in
    // total. An outage longer than that still parks the message.
    private static readonly int[] WaitsInSeconds =
        [1, 2, 3, 5, 5, 10, 10, 15, 20, 30, 30, 30, 30, 30, 30, 30, 30, 30];

    /// <summary>
    /// Register AFTER the short bus-level retry: in MassTransit the retry registered last is
    /// the outer one, so a transient failure first gets the quick retries and only if those
    /// are exhausted does it wait this out.
    /// </summary>
    public static void ConfigureRetry(IRetryConfigurator retry)
    {
        retry.Handle<Exception>(IsOutage);
        retry.Intervals(WaitsInSeconds.Select(s => TimeSpan.FromSeconds(s)).ToArray());
    }

    /// <summary>
    /// True when the database could not be reached or is not accepting work: a refused or
    /// dropped connection, a timeout, or the server shutting down, restarting or out of
    /// connections. Serialization conflicts (40001) and deadlocks (40P01) are deliberately
    /// not included: the short retry already resolves those in milliseconds.
    /// </summary>
    public static bool IsOutage(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PostgresException pg:
                    return IsOutageSqlState(pg.SqlState);
                case NpgsqlException npgsql when npgsql.IsTransient:
                    return true;
                case IOException or SocketException or TimeoutException:
                    return true;
            }
        }

        return false;
    }

    // 08 connection exception, 53 insufficient resources (53300: too many connections),
    // 57P01-03 admin/crash shutdown and "cannot connect now" (the recovery mode seen after a
    // crash), 58 system error.
    private static bool IsOutageSqlState(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal)
        || sqlState.StartsWith("53", StringComparison.Ordinal)
        || sqlState.StartsWith("58", StringComparison.Ordinal)
        || sqlState is "57P01" or "57P02" or "57P03";
}
