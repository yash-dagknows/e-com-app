// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using cart.cartstore;
using OpenFeature;
using Oteldemo;

namespace cart.services;

public class CartService : Oteldemo.CartService.CartServiceBase
{
    private static readonly Empty Empty = new();
    private readonly Random random = new Random();
    private readonly ICartStore _badCartStore;
    private readonly ICartStore _cartStore;
    private readonly IFeatureClient _featureFlagHelper;
    private readonly ILogger<CartService> _logger;

    // Synthetic scenario: simulate a fixed-size Redis connection pool. Under
    // sustained concurrent AddItem load, callers beyond PoolSize are rejected
    // with gRPC RESOURCE_EXHAUSTED after a short wait, modelling pool exhaustion.
    private const int PoolSize = 5;
    private static readonly SemaphoreSlim _poolGate = new(PoolSize, PoolSize);

    public CartService(
        ICartStore cartStore,
        ICartStore badCartStore,
        IFeatureClient featureFlagService,
        ILogger<CartService> logger)
    {
        _badCartStore = badCartStore;
        _cartStore = cartStore;
        _featureFlagHelper = featureFlagService;
        _logger = logger;
    }

    public override async Task<Empty> AddItem(AddItemRequest request, ServerCallContext context)
    {
        var activity = Activity.Current;
        activity?.SetTag("app.user.id", request.UserId);
        activity?.SetTag("app.product.id", request.Item.ProductId);
        activity?.SetTag("app.product.quantity", request.Item.Quantity);

        var acquired = await _poolGate.WaitAsync(TimeSpan.FromMilliseconds(100));
        if (!acquired)
        {
            var inUse = PoolSize - _poolGate.CurrentCount;
            _logger.LogWarning(
                "redis pool exhausted: user_id={UserId} permits_in_use={InUse} pool_size={PoolSize}",
                request.UserId, inUse, PoolSize);
            activity?.SetTag("app.cart.pool_permits_in_use", inUse);
            activity?.SetTag("app.cart.pool_size", PoolSize);
            activity?.SetStatus(ActivityStatusCode.Error, "redis pool exhausted");
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "redis connection pool exhausted"));
        }

        try
        {
            await _cartStore.AddItemAsync(request.UserId, request.Item.ProductId, request.Item.Quantity);

            return Empty;
        }
        catch (RpcException ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _poolGate.Release();
        }
    }

    public override async Task<Cart> GetCart(GetCartRequest request, ServerCallContext context)
    {
        var activity = Activity.Current;
        activity?.SetTag("app.user.id", request.UserId);
        activity?.AddEvent(new("Fetch cart"));

        try
        {
            var cart = await _cartStore.GetCartAsync(request.UserId);
            var totalCart = 0;
            foreach (var item in cart.Items)
            {
                totalCart += item.Quantity;
            }
            activity?.SetTag("app.cart.items.count", totalCart);

            return cart;
        }
        catch (RpcException ex)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public override async Task<Empty> EmptyCart(EmptyCartRequest request, ServerCallContext context)
    {
        var activity = Activity.Current;
        activity?.SetTag("app.user.id", request.UserId);
        activity?.AddEvent(new("Empty cart"));

        try
        {
            if (await _featureFlagHelper.GetBooleanValueAsync("cartFailure", false))
            {
                await _badCartStore.EmptyCartAsync(request.UserId);
            }
            else
            {
                await _cartStore.EmptyCartAsync(request.UserId);
            }
        }
        catch (RpcException ex)
        {
            Activity.Current?.AddException(ex);
            Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }

        return Empty;
    }
}
