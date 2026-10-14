using System.Net.Sockets;
using MassTransit;
using Npgsql;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Telling "the database is not there right now" apart from every other failure, and the retry
/// that rides it out.
///
/// The bus-level retry is a couple of seconds long: it exists for the momentary
/// serialization conflicts between concurrent saga messages. That is far too short for a
/// database restart. Once it gave up, the message was parked in an _error queue that nothing
/// reads, so an order never got its saga, or a stock response was never counted and a
/// reservation was lost (measured: 3,758 parked messages, 1,604 stranded orders, ~285 units
/// per product after a 45 s outage). So an outage gets its own, much longer retry, and only an
/// outage: a message that fails for any other reason must not occupy a consumer for minutes.
/// </summary>
public static class DatabaseOutage
{
    // Waits between attempts, in seconds: dense at first, so a database that is back after a
    // few seconds is noticed within a few seconds, then every 30 s. About 5.7 minutes in
    // total. An outage longer than that still parks the message; deploy/scripts/replay-errors.sh
    // moves those back once the database is healthy.
    private static readonly int[] WaitsInSeconds =
        [1, 2, 3, 5, 5, 10, 10, 15, 20, 30, 30, 30, 30, 30, 30, 30, 30, 30];

    /// <summary>
    /// Register AFTER the short bus-level retry: in MassTransit the retry registered last is
    /// the outer one, so a transient failure first gets the quick retries and only if those
    /// are exhausted does it wait this out. (Registered the other way round it silently did
    /// nothing: the message was parked after the short retry, as before. The integration test
    /// Checkout_WhenTheOrderDatabaseIsDownForSomeSeconds_StillCompletes guards the order.)
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
