// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Oteldemo;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace Accounting;

internal class DBContext : DbContext
{
    public DbSet<OrderEntity> Orders { get; set; }
    public DbSet<OrderItemEntity> CartItems { get; set; }
    public DbSet<ShippingEntity> Shipping { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

        optionsBuilder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
    }
}


internal class Consumer : IDisposable
{
    private const string TopicName = "orders";

    private ILogger _logger;
    private IConsumer<string, byte[]> _consumer;
    private bool _isListening;
    private DBContext? _dbContext;
    private static readonly ActivitySource MyActivitySource = new("Accounting.Consumer");

    // Synthetic scenario state: track recent consume timestamps to model a
    // throughput threshold beyond which per-message processing slows down.
    private static readonly ConcurrentQueue<DateTime> _recentMessageTimestamps = new();
    private const int LagThresholdMsgPerMinute = 20;

    public Consumer(ILogger<Consumer> logger)
    {
        _logger = logger;

        var servers = Environment.GetEnvironmentVariable("KAFKA_ADDR")
            ?? throw new ArgumentNullException("KAFKA_ADDR");

        _consumer = BuildConsumer(servers);
        _consumer.Subscribe(TopicName);

        _logger.LogInformation($"Connecting to Kafka: {servers}");
        _dbContext = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") == null ? null : new DBContext();
    }

    public void StartListening()
    {
        _isListening = true;

        try
        {
            while (_isListening)
            {
                try
                {
                    using var activity = MyActivitySource.StartActivity("order-consumed",  ActivityKind.Internal);
                    var consumeResult = _consumer.Consume();
                    ProcessMessage(consumeResult.Message);
                }
                catch (ConsumeException e)
                {
                    _logger.LogError(e, "Consume error: {0}", e.Error.Reason);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Closing consumer");

            _consumer.Close();
        }
    }

    private void ProcessMessage(Message<string, byte[]> message)
    {
        // Synthetic scenario: when sustained throughput exceeds LagThresholdMsgPerMinute,
        // inject a per-message delay to simulate consumer lag. Loud instrumentation
        // emits a WARN log with the delay + window count + order_id, sets the current
        // Activity to ERROR, and tags the disambiguating attributes.
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-60);
        while (_recentMessageTimestamps.TryPeek(out var oldest) && oldest < cutoff)
        {
            _recentMessageTimestamps.TryDequeue(out _);
        }
        _recentMessageTimestamps.Enqueue(now);
        var recentCount = _recentMessageTimestamps.Count;

        if (recentCount > LagThresholdMsgPerMinute)
        {
            var delayMs = Random.Shared.Next(50, 200);
            Thread.Sleep(delayMs);

            string orderId = "";
            try
            {
                orderId = OrderResult.Parser.ParseFrom(message.Value).OrderId;
            }
            catch
            {
                // best-effort: the main parse below will surface real parse errors
            }

            _logger.LogWarning(
                "accounting consumer lag: processing_ms={ProcessingMs} recent_msg_count={RecentCount} order_id={OrderId}",
                delayMs, recentCount, orderId);
            Activity.Current?.SetTag("app.accounting.processing_ms", delayMs);
            Activity.Current?.SetTag("app.accounting.recent_msg_count", recentCount);
            Activity.Current?.SetTag("app.accounting.order_id", orderId);
            Activity.Current?.SetStatus(ActivityStatusCode.Error, $"consumer lag triggered at {recentCount} msg/min");
        }

        try
        {
            var order = OrderResult.Parser.ParseFrom(message.Value);
            Log.OrderReceivedMessage(_logger, order);

            if (_dbContext == null)
            {
                return;
            }

            var orderEntity = new OrderEntity
            {
                Id = order.OrderId
            };
            _dbContext.Add(orderEntity);
            foreach (var item in order.Items)
            {
                var orderItem = new OrderItemEntity
                {
                    ItemCostCurrencyCode = item.Cost.CurrencyCode,
                    ItemCostUnits = item.Cost.Units,
                    ItemCostNanos = item.Cost.Nanos,
                    ProductId = item.Item.ProductId,
                    Quantity = item.Item.Quantity,
                    OrderId = order.OrderId
                };

                _dbContext.Add(orderItem);
            }

            var shipping = new ShippingEntity
            {
                ShippingTrackingId = order.ShippingTrackingId,
                ShippingCostCurrencyCode = order.ShippingCost.CurrencyCode,
                ShippingCostUnits = order.ShippingCost.Units,
                ShippingCostNanos = order.ShippingCost.Nanos,
                StreetAddress = order.ShippingAddress.StreetAddress,
                City = order.ShippingAddress.City,
                State = order.ShippingAddress.State,
                Country = order.ShippingAddress.Country,
                ZipCode = order.ShippingAddress.ZipCode,
                OrderId = order.OrderId
            };
            _dbContext.Add(shipping);
            _dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order parsing failed:");
        }
    }

    private IConsumer<string, byte[]> BuildConsumer(string servers)
    {
        var conf = new ConsumerConfig
        {
            GroupId = $"accounting",
            BootstrapServers = servers,
            // https://github.com/confluentinc/confluent-kafka-dotnet/tree/07de95ed647af80a0db39ce6a8891a630423b952#basic-consumer-example
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        return new ConsumerBuilder<string, byte[]>(conf)
            .Build();
    }

    public void Dispose()
    {
        _isListening = false;
        _consumer?.Dispose();
    }
}
