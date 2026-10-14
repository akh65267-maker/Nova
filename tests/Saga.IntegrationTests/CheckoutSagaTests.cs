extern alias InventoryAlias;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Contracts.Commands;
using Contracts.Common;
using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OrderService.Application.Catalog;
using OrderService.Application.Orders;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

using InventoryDbContext = InventoryAlias::InventoryService.Infrastructure.Persistence.InventoryDbContext;
using InventoryItem = InventoryAlias::InventoryService.Domain.InventoryItem;
using IInventoryRepository = InventoryAlias::InventoryService.Domain.IInventoryRepository;
using InventoryRepository = InventoryAlias::InventoryService.Infrastructure.Persistence.InventoryRepository;
using ReserveStockConsumer = InventoryAlias::InventoryService.Application.Consumers.ReserveStockConsumer;
using ReleaseStockConsumer = InventoryAlias::InventoryService.Application.Consumers.ReleaseStockConsumer;
using InventoryOutbox = InventoryAlias::InventoryService.Infrastructure.Messaging.InventoryOutbox;
using InventoryItemsService = InventoryAlias::InventoryService.Application.InventoryItemsService;

namespace Saga.IntegrationTests;

/// <summary>
/// Exercises the real checkout saga end to end: a real Postgres per service,
/// a real RabbitMQ, OrderService hosted via WebApplicationFactory (its HTTP
/// API + saga), and InventoryService's consumers hosted directly (not as a
/// web app, since this test never calls its HTTP endpoints — stock is
/// seeded straight into its database instead).
///
/// The two Program classes (OrderService's and InventoryService's) would
/// collide by name if both assemblies were referenced normally, since
/// top-level statements generate an unqualified `Program` in each assembly.
/// InventoryService is referenced under the `InventoryAlias` extern alias
/// to avoid that; only its non-Program types are used here anyway.
/// </summary>
public class CheckoutSagaTests : IAsyncLifetime
{
    private const string JwtKey = "dev-only-insecure-signing-key-change-me-32bytes";
    private const string JwtIssuer = "TaskManager";
    private const string JwtAudience = "TaskManager";

    // Using the same image tags as docker-compose.yml (postgres:17, rabbitmq:3-management)
    // rather than -alpine variants: the alpine RabbitMQ tag hit an "exec format error"
    // (architecture mismatch) on this machine, while these tags are already proven to work.
    // A fixed host port, chosen once, so the order database can be stopped and started again
    // (the outage test) and still be found at the same address by the running OrderService.
    private readonly PostgreSqlContainer _orderDb = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithPortBinding(FreeTcpPort(), 5432)
        .Build();

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private readonly PostgreSqlContainer _inventoryDb = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder().WithImage("rabbitmq:3-management").Build();

    private WebApplicationFactory<Program>? _orderFactory;

    // The products CatalogService "has" and their prices. OrderService checks every order against
    // the catalog, so a test that orders something has to put it here first (CreateOrderAsync does).
    private readonly ConcurrentDictionary<Guid, decimal> _catalogPrices = new();
    private IHost? _inventoryHost;
    private Func<IHost> _createInventoryHost = default!;
    private HttpClient _orderClient = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_orderDb.StartAsync(), _inventoryDb.StartAsync(), _rabbitMq.StartAsync());

        var rabbitUri = new Uri(_rabbitMq.GetConnectionString());

        // Testcontainers.RabbitMq generates its own username/password rather
        // than using "guest"/"guest" - read the real credentials back out of
        // the connection string instead of hardcoding a default that doesn't
        // match, which otherwise causes a persistent (not transient)
        // ACCESS_REFUSED no matter how long you wait for the container.
        var rabbitUserInfo = rabbitUri.UserInfo.Split(':', 2);
        var rabbitUsername = Uri.UnescapeDataString(rabbitUserInfo[0]);
        var rabbitPassword = Uri.UnescapeDataString(rabbitUserInfo[1]);

        // RabbitMQ's TCP port (5672) becomes reachable before its auth backend
        // and exchange infrastructure are fully initialized. On cold CI runners
        // with the non-alpine image this takes noticeably longer than locally;
        // 15 s is enough headroom without relying on the management port (15672),
        // which RabbitMqBuilder does not map by default.
        await Task.Delay(TimeSpan.FromSeconds(15));

        // Migrate via a standalone DbContext, built directly from a connection
        // string rather than resolved from _orderFactory.Services. Touching
        // WebApplicationFactory's service provider at all (including just for
        // a migration scope) starts the whole ASP.NET host and its background
        // services - the MassTransit bus and its outbox-delivery poller start
        // immediately and can hit "relation OutboxState does not exist" before
        // migrations (which would otherwise run right after) ever get a
        // chance to create it. This was a real, reproducible bug, not just a
        // slow environment: one hosted service's outbox poll would fail on
        // its very first tick and never successfully deliver that order's
        // OrderSubmitted event afterward.
        var orderDbOptions = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(_orderDb.GetConnectionString())
            .Options;
        await using (var migrationContext = new OrderDbContext(orderDbOptions))
        {
            await migrationContext.Database.MigrateAsync();
        }

        _orderFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // UseSetting (not ConfigureAppConfiguration+AddInMemoryCollection):
            // Program.cs reads builder.Configuration.GetConnectionString(...)
            // immediately after WebApplication.CreateBuilder(args), before a
            // later-added ConfigureAppConfiguration source is guaranteed to be
            // layered in yet. UseSetting writes directly into the same
            // configuration WebApplicationFactory hands to CreateBuilder.
            builder.UseSetting("ConnectionStrings:OrderDatabase", _orderDb.GetConnectionString());
            builder.UseSetting("RabbitMq:Host", rabbitUri.Host);
            builder.UseSetting("RabbitMq:Port", rabbitUri.Port.ToString());
            builder.UseSetting("RabbitMq:Username", rabbitUsername);
            builder.UseSetting("RabbitMq:Password", rabbitPassword);

            // Required at startup, but never dialled: the catalog is replaced below.
            builder.UseSetting("Catalog:BaseUrl", "http://catalog.invalid");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IProductPriceProvider>(new InMemoryCatalog(_catalogPrices)));

            // The production default is minutes, which no test can wait for.
            // 20s is still ~20x a healthy checkout - both of the other tests
            // resolve in well under a second - so it is long enough not to
            // cancel a merely slow one on a cold runner, while keeping the
            // timeout test to roughly its own duration.
            builder.UseSetting("Checkout:Timeout", "00:00:40");
            builder.UseSetting("Checkout:SweepInterval", "00:00:01");

            // How long a timed-out saga survives after its deadline. It has to
            // outlast bringing InventoryService back up in the late-reservation
            // test (a few seconds, more on a cold runner) with real margin, yet
            // be short enough that the same test can watch the row get purged.
            builder.UseSetting("Checkout:TimedOutRetention", "00:00:45");

            builder.ConfigureServices(WaitForBusTopology);
        });
        _orderClient = _orderFactory.CreateClient();

        // A factory rather than a single instance: the late-reservation test stops
        // InventoryService and later brings up a fresh one against the same broker
        // and database, which is what a real restart looks like. The queued
        // ReserveStock survives in RabbitMQ in between.
        _createInventoryHost = () => Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(_inventoryDb.GetConnectionString()));
                services.AddScoped<IInventoryRepository, InventoryRepository>();

                WaitForBusTopology(services);

                services.AddMassTransit(x =>
                {
                    x.AddEntityFrameworkOutbox<InventoryDbContext>(InventoryOutbox.Configure);
                    x.AddConsumer<ReserveStockConsumer>();
                    x.AddConsumer<ReleaseStockConsumer>();
                    x.AddConsumer<FaultRecorder>();

                    x.UsingRabbitMq((context, cfg) =>
                    {
                        cfg.Host(rabbitUri.Host, (ushort)rabbitUri.Port, "/", h =>
                        {
                            h.Username(rabbitUsername);
                            h.Password(rabbitPassword);
                        });

                        cfg.ReceiveEndpoint("ReserveStock", e =>
                        {
                            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
                            e.ConfigureConsumer<ReserveStockConsumer>(context);
                        });

                        cfg.ReceiveEndpoint("ReleaseStock", e =>
                        {
                            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
                            e.ConfigureConsumer<ReleaseStockConsumer>(context);
                        });

                        // Test-only. When a message exhausts UseMessageRetry,
                        // MassTransit parks it in an _error queue and publishes
                        // a Fault<T>. Nothing in this system consumes either
                        // (see docs/TODO.md), so such a failure is completely
                        // silent and the saga simply never progresses - which
                        // surfaces only as a timeout with no cause. Binding the
                        // fault exchanges here turns that into a readable error.
                        cfg.ReceiveEndpoint("saga-test-fault-recorder", e =>
                        {
                            e.ConfigureConsumer<FaultRecorder>(context);
                        });
                    });
                });
            })
            .Build();
        _inventoryHost = _createInventoryHost();

        using (var scope = _inventoryHost.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
        }

        await _inventoryHost.StartAsync();
    }

    /// <summary>
    /// Blocks host startup until MassTransit has finished declaring its
    /// exchanges, queues and bindings.
    ///
    /// MassTransitHostOptions.WaitUntilStarted defaults to false, so
    /// StartAsync returns as soon as the bus has been *asked* to start. The
    /// topology is still being declared in the background. A message
    /// published in that window goes to an exchange that has no queue bound
    /// to it yet, and RabbitMQ silently discards it: no consumer runs, no
    /// fault is produced, and the outbox considers it delivered. The saga
    /// then never starts and the order sits in Pending until the test times
    /// out with nothing to show for it.
    ///
    /// A fast machine usually wins that race, which is why this only ever
    /// failed on cold CI runners. Waiting removes the race instead of
    /// widening the window it has to win in.
    /// </summary>
    private static void WaitForBusTopology(IServiceCollection services) =>
        services.AddOptions<MassTransitHostOptions>().Configure(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(60);
        });

    public async Task DisposeAsync()
    {
        if (_inventoryHost is not null)
        {
            await _inventoryHost.StopAsync();
            _inventoryHost.Dispose();
        }

        _orderFactory?.Dispose();

        await Task.WhenAll(_orderDb.DisposeAsync().AsTask(), _inventoryDb.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Checkout_WithSufficientStock_ConfirmsOrderAndDecrementsInventory()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());

        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));

        var order = await WaitForResolutionAsync(orderId);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.NotNull(order.ResolvedAtUtc);
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));
    }

    [Fact]
    public async Task Checkout_WithInsufficientStock_CancelsOrderAndReleasesReservedItems()
    {
        var plentifulProductId = Guid.NewGuid();
        var scarceProductId = Guid.NewGuid();
        await SeedInventoryAsync(plentifulProductId, quantityAvailable: 10);
        await SeedInventoryAsync(scarceProductId, quantityAvailable: 1);
        AuthenticateAs(Guid.NewGuid());

        var orderId = await CreateOrderAsync(
            new OrderItemRequest(plentifulProductId, 3, 9.99m),
            new OrderItemRequest(scarceProductId, 5, 4.99m));

        var order = await WaitForResolutionAsync(orderId);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.NotNull(order.ResolvedAtUtc);

        // The plentiful item's reservation must have been released once the
        // scarce item failed — this is the compensating transaction actually
        // firing, not just the order being marked cancelled.
        Assert.Equal(10, await GetInventoryQuantityAsync(plentifulProductId));
        Assert.Equal(1, await GetInventoryQuantityAsync(scarceProductId));
    }

    /// <summary>
    /// The stall the timeout exists for: a ReserveStock command that is never
    /// answered. Stopping InventoryService leaves the command sitting unconsumed
    /// in its queue, which is indistinguishable to the saga from a response that
    /// was dropped or parked in an _error queue after exhausting retries.
    ///
    /// Before the timeout existed this order stayed Pending forever, and any
    /// stock reserved for it stayed reserved forever with it.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenStockResponseNeverArrives_TimesOutAndCancelsOrder()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());

        await _inventoryHost!.StopAsync();

        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));

        var order = await WaitForResolutionAsync(orderId);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Contains("Timed out", order.CancellationReason!);

        // Nothing ever got as far as reserving, so the compensation had nothing
        // to release and the quantity must be untouched. Asserting this is what
        // separates a timeout that cleaned up correctly from one that fired a
        // spurious ReleaseStock for stock that was never taken.
        Assert.Equal(10, await GetInventoryQuantityAsync(productId));
    }

    /// <summary>
    /// The case the timeout alone got wrong. InventoryService is down, so the
    /// checkout times out and is cancelled with nothing to release - but the
    /// ReserveStock is still queued. When InventoryService comes back it consumes
    /// that command and takes stock for an order that no longer exists; the
    /// StockReserved that follows used to find no saga and vanish, leaving the
    /// stock lost. The saga now stays in TimedOut for a retention window so that
    /// reservation can be released, then is purged.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenReservationCompletesAfterTimeout_ReleasesItAndThenPurgesTheSaga()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());

        await _inventoryHost!.StopAsync();

        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        var cancelled = await WaitForResolutionAsync(orderId);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);

        // The row must survive the timeout, parked rather than finalized -
        // otherwise there is nothing for the late reservation to be released by.
        Assert.Equal("TimedOut", await SagaStateAsync(orderId));

        _inventoryHost.Dispose();
        _inventoryHost = _createInventoryHost();
        await _inventoryHost.StartAsync();

        // Two inbox rows means both messages were handled: the late ReserveStock,
        // then the ReleaseStock the saga sent back for it. Stock alone would not
        // prove it, since it reads 10 both before the reservation and after the release.
        await WaitUntilAsync(
            async () => await CountInventoryConsumedAsync() >= 2 && await GetInventoryQuantityAsync(productId) == 10,
            TimeSpan.FromSeconds(45),
            "the late ReserveStock to be consumed and released (2 inbox rows, stock back to 10)");

        Assert.Equal(OrderStatus.Cancelled, (await WaitForResolutionAsync(orderId, timeoutSeconds: 5)).Status);

        // And the tombstone is not kept forever.
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) is null,
            TimeSpan.FromSeconds(90),
            "the timed-out saga to be purged after its retention window");
    }

    /// <summary>
    /// Finalizing a checkout whose order is already resolved. Reaching FinalizeAsync twice for
    /// one order (a redelivery after the order's own write had committed) used to throw from
    /// Order.Confirm(), fault the message and leave the saga stuck in AwaitingStockReservation
    /// even though the order was done. Here the order is confirmed behind the saga's back
    /// while InventoryService is down, then the replies arrive: the saga must simply finish.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenTheOrderIsAlreadyConfirmed_FinalizesTheSagaInsteadOfFaulting()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await _inventoryHost!.StopAsync();
        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(30), "the saga to start waiting for stock");

        await SetOrderStatusAsync(orderId, "Confirmed", reason: null);

        _inventoryHost.Dispose();
        _inventoryHost = _createInventoryHost();
        await _inventoryHost.StartAsync();

        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) is null || !_faults.IsEmpty,
            TimeSpan.FromSeconds(60), "the saga to finalize (or fault)");

        Assert.True(_faults.IsEmpty, $"finalizing faulted: {_faults.FirstOrDefault()}");
        Assert.Null(await SagaStateAsync(orderId));
        Assert.Equal(OrderStatus.Confirmed, (await WaitForResolutionAsync(orderId, timeoutSeconds: 5)).Status);
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));
    }

    /// <summary>
    /// The same for a cancellation: the order was cancelled elsewhere, then the saga
    /// finalizes with a failure. It must neither fault, nor overwrite the original reason,
    /// nor publish its releases a second time - those belong to the finalize that actually
    /// cancelled the order, and publishing them again would inflate stock.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenTheOrderIsAlreadyCancelled_FinalizesWithoutFaultingOrReleasingAgain()
    {
        var plentiful = Guid.NewGuid();
        var scarce = Guid.NewGuid();
        await SeedInventoryAsync(plentiful, quantityAvailable: 10);
        await SeedInventoryAsync(scarce, quantityAvailable: 1);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await _inventoryHost!.StopAsync();
        var orderId = await CreateOrderAsync(
            new OrderItemRequest(plentiful, 3, 9.99m),
            new OrderItemRequest(scarce, 5, 4.99m));
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(30), "the saga to start waiting for stock");

        await SetOrderStatusAsync(orderId, "Cancelled", reason: "Cancelled before the saga finished.");

        _inventoryHost.Dispose();
        _inventoryHost = _createInventoryHost();
        await _inventoryHost.StartAsync();

        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) is null || !_faults.IsEmpty,
            TimeSpan.FromSeconds(60), "the saga to finalize (or fault)");

        Assert.True(_faults.IsEmpty, $"finalizing faulted: {_faults.FirstOrDefault()}");
        Assert.Null(await SagaStateAsync(orderId));

        var order = await WaitForResolutionAsync(orderId, timeoutSeconds: 5);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("Cancelled before the saga finished.", order.CancellationReason);

        // The plentiful item was reserved (10 - 3) and nothing released it again.
        Assert.Equal(7, await GetInventoryQuantityAsync(plentiful));
    }

    /// <summary>
    /// What replaying an _error queue can do. An OrderSubmitted that was parked during an
    /// outage may arrive for an order that has since been resolved (its other copy got
    /// through, say). A new saga for it would reserve the stock a second time, and nothing
    /// would ever release it. The saga must refuse to start for an order that is not Pending.
    /// </summary>
    [Fact]
    public async Task OrderSubmitted_ForAnOrderThatIsAlreadyConfirmed_DoesNotReserveStockAgain()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        Assert.Equal(OrderStatus.Confirmed, (await WaitForResolutionAsync(orderId)).Status);
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) is null,
            TimeSpan.FromSeconds(15), "the first saga to finish");
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));

        // The replay: the same order submitted again, as a new message.
        var bus = _orderFactory!.Services.GetRequiredService<IBus>();
        await bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), new List<OrderLineItem> { new(productId, 4, 9.99m) }));

        // Long enough for a saga to start, reserve and come back if it were going to.
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.True(_faults.IsEmpty, $"the replay faulted: {_faults.FirstOrDefault()}");
        Assert.Null(await SagaStateAsync(orderId));
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));
        Assert.Equal(OrderStatus.Confirmed, (await WaitForResolutionAsync(orderId, timeoutSeconds: 5)).Status);
    }

    /// <summary>
    /// The same message arriving while the saga for that order is still waiting. It must be
    /// ignored: starting over would send every ReserveStock again.
    /// </summary>
    [Fact]
    public async Task OrderSubmitted_WhileTheCheckoutIsStillInFlight_IsIgnored()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await _inventoryHost!.StopAsync();
        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(30), "the saga to start waiting for stock");

        var bus = _orderFactory!.Services.GetRequiredService<IBus>();
        await bus.Publish(new OrderSubmitted(
            orderId, Guid.NewGuid(), new List<OrderLineItem> { new(productId, 4, 9.99m) }));
        await Task.Delay(TimeSpan.FromSeconds(4));

        _inventoryHost.Dispose();
        _inventoryHost = _createInventoryHost();
        await _inventoryHost.StartAsync();

        Assert.Equal(OrderStatus.Confirmed, (await WaitForResolutionAsync(orderId)).Status);
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) is null,
            TimeSpan.FromSeconds(15), "the saga to finish");

        Assert.True(_faults.IsEmpty, $"the duplicate faulted: {_faults.FirstOrDefault()}");
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));   // reserved once, not twice
    }

    /// <summary>
    /// The incident in miniature. InventoryService answers while the order database is down:
    /// the saga cannot record StockReserved. With the old retry (about 2 s) the message was
    /// parked in an _error queue, the saga never heard, the checkout timed out and the
    /// reservation was lost. The retry now rides out an outage of a few minutes, so the same
    /// message simply succeeds once the database is back.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenTheOrderDatabaseIsDownForSomeSeconds_StillCompletes()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await _inventoryHost!.StopAsync();
        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(30), "the saga to start waiting for stock");

        // The outage begins, then InventoryService comes up and answers into it.
        await _orderDb.StopAsync();

        _inventoryHost.Dispose();
        _inventoryHost = _createInventoryHost();
        await _inventoryHost.StartAsync();

        // Long enough that a retry of about 2 s has long since given up, well inside the
        // checkout timeout, so the only thing that can save the order is the retry.
        await Task.Delay(TimeSpan.FromSeconds(8));
        await _orderDb.StartAsync();

        var order = await WaitForResolutionAsync(orderId, timeoutSeconds: 120);

        Assert.True(_faults.IsEmpty, $"a message was parked instead of retried: {_faults.FirstOrDefault()}");
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));
    }

    /// <summary>
    /// A checkout that can no longer reach its outcome must still END. Here the order was
    /// confirmed behind the saga's back (a duplicate checkout is how this happened for real:
    /// six such sagas faulted on every sweeper tick, about 12 parked messages a minute, for
    /// days), and then the checkout times out. Cancelling a confirmed order is a conflict; it
    /// used to throw, so the saga stayed in AwaitingStockReservation and the sweeper
    /// republished the timeout on every tick, each one faulting again. Now it is logged, the
    /// order is left as it is, and the saga moves on to be purged like any timed-out checkout.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenTheOrderWasConfirmedElsewhereAndTheCheckoutTimesOut_EndsInsteadOfFaultingForever()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await _inventoryHost!.StopAsync();
        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(30), "the saga to start waiting for stock");

        await SetOrderStatusAsync(orderId, "Confirmed", reason: null);

        // InventoryService stays down, so the only thing left to happen is the timeout.
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) != "AwaitingStockReservation",
            TimeSpan.FromSeconds(90), "the timed-out checkout to leave AwaitingStockReservation");

        Assert.True(_faults.IsEmpty, $"the timeout faulted: {_faults.FirstOrDefault()}");
        Assert.Equal("TimedOut", await SagaStateAsync(orderId));
        Assert.Equal(OrderStatus.Confirmed, (await WaitForResolutionAsync(orderId, timeoutSeconds: 5)).Status);
    }

    // Makes every UPDATE of an order fail, the way a constraint, a deadlock victim or a full disk
    // would, so "finalizing the order failed" can be tested without touching the code under test.
    // P0001 is an ordinary error, not a database outage, so it gets the short retry and is then
    // parked rather than waited out.
    private const string FailOrderUpdates = """
        CREATE OR REPLACE FUNCTION fail_order_update() RETURNS trigger AS $$
        BEGIN RAISE EXCEPTION 'injected failure' USING ERRCODE = 'P0001'; END;
        $$ LANGUAGE plpgsql;
        CREATE TRIGGER fail_order_update BEFORE UPDATE ON "Orders"
            FOR EACH ROW EXECUTE FUNCTION fail_order_update();
        """;

    private const string AllowOrderUpdates = "DROP TRIGGER IF EXISTS fail_order_update ON \"Orders\";";

    private async Task<OrderDto> GetOrderAsync(Guid orderId)
    {
        var response = await _orderClient.GetAsync($"/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDto>(_jsonOptions))!;
    }

    private async Task ExecuteOrderSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_orderDb.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int?> SagaResponseCountAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(_orderDb.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"ResponseCount\" FROM \"OrderSagaStates\" WHERE \"CorrelationId\" = @id", connection);
        command.Parameters.AddWithValue("id", orderId);
        return await command.ExecuteScalarAsync() as int?;
    }

    /// <summary>
    /// Does finalizing a checkout commit atomically? FinalizeAsync saves the order from inside
    /// the saga's handler, and the saga's own change (the response it just counted) is saved by
    /// the saga repository. If those were two transactions, a failure between them could leave
    /// the saga advanced and the order untouched, or the reverse. Here the order write fails: the
    /// response that triggered it must not stay counted, the saga must not move on, and the
    /// order must still be Pending, so that a retry or a replay starts from a consistent place.
    /// </summary>
    [Fact]
    public async Task Finalizing_WhenTheOrderCannotBeSaved_RollsTheSagaBackWithIt()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await _inventoryHost!.StopAsync();
        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));
        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(30), "the saga to start waiting for stock");

        await ExecuteOrderSqlAsync(FailOrderUpdates);
        try
        {
            _inventoryHost.Dispose();
            _inventoryHost = _createInventoryHost();
            await _inventoryHost.StartAsync();

            await WaitUntilAsync(
                async () => !_faults.IsEmpty,
                TimeSpan.FromSeconds(60), "the stock response to fail on the order write");

            Assert.Equal("AwaitingStockReservation", await SagaStateAsync(orderId));
            Assert.Equal(0, await SagaResponseCountAsync(orderId));
            Assert.Equal(OrderStatus.Pending, (await GetOrderAsync(orderId)).Status);
        }
        finally
        {
            await ExecuteOrderSqlAsync(AllowOrderUpdates);
        }
    }

    /// <summary>
    /// The same question for what the saga SENDS. When a checkout fails it publishes ReleaseStock
    /// for what it had reserved and cancels the order. If the order write fails, the releases
    /// must not go out: they would hand back stock for an order that is still waiting, and a
    /// retry would publish them a second time.
    /// </summary>
    [Fact]
    public async Task Finalizing_WhenTheOrderCannotBeSaved_DoesNotSendTheReleases()
    {
        var plentiful = Guid.NewGuid();
        var scarce = Guid.NewGuid();
        await SeedInventoryAsync(plentiful, quantityAvailable: 10);
        await SeedInventoryAsync(scarce, quantityAvailable: 1);
        AuthenticateAs(Guid.NewGuid());
        _faults.Clear();

        await ExecuteOrderSqlAsync(FailOrderUpdates);
        try
        {
            // The plentiful item reserves and the scarce one is refused, so the last response
            // finalizes with a failure: ReleaseStock for the plentiful one, then the order write.
            var orderId = await CreateOrderAsync(
                new OrderItemRequest(plentiful, 3, 9.99m),
                new OrderItemRequest(scarce, 5, 4.99m));

            await WaitUntilAsync(
                async () => !_faults.IsEmpty,
                TimeSpan.FromSeconds(60), "the final response to fail on the order write");

            // Give a release that should not exist time to arrive.
            await Task.Delay(TimeSpan.FromSeconds(5));

            Assert.Equal(7, await GetInventoryQuantityAsync(plentiful));   // reserved, never released
            Assert.Equal("AwaitingStockReservation", await SagaStateAsync(orderId));
        }
        finally
        {
            await ExecuteOrderSqlAsync(AllowOrderUpdates);
        }
    }

    private async Task SetOrderStatusAsync(Guid orderId, string status, string? reason)
    {
        await using var connection = new NpgsqlConnection(_orderDb.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE \"Orders\" SET \"Status\" = @status, \"CancellationReason\" = @reason WHERE \"Id\" = @id",
            connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("id", orderId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// The race behind the "first order after a restart never confirms" stall.
    /// The saga publishes ReserveStock while its own transaction is still open, and
    /// only afterwards INSERTs and commits its row. If InventoryService answers
    /// faster than that commit, StockReserved looks the saga up, finds nothing and is
    /// dropped without a fault - the checkout then hangs until the timeout.
    ///
    /// A real run only hits that window when the saga is unusually slow (a cold
    /// process), so this holds it open on purpose: a table lock that blocks the
    /// saga's INSERT (but not its SELECT ... FOR UPDATE) for a few seconds, while
    /// InventoryService is healthy and replies within milliseconds. Without the
    /// outbox on the saga endpoint the order never confirms; with it, ReserveStock
    /// is held back until the row is committed, so the reply can never arrive early.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenInventoryRepliesBeforeTheSagaCommits_StillConfirms()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());

        await using var blocker = new NpgsqlConnection(_orderDb.GetConnectionString());
        await blocker.OpenAsync();
        await using var lockTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
                         "LOCK TABLE \"OrderSagaStates\" IN SHARE ROW EXCLUSIVE MODE", blocker, lockTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));

        // Long enough for the saga to reach its blocked INSERT and, without the
        // fix, for ReserveStock to have already been sent, consumed and answered.
        await Task.Delay(TimeSpan.FromSeconds(4));
        await lockTransaction.CommitAsync();

        var order = await WaitForResolutionAsync(orderId);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));
    }

    /// <summary>
    /// Delivery is at-least-once, so a stock response can arrive twice. Without an
    /// inbox on the saga endpoint the duplicate increments ResponseCount again and a
    /// two-item order confirms after hearing about only one item - confirming an
    /// order whose second item was never reserved. The same MessageId delivered twice
    /// is exactly what a broker redelivery looks like.
    /// </summary>
    [Fact]
    public async Task Checkout_WhenAStockResponseIsRedelivered_DoesNotConfirmBeforeEveryItemHasAnswered()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await SeedInventoryAsync(first, quantityAvailable: 10);
        await SeedInventoryAsync(second, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());

        // No real replies: the test supplies the responses itself.
        await _inventoryHost!.StopAsync();

        var orderId = await CreateOrderAsync(
            new OrderItemRequest(first, 1, 1.00m),
            new OrderItemRequest(second, 1, 1.00m));

        await WaitUntilAsync(
            async () => await SagaStateAsync(orderId) == "AwaitingStockReservation",
            TimeSpan.FromSeconds(20),
            "the saga to start");

        // IBus, not IPublishEndpoint: the latter is captured by the bus outbox and
        // would wait for a SaveChanges that this test never makes.
        var bus = _orderFactory!.Services.GetRequiredService<IBus>();
        var messageId = Guid.NewGuid();
        await bus.Publish(new StockReserved(orderId, first), publish => publish.MessageId = messageId);
        await bus.Publish(new StockReserved(orderId, first), publish => publish.MessageId = messageId);

        await Task.Delay(TimeSpan.FromSeconds(4));

        // One item has answered, once. The order must still be waiting on the other.
        var response = await _orderClient.GetAsync($"/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderDto>(_jsonOptions);
        Assert.Equal(OrderStatus.Pending, order!.Status);
    }

    /// <summary>
    /// Many reservations of the same product arriving at once - what a flash sale looks
    /// like, and what a broker outage produces when its backlog is delivered together.
    /// Each ReserveStock reads the product's row, computes the new quantity and writes it
    /// back, in a transaction Postgres will not let two of them win concurrently: the loser
    /// gets 40001 "could not serialize access due to concurrent update". That is supposed
    /// to be absorbed by retry, but retries that fire on the same schedule collide again,
    /// and a message that loses every attempt is parked in an _error queue that nothing
    /// reads. Found by the load tests, where it took a broker outage to show up.
    ///
    /// Published straight to the bus (no saga), so this tests InventoryService alone.
    /// </summary>
    [Fact]
    public async Task Reservations_ForOneProduct_ArrivingTogether_AreAllApplied()
    {
        const int reservations = 40;
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 1000);
        _faults.Clear();

        var bus = _orderFactory!.Services.GetRequiredService<IBus>();
        await Task.WhenAll(Enumerable.Range(0, reservations)
            .Select(_ => bus.Publish(new ReserveStock(Guid.NewGuid(), productId, 1))));

        // Done when every unit is accounted for - or as soon as something faulted, which
        // can only mean a message exhausted its retries (about 7 s into a collision).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        int stock;
        do
        {
            await Task.Delay(500);
            stock = await GetInventoryQuantityAsync(productId);
        }
        while (stock != 1000 - reservations && _faults.IsEmpty && DateTime.UtcNow < deadline);

        Assert.True(_faults.IsEmpty, $"{_faults.Count} reservation(s) failed: {_faults.FirstOrDefault()}");
        Assert.Equal(1000 - reservations, stock);
    }

    /// <summary>
    /// A restock racing a reservation. The reservation has taken the row lock and not yet
    /// committed (the state every in-flight reservation is in). A restock that reads the
    /// quantity without taking that lock reads the old value, then overwrites the
    /// reservation's decrement once the lock frees: stock rises by the restock <i>plus</i>
    /// whatever the reservation had taken, and the order that reserved it is still confirmed.
    /// Deterministic, because the reservation is held open by hand rather than by timing luck.
    /// </summary>
    [Fact]
    public async Task Restock_WhileAReservationIsInFlight_DoesNotOverwriteIt()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);

        // The reservation: lock the row, take 4, but do not commit yet.
        using var reserveScope = _inventoryHost!.Services.CreateScope();
        var reserveDb = reserveScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await using var reservation = await reserveDb.Database.BeginTransactionAsync();
        var held = await new InventoryRepository(reserveDb).GetByProductIdForUpdateAsync(productId);
        held!.Reserve(4);
        await reserveDb.SaveChangesAsync();

        // The restock starts while that is open, on its own connection.
        using var restockScope = _inventoryHost.Services.CreateScope();
        var restockDb = restockScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var service = new InventoryItemsService(
            new InventoryRepository(restockDb), NullLogger<InventoryItemsService>.Instance);
        var restock = service.RestockAsync(productId, 5);

        // Give it time to do whatever it does before it has to wait for the lock.
        await Task.Delay(750);
        Assert.False(restock.IsCompleted, "the restock should be waiting for the reservation's row lock");

        await reservation.CommitAsync();
        await restock;

        Assert.Equal(10 - 4 + 5, await GetInventoryQuantityAsync(productId));
    }

    /// <summary>
    /// The check that makes the previous test meaningful. Removing the 40001 failures by
    /// running at READ COMMITTED is only a fix if the row lock really serializes the
    /// writers; if it did not, they would silently overwrite each other's quantity and
    /// the test above could pass while the stock was wrong. Here demand far exceeds
    /// stock, so any lost update shows as stock that is not exactly zero (never negative,
    /// never left over), and every message must still be processed without a fault.
    /// </summary>
    [Fact]
    public async Task Reservations_ExceedingStock_NeverOversell()
    {
        const int reservations = 40;
        const int stock = 10;
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: stock);
        _faults.Clear();

        // The inbox table is shared by every test in this class, so wait for it to grow by this
        // test's messages, not to reach a total that earlier tests may already have passed.
        var inboxBefore = await CountInventoryConsumedAsync();

        var bus = _orderFactory!.Services.GetRequiredService<IBus>();
        await Task.WhenAll(Enumerable.Range(0, reservations)
            .Select(_ => bus.Publish(new ReserveStock(Guid.NewGuid(), productId, 1))));

        await WaitUntilAsync(
            async () => await CountInventoryConsumedAsync() >= inboxBefore + reservations || !_faults.IsEmpty,
            TimeSpan.FromSeconds(60),
            $"all {reservations} reservations to be processed");

        Assert.True(_faults.IsEmpty, $"{_faults.Count} reservation(s) failed: {_faults.FirstOrDefault()}");
        Assert.Equal(0, await GetInventoryQuantityAsync(productId));
    }

    /// <summary>
    /// Reservations and releases for the same product run on different endpoints, so
    /// unlike the tests above they race each other even within one process. Each pair
    /// cancels out, so the stock must end exactly where it started; a lost update in
    /// either direction would leave it off.
    /// </summary>
    [Fact]
    public async Task ReservationsAndReleases_ForOneProduct_ArrivingTogether_LeaveStockUnchanged()
    {
        const int pairs = 25;
        const int stock = 500;
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: stock);
        _faults.Clear();

        var inboxBefore = await CountInventoryConsumedAsync();

        var bus = _orderFactory!.Services.GetRequiredService<IBus>();
        var messages = Enumerable.Range(0, pairs).SelectMany(_ => new Func<Task>[]
        {
            () => bus.Publish(new ReserveStock(Guid.NewGuid(), productId, 1)),
            () => bus.Publish(new ReleaseStock(Guid.NewGuid(), productId, 1)),
        });
        await Task.WhenAll(messages.Select(send => send()));

        await WaitUntilAsync(
            async () => await CountInventoryConsumedAsync() >= inboxBefore + pairs * 2 || !_faults.IsEmpty,
            TimeSpan.FromSeconds(60),
            $"all {pairs * 2} messages to be processed");

        Assert.True(_faults.IsEmpty, $"{_faults.Count} message(s) failed: {_faults.FirstOrDefault()}");
        Assert.Equal(stock, await GetInventoryQuantityAsync(productId));
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Gave up after {timeout.TotalSeconds:0}s waiting for {description}. " +
            await DescribeStalledStateAsync());
    }

    private async Task<string?> SagaStateAsync(Guid orderId)
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(_orderDb.GetConnectionString())
            .Options;
        await using var db = new OrderDbContext(options);

        return await db.Database
            .SqlQuery<string>($"SELECT \"CurrentState\" AS \"Value\" FROM \"OrderSagaStates\" WHERE \"CorrelationId\" = {orderId}")
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// How many messages InventoryService has FINISHED handling. An inbox row appears when a
    /// message is received and is only stamped Consumed when its handler is done, so counting
    /// rows would report "processed" while consumers are still queued on the stock row lock.
    /// </summary>
    private async Task<int> CountInventoryConsumedAsync()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(_inventoryDb.GetConnectionString())
            .Options;
        await using var db = new InventoryDbContext(options);

        return await db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"InboxState\" WHERE \"Consumed\" IS NOT NULL")
            .SingleAsync();
    }

    private readonly List<Guid> _seededProducts = new();

    private async Task SeedInventoryAsync(Guid productId, int quantityAvailable)
    {
        using var scope = _inventoryHost!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.Add(new InventoryItem(productId, quantityAvailable));
        await db.SaveChangesAsync();
        _seededProducts.Add(productId);
    }

    private async Task<int> GetInventoryQuantityAsync(Guid productId)
    {
        using var scope = _inventoryHost!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await db.Set<InventoryItem>().AsNoTracking().FirstAsync(x => x.ProductId == productId);
        return item.QuantityAvailable;
    }

    private void AuthenticateAs(Guid userId, string? role = null)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: role is null
                ? new[] { new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()) }
                : new[] { new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim(ClaimTypes.Role, role) },
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        var rawToken = new JwtSecurityTokenHandler().WriteToken(token);
        _orderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
    }


    private const string AdminRange = "from=2026-10-01T00:00:00Z&to=2026-10-08T00:00:00Z";

    // Every admin analytics endpoint, with the shape its answer must have.
    private static readonly (string Url, string Contains)[] AdminEndpoints =
    {
        ($"/orders/admin/summary?{AdminRange}", "\"current\""),
        ($"/orders/admin/timeseries?{AdminRange}", "\"days\""),
        ($"/orders/admin/cancellations?{AdminRange}", "["),
        ($"/orders/admin/top-products?{AdminRange}&by=revenue&limit=5", "["),
        ($"/orders/admin/pending", "["),
        ($"/orders/admin/orders?{AdminRange}&status=Pending&page=1&pageSize=10", "\"total\""),
    };

    [Fact]
    public async Task AdminAnalytics_IsForAdministratorsOnly()
    {
        _orderClient.DefaultRequestHeaders.Authorization = null;
        foreach (var (url, _) in AdminEndpoints)
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await _orderClient.GetAsync(url)).StatusCode);

        AuthenticateAs(Guid.NewGuid());
        foreach (var (url, _) in AdminEndpoints)
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _orderClient.GetAsync(url)).StatusCode);

        AuthenticateAs(Guid.NewGuid(), role: "Customer");
        foreach (var (url, _) in AdminEndpoints)
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await _orderClient.GetAsync(url)).StatusCode);

        AuthenticateAs(Guid.NewGuid(), role: "Admin");
        foreach (var (url, contains) in AdminEndpoints)
        {
            var response = await _orderClient.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}");
            Assert.Contains(contains, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AdminAnalytics_RefusesBadParametersWith400()
    {
        AuthenticateAs(Guid.NewGuid(), role: "Admin");

        foreach (var bad in new[]
        {
            "/orders/admin/summary",                                                                        // no range
            "/orders/admin/summary?from=2026-10-01T00:00:00Z",                                              // no end
            "/orders/admin/summary?from=2026-10-08T00:00:00Z&to=2026-10-01T00:00:00Z",                      // reversed
            "/orders/admin/summary?from=2024-01-01T00:00:00Z&to=2026-01-01T00:00:00Z",                      // too long
            "/orders/admin/summary?from=yesterday&to=today",                                                // not dates
            $"/orders/admin/timeseries?{AdminRange}&bucket=hour",
            $"/orders/admin/top-products?{AdminRange}&by=profit",
            $"/orders/admin/top-products?{AdminRange}&limit=100000",
            "/orders/admin/pending?limit=100000",
            "/orders/admin/pending?minAgeSeconds=-5",
            $"/orders/admin/orders?{AdminRange}&pageSize=100000",
            $"/orders/admin/orders?{AdminRange}&status=Shipped",
            $"/orders/admin/orders?{AdminRange}&page=0",
        })
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _orderClient.GetAsync(bad)).StatusCode);
        }
    }

    [Fact]
    public async Task Checkout_WithAPriceThatIsNotTheCatalogPrice_IsRefusedWith409AndCreatesNoOrder()
    {
        AuthenticateAs(Guid.NewGuid());
        var productId = Guid.NewGuid();
        _catalogPrices[productId] = 9.99m;

        // The caller claims the item costs a cent.
        var response = await _orderClient.PostAsJsonAsync(
            "/orders", new CreateOrderRequest(new[] { new OrderItemRequest(productId, 1, 0.01m) }));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("9.99", await response.Content.ReadAsStringAsync());
        Assert.Empty(await ListMyOrdersAsync());
    }

    [Fact]
    public async Task Checkout_WithAProductTheCatalogDoesNotKnow_IsRefusedWith422AndCreatesNoOrder()
    {
        AuthenticateAs(Guid.NewGuid());
        var response = await _orderClient.PostAsJsonAsync(
            "/orders", new CreateOrderRequest(new[] { new OrderItemRequest(Guid.NewGuid(), 1, 9.99m) }));

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await ListMyOrdersAsync());
    }

    private async Task<JsonElement[]> ListMyOrdersAsync()
    {
        var response = await _orderClient.GetAsync("/orders");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
    }

    // Stands in for CatalogService: prices for the products a test has registered.
    private sealed class InMemoryCatalog(ConcurrentDictionary<Guid, decimal> prices) : IProductPriceProvider
    {
        public Task<IReadOnlyDictionary<Guid, decimal>> GetPricesAsync(
            IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(
                productIds.Where(prices.ContainsKey).ToDictionary(id => id, id => prices[id]));
    }

    private async Task<Guid> CreateOrderAsync(params OrderItemRequest[] items)
    {
        foreach (var item in items)
            _catalogPrices[item.ProductId] = item.UnitPrice;

        var response = await _orderClient.PostAsJsonAsync("/orders", new CreateOrderRequest(items));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreatedOrderResponse>();

        // Sampled immediately, because the count at timeout cannot distinguish
        // "written and since delivered" from "never written at all" - both read
        // as zero 90 s later. This is the sample that tells them apart.
        _outboxAfterCreate = await CountOutboxRowsAsync();

        return body!.Id;
    }

    /// <summary>
    /// MassTransit registers a bus health check whose description names each
    /// configured receive endpoint and its state. The CI log only ever shows
    /// the tail of the run, so the startup lines that would reveal a missing
    /// or failed saga endpoint are never visible - this reads the same
    /// information back at the point of failure.
    /// </summary>
    private async Task<string> DescribeBusHealthAsync()
    {
        try
        {
            var health = _orderFactory!.Services.GetRequiredService<HealthCheckService>();
            var report = await health.CheckHealthAsync();

            return string.Join(", ", report.Entries.Select(e =>
                $"{e.Key}={e.Value.Status}{(string.IsNullOrEmpty(e.Value.Description) ? "" : $" ({e.Value.Description})")}"));
        }
        catch (Exception ex)
        {
            return $"(health check failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Reads the broker's real topology via rabbitmqctl inside the container.
    /// The outbox proves the message reached RabbitMQ and the bus reports
    /// Healthy, yet nothing consumed it and nothing faulted - which is what a
    /// publish to an exchange with no queue bound to it looks like. That has
    /// been inferred twice now without being checked; list_queues shows
    /// whether the saga's queue exists and has a consumer, and list_bindings
    /// shows whether the OrderSubmitted exchange actually routes to it.
    /// Uses exec rather than the management HTTP API, which RabbitMqBuilder
    /// does not expose.
    /// </summary>
    private async Task<string> DescribeBrokerTopologyAsync()
    {
        try
        {
            var queues = await _rabbitMq.ExecAsync(new[]
            {
                "rabbitmqctl", "list_queues", "name", "messages", "consumers", "--no-table-headers"
            });
            var bindings = await _rabbitMq.ExecAsync(new[]
            {
                "rabbitmqctl", "list_bindings", "source_name", "destination_name", "--no-table-headers"
            });

            return $"queues:{Environment.NewLine}{Truncate(queues.Stdout)}{Environment.NewLine}" +
                   $"bindings:{Environment.NewLine}{Truncate(bindings.Stdout)}";
        }
        catch (Exception ex)
        {
            return $"(broker topology query failed: {ex.Message})";
        }

        static string Truncate(string value) =>
            string.IsNullOrWhiteSpace(value) ? "(empty)"
            : value.Length <= 4000 ? value.TrimEnd()
            : value[..4000] + " …(truncated)";
    }

    private string _outboxAfterCreate = "(not sampled)";

    private async Task<string> CountOutboxRowsAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<OrderDbContext>()
                .UseNpgsql(_orderDb.GetConnectionString())
                .Options;
            await using var db = new OrderDbContext(options);

            var messages = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OutboxMessage\"")
                .SingleAsync();
            var states = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OutboxState\"")
                .SingleAsync();

            return $"OutboxMessage={messages}, OutboxState={states}";
        }
        catch (Exception ex)
        {
            return $"(query failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Reads InventoryService's side of the exchange, which no diagnostic has
    /// looked at so far - CountOutboxRowsAsync only ever queries _orderDb, so
    /// everything downstream of ReserveStock has been invisible.
    ///
    /// Splits the remaining possibilities apart. A decremented quantity and an
    /// InboxState row prove ReserveStockConsumer actually ran; OutboxMessage
    /// rows left behind prove it published StockReserved but the message never
    /// reached the broker. Untouched quantity with no InboxState row means
    /// ReserveStock never arrived at all, and the stall is upstream.
    /// </summary>
    private async Task<string> DescribeInventoryStateAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(_inventoryDb.GetConnectionString())
                .Options;
            await using var db = new InventoryDbContext(options);

            var quantities = new List<string>();
            foreach (var productId in _seededProducts)
            {
                var item = await db.Set<InventoryItem>().AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProductId == productId);
                var shortId = productId.ToString("N")[..8];
                quantities.Add($"{shortId}={item?.QuantityAvailable.ToString() ?? "(missing)"}");
            }

            var inbox = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"InboxState\"")
                .SingleAsync();
            var messages = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OutboxMessage\"")
                .SingleAsync();
            var states = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OutboxState\"")
                .SingleAsync();

            return $"quantities: {string.Join(", ", quantities)}; " +
                   $"InboxState={inbox}, OutboxMessage={messages}, OutboxState={states}";
        }
        catch (Exception ex)
        {
            return $"(inventory query failed: {ex.Message})";
        }
    }

    // JsonSerializerDefaults.Web is load-bearing, not decoration. The service
    // serializes camelCase, so a bare JsonSerializerOptions - which defaults
    // PropertyNameCaseInsensitive to false - matches none of OrderDto's
    // PascalCase constructor parameters. Positional records bind by parameter
    // name and silently take the default when nothing matches, so rather than
    // throwing, every field comes back as default: Id as Guid.Empty and Status
    // as OrderStatus.Pending, the zero value. WaitForResolutionAsync then reads
    // Pending forever no matter what the database actually holds.
    //
    // The converter is still required on top: Web defaults handle the naming,
    // but OrderStatus is written as "Confirmed"/"Cancelled" rather than an
    // integer (JsonStringEnumConverter, configured in OrderService/Program.cs).
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<OrderDto> WaitForResolutionAsync(Guid orderId, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var response = await _orderClient.GetAsync($"/orders/{orderId}");
            response.EnsureSuccessStatusCode();
            var order = await response.Content.ReadFromJsonAsync<OrderDto>(_jsonOptions);

            if (order!.Status != OrderStatus.Pending)
                return order;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Order {orderId} did not resolve out of Pending within {timeoutSeconds}s. " +
            await DescribeStalledStateAsync());
    }

    /// <summary>
    /// Explains *where* a stalled checkout stopped, so a CI timeout is
    /// actionable. Distinguishes the three possible stalls: the OrderSubmitted
    /// event never left the outbox, the saga never started, or the saga started
    /// but is still waiting on stock responses.
    /// </summary>
    private async Task<string> DescribeStalledStateAsync()
    {
        var faults = _faults.IsEmpty
            ? "none"
            : string.Join(" | ", _faults);

        try
        {
            var options = new DbContextOptionsBuilder<OrderDbContext>()
                .UseNpgsql(_orderDb.GetConnectionString())
                .Options;
            await using var db = new OrderDbContext(options);

            var sagaStates = await db.Database
                .SqlQueryRaw<string>("SELECT coalesce(string_agg(\"CurrentState\", ','), '(no rows)') AS \"Value\" FROM \"OrderSagaStates\"")
                .SingleAsync();

            // Read the row straight from Postgres, bypassing the HTTP path the
            // polling loop uses. FinalizeAsync logs a successful save at the
            // same second the order is created, yet GET /orders keeps returning
            // Pending for 90s afterwards. A committed update that later reads
            // cannot see is impossible inside one database - so either this
            // disagrees with HTTP (the read path is at fault) or it agrees
            // (the write never actually committed).
            var direct = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT coalesce(string_agg(\"Status\" || '/' || coalesce(\"CancellationReason\", '-'), ','), '(no rows)') AS \"Value\" " +
                    "FROM \"Orders\"")
                .SingleAsync();

            return $"orders table (direct): {direct}; " +
                   $"outbox right after create: [{_outboxAfterCreate}]; " +
                   $"outbox now: [{await CountOutboxRowsAsync()}]; " +
                   $"saga states: {sagaStates}; faults: {faults}; " +
                   $"order bus health: [{await DescribeBusHealthAsync()}]{Environment.NewLine}" +
                   $"inventory: [{await DescribeInventoryStateAsync()}]{Environment.NewLine}" +
                   await DescribeBrokerTopologyAsync();
        }
        catch (Exception ex)
        {
            return $"faults: {faults}; (diagnostics query failed: {ex.Message})";
        }
    }

    // Static so the recorder consumer (constructed by DI in the inventory host)
    // can report back into the test that is currently running.
    private static readonly ConcurrentBag<string> _faults = new();

    private sealed class FaultRecorder :
        IConsumer<Fault<OrderSubmitted>>,
        IConsumer<Fault<ReserveStock>>,
        IConsumer<Fault<ReleaseStock>>,
        IConsumer<Fault<StockReserved>>,
        IConsumer<Fault<StockReservationFailed>>
    {
        public Task Consume(ConsumeContext<Fault<OrderSubmitted>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<ReserveStock>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<ReleaseStock>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<StockReserved>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<StockReservationFailed>> context) => Record(context.Message);

        private static Task Record<T>(Fault<T> fault) where T : class
        {
            var detail = fault.Exceptions.FirstOrDefault();
            _faults.Add($"{typeof(T).Name} faulted: {detail?.ExceptionType}: {detail?.Message}");
            return Task.CompletedTask;
        }
    }

    private sealed record CreatedOrderResponse(Guid Id);
}
