using System.Net.Sockets;
using Npgsql;
using OrderService.Infrastructure.Messaging;

namespace OrderService.UnitTests.Infrastructure;

public class DatabaseOutageTests
{
    private static PostgresException Postgres(string sqlState) =>
        new("message", "ERROR", "ERROR", sqlState);

    [Fact]
    public void ARefusedConnection_WrappedTheWayEntityFrameworkWrapsIt_IsAnOutage()
    {
        var refused = new NpgsqlException("Failed to connect", new SocketException(111));
        var wrapped = new InvalidOperationException("An exception has been raised that is likely due to a transient failure.", refused);

        Assert.True(DatabaseOutage.IsOutage(wrapped));
    }

    [Theory]
    [InlineData("57P01")] // terminating connection due to administrator command
    [InlineData("57P03")] // the database system is in recovery mode
    [InlineData("53300")] // too many clients already
    [InlineData("08006")] // connection failure
    [InlineData("58030")] // I/O error
    public void ADatabaseThatIsShuttingDownOrOutOfConnections_IsAnOutage(string sqlState)
    {
        Assert.True(DatabaseOutage.IsOutage(Postgres(sqlState)));
    }

    [Fact]
    public void ADroppedConnection_IsAnOutage()
    {
        Assert.True(DatabaseOutage.IsOutage(new EndOfStreamException("Attempted to read past the end of the stream.")));
    }

    [Fact]
    public void ATimeout_IsAnOutage()
    {
        Assert.True(DatabaseOutage.IsOutage(new TimeoutException()));
    }

    [Theory]
    [InlineData("40001")] // serialization failure: the short retry resolves these in milliseconds
    [InlineData("40P01")] // deadlock
    [InlineData("23505")] // unique violation: retrying cannot help
    [InlineData("42P01")] // undefined table
    public void AFailureThatIsNotTheDatabaseBeingGone_IsNotAnOutage(string sqlState)
    {
        Assert.False(DatabaseOutage.IsOutage(Postgres(sqlState)));
    }

    [Fact]
    public void AnOutageBehindAWrapper_IsFound()
    {
        var wrapped = new Exception("outer", new InvalidOperationException("middle", Postgres("57P03")));

        Assert.True(DatabaseOutage.IsOutage(wrapped));
    }

    [Theory]
    [MemberData(nameof(OrdinaryFailures))]
    public void AnOrdinaryFailure_MustNotOccupyAConsumerForMinutes(Exception exception)
    {
        Assert.False(DatabaseOutage.IsOutage(exception));
    }

    public static TheoryData<Exception> OrdinaryFailures => new()
    {
        new InvalidOperationException("Cannot confirm an order in status 'Cancelled'."),
        new ArgumentException("An order must have at least one item."),
        new NullReferenceException(),
    };
}
