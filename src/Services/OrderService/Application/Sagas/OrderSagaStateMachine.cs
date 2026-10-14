using System.Text.Json;
using Contracts.Commands;
using Contracts.Common;
using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderService.Domain;

namespace OrderService.Application.Sagas;

/// <summary>
/// Orchestrates checkout: on OrderSubmitted, sends one ReserveStock command
/// per line item, then waits for a StockReserved/StockReservationFailed
/// response for each. Once every item has responded, either confirms the
/// order (all reserved) or compensates by releasing whatever *did* succeed
/// and cancels the order (any failure).
/// </summary>
public class OrderSagaStateMachine : MassTransitStateMachine<OrderSagaState>
{
    // The persisted CurrentState strings. CheckoutTimeoutSweeper and
    // OrderMetricsCollector query on them, so they are named here rather than
    // repeated as literals that could drift from the properties below.
    public const string AwaitingStockReservationName = nameof(AwaitingStockReservation);
    public const string TimedOutName = nameof(TimedOut);

    public State AwaitingStockReservation { get; private set; } = default!;

    /// <summary>
    /// A checkout that gave up waiting. The order is already cancelled and the
    /// stock the saga knew about already released, but the row is deliberately
    /// kept for a while instead of finalized: a ReserveStock that was still in
    /// flight (InventoryService was down) completes later, and its StockReserved
    /// must find a saga so that reservation can be released too. CheckoutTimeoutSweeper
    /// deletes the row once the retention window has passed.
    /// </summary>
    public State TimedOut { get; private set; } = default!;

    public Event<OrderSubmitted> OrderSubmittedEvent { get; private set; } = default!;
    public Event<StockReserved> StockReservedEvent { get; private set; } = default!;
    public Event<StockReservationFailed> StockReservationFailedEvent { get; private set; } = default!;
    public Event<CheckoutTimedOut> CheckoutTimedOutEvent { get; private set; } = default!;

    public OrderSagaStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmittedEvent, e =>
        {
            e.CorrelateById(context => context.Message.OrderId);
            e.SelectId(context => context.Message.OrderId);
        });
        //Event(() => OrderSubmittedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => StockReservedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => StockReservationFailedEvent, x => x.CorrelateById(context => context.Message.OrderId));

        // Discard rather than fault when no instance matches. The sweeper
        // republishes on every tick until the saga is gone, so a timeout that
        // arrives just after the checkout finished normally - or a second one
        // for a saga the first already finalized - is expected, not an error.
        // MassTransit's default for an unmatched non-initial event is to throw.
        Event(() => CheckoutTimedOutEvent, x =>
        {
            x.CorrelateById(context => context.Message.OrderId);
            x.OnMissingInstance(m => m.Discard());
        });

        // Only an order that is still Pending gets a checkout. A second OrderSubmitted for one
        // that is not is a replay or a duplicate (a message parked in an _error queue during an
        // outage, moved back by deploy/scripts/replay-errors.sh, for an order whose other copy
        // already got through). Starting a saga for it would reserve the stock again, and
        // nothing would ever release that: the order is already resolved. Reproduced by
        // OrderSubmitted_ForAnOrderThatIsAlreadyConfirmed_DoesNotReserveStockAgain.
        Initially(
            When(OrderSubmittedEvent)
                .IfElseAsync(IsOrderPendingAsync,
                    start => start
                        .Then(context =>
                        {
                            context.Saga.CreatedAtUtc = DateTimeOffset.UtcNow;
                            context.Saga.UserId = context.Message.UserId;
                            context.Saga.TotalItems = context.Message.Items.Count;
                            context.Saga.ResponseCount = 0;
                            context.Saga.HasFailure = false;
                            context.Saga.ItemsJson = JsonSerializer.Serialize(context.Message.Items);
                            context.Saga.ReservedProductIdsJson = "[]";
                        })
                        .ThenAsync(async context =>
                        {
                            foreach (var item in context.Message.Items)
                            {
                                await context.Publish(new ReserveStock(context.Message.OrderId, item.ProductId, item.Quantity));
                            }
                        })
                        .TransitionTo(AwaitingStockReservation),
                    skip => skip
                        .Then(context => context.GetPayload<IServiceProvider>()
                            .GetService<ILogger<OrderSagaStateMachine>>()?
                            .LogWarning(
                                "Ignoring OrderSubmitted for order {OrderId}: it is not a Pending order (already resolved, or unknown)",
                                context.Message.OrderId))
                        .Finalize())
        );

        // The same message for a checkout that already exists is ignored, not faulted:
        // starting over would send every ReserveStock again.
        During(AwaitingStockReservation,
            Ignore(OrderSubmittedEvent));

        During(AwaitingStockReservation,
            When(StockReservedEvent)
                .Then(context =>
                {
                    context.Saga.ResponseCount++;

                    var reserved = JsonSerializer.Deserialize<List<Guid>>(context.Saga.ReservedProductIdsJson) ?? new List<Guid>();
                    reserved.Add(context.Message.ProductId);
                    context.Saga.ReservedProductIdsJson = JsonSerializer.Serialize(reserved);
                })
                .IfElse(context => context.Saga.ResponseCount >= context.Saga.TotalItems,
                    complete => complete.ThenAsync(FinalizeAsync).Finalize(),
                    incomplete => incomplete),

            When(StockReservationFailedEvent)
                .Then(context => context.GetPayload<IServiceProvider>()
                    .GetRequiredService<OrderMetrics>()
                    .RecordReservationFailure())
                .Then(context => context.Saga.HasFailure = true)
                .Then(context => context.Saga.FailureReason ??= context.Message.Reason)
                .Then(context => context.Saga.ResponseCount++)
                .IfElse(context => context.Saga.ResponseCount >= context.Saga.TotalItems,
                    complete => complete.ThenAsync(FinalizeAsync).Finalize(),
                    incomplete => incomplete),

            // Give up waiting. Compensating through the same path as a rejected
            // reservation is the point: it releases whatever stock was already
            // reserved for this order, which is what would otherwise be held
            // forever, and cancels the order rather than leaving it Pending.
            // ResponseCount is deliberately left alone - it is the record of how
            // many items did answer, and FinalizeAsync does not consult it.
            //
            // Unlike the two paths above this does NOT Finalize(): that would
            // delete the row, and a reservation still in flight would then have
            // no saga to be released by. It moves to TimedOut instead.
            When(CheckoutTimedOutEvent)
                .Then(context =>
                {
                    context.Saga.HasFailure = true;
                    context.Saga.FailureReason ??=
                        "Timed out waiting for stock reservation responses.";
                })
                .ThenAsync(FinalizeAsync)
                .TransitionTo(TimedOut)
        );

        During(TimedOut,
            // A reservation that completed after the deadline: nothing will use
            // this stock (the order is cancelled), so give it back.
            When(StockReservedEvent)
                .ThenAsync(ReleaseLateReservationAsync),

            // A late refusal took no stock, so there is nothing to undo.
            Ignore(StockReservationFailedEvent),

            // A replayed OrderSubmitted: the checkout already ran and gave up.
            Ignore(OrderSubmittedEvent),

            // The sweeper only selects AwaitingStockReservation rows, but a
            // timeout already in flight can still arrive; it has nothing to do.
            Ignore(CheckoutTimedOutEvent)
        );

        SetCompletedWhenFinalized();
    }

    private static async Task<bool> IsOrderPendingAsync(BehaviorContext<OrderSagaState, OrderSubmitted> context)
    {
        var repo = context.GetPayload<IServiceProvider>().GetRequiredService<IOrderRepository>();
        var order = await repo.GetByIdAsync(context.Message.OrderId);

        return order is { Status: OrderStatus.Pending };
    }

    private static async Task ReleaseLateReservationAsync(BehaviorContext<OrderSagaState, StockReserved> context)
    {
        var saga = context.Saga;
        var productId = context.Message.ProductId;

        var reserved = JsonSerializer.Deserialize<List<Guid>>(saga.ReservedProductIdsJson) ?? new List<Guid>();
        var lines = (JsonSerializer.Deserialize<List<OrderLineItem>>(saga.ItemsJson) ?? new List<OrderLineItem>())
            .Where(i => i.ProductId == productId)
            .ToList();

        // ReservedProductIdsJson already holds everything released so far: what
        // the timeout released, plus earlier late releases. StockReserved is
        // delivered at least once and this endpoint has no inbox, so a duplicate
        // is possible, and releasing twice would inflate stock. Counting
        // (rather than a plain Contains) keeps an order with two lines for the
        // same product correct: each line is released once, in order.
        var alreadyReleased = reserved.Count(id => id == productId);
        if (alreadyReleased >= lines.Count)
            return;

        var line = lines[alreadyReleased];

        reserved.Add(productId);
        saga.ReservedProductIdsJson = JsonSerializer.Serialize(reserved);

        await context.Publish(new ReleaseStock(saga.CorrelationId, productId, line.Quantity));

        context.GetPayload<IServiceProvider>()
            .GetRequiredService<OrderMetrics>()
            .RecordLateReservationReleased();
    }

    private static async Task FinalizeAsync<T>(BehaviorContext<OrderSagaState, T> context) where T : class
    {
        var saga = context.Saga;
        var provider = context.GetPayload<IServiceProvider>();
        var repo = provider.GetRequiredService<IOrderRepository>();

        var order = await repo.GetByIdAsync(saga.CorrelationId);
        if (order is null)
            return;

        // The opposite state: a confirmed order whose checkout failed or timed out, or a
        // cancelled one whose checkout succeeded. Nothing this saga can do is right, and
        // throwing is worse than leaving it: the failure path is driven by the sweeper, which
        // republishes the timeout on every tick until the saga leaves AwaitingStockReservation,
        // so a throw here faulted and parked a message every tick, forever (six such sagas,
        // about 12 parked messages a minute, for days). So it is logged loudly, the order is
        // left exactly as it is, and the saga ends like any other. It does NOT release stock:
        // which of the two checkouts that touched this order owns the reservation is not
        // knowable here, and releasing a confirmed order's stock would oversell it.
        var conflicting = saga.HasFailure
            ? order.Status == OrderStatus.Confirmed
            : order.Status == OrderStatus.Cancelled;
        if (conflicting)
        {
            provider.GetService<ILogger<OrderSagaStateMachine>>()?.LogError(
                "Checkout for order {OrderId} ended {Outcome} but the order is already {Status}; leaving the order unchanged and finishing the checkout",
                order.Id,
                saga.HasFailure ? "unsuccessfully" : "successfully",
                order.Status);
            return;
        }

        // Idempotent: if the order is already in the state this checkout would put it in
        // (a redelivery after the order's own write committed), there is nothing to save,
        // no stock to release a second time and nothing to count - the saga just finishes.
        bool changed;
        if (saga.HasFailure)
        {
            changed = order.Cancel(saga.FailureReason ?? "Unable to reserve stock for one or more items.");

            if (changed)
            {
                var items = JsonSerializer.Deserialize<List<OrderLineItem>>(saga.ItemsJson) ?? new List<OrderLineItem>();
                var reservedProductIds = JsonSerializer.Deserialize<List<Guid>>(saga.ReservedProductIdsJson) ?? new List<Guid>();

                foreach (var productId in reservedProductIds)
                {
                    var item = items.FirstOrDefault(i => i.ProductId == productId);
                    if (item is not null)
                        await context.Publish(new ReleaseStock(saga.CorrelationId, productId, item.Quantity));
                }
            }
        }
        else
        {
            changed = order.Confirm();
        }

        if (!changed)
        {
            provider.GetService<ILogger<OrderSagaStateMachine>>()?.LogWarning(
                "Order {OrderId} was already {Status} when its checkout finalized; nothing to do",
                order.Id,
                order.Status);
            return;
        }

        await repo.SaveChangesAsync();

        provider.GetRequiredService<OrderMetrics>().RecordFinalized(saga.HasFailure);
    }
}
