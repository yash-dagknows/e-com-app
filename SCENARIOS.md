# Error Scenarios — AI SRE Agent Training Set

Ground-truth documentation for the bug scenarios in this repo. Each scenario lists how to trigger it, what observability signals to expect, and the conclusion an ideal SRE agent should reach.

Two injection patterns:
- **Flag-based** — toggle via flagd UI at `http://<host>/feature`. Defined in [src/flagd/demo.flagd.json](src/flagd/demo.flagd.json).
- **Branch-based** — deploy a `scenario/<name>` branch. Bug is baked into the code.

---

## Index

### Flag-based scenarios (new)
- [paymentRetryStorm](#paymentretrystorm) — checkout retries payments 5x with no backoff
- [adGcThrash](#adgcthrash) — JVM heap pressure causing minor-GC sawtooth

### Branch-based scenarios (new)
- [scenario/currency-service-creep](#scenariocurrency-service-creep) — silent 200-500ms jitter in currency
- [scenario/email-unicode-crash](#scenarioemail-unicode-crash) — Ruby email service crashes on Unicode product names
- [scenario/cart-stale-after-checkout](#scenariocart-stale-after-checkout) — EmptyCart fire-and-forget leaves stale cart

### Branch-based scenarios (Round 1 — June 2026)
- [scenario/shippingTimeout](#scenarioshippingtimeout) — ~15% of ship-order calls exceed caller gRPC deadline
- [scenario/recommendationTimeoutOnLargeContext](#scenariorecommendationtimeoutonlargecontext) — O(n²) slow path triggered when context_size > 10
- [scenario/checkoutKafkaPublishFailure](#scenariocheckoutkafkapublishfailure) — 10% of order events silently dropped from Kafka publish

### Branch-based scenarios (Round 2 — June 2026)
- [scenario/productCatalogSlowSearch](#scenarioproductcatalogslowsearch) — input-correlated slow path on SearchProducts for queries >= 4 chars
- [scenario/accountingConsumerLag](#scenarioaccountingconsumerlag) — Kafka consumer lag accumulation under sustained > 20 msg/min throughput
- [scenario/cartRedisPoolExhaustion](#scenariocartredispoolexhaustion) — 5-slot connection pool rejects concurrent AddItem with RESOURCE_EXHAUSTED
- [scenario/adCircuitBreakerFlap](#scenarioadcircuitbreakerflap) — ~25% of getAds calls silently return a hardcoded fallback ad
- [scenario/frontendStaleProductRace](#scenariofrontendstaleproductrace) — ~10% of product-detail requests serve the previously-fetched product

### Existing scenarios (for reference)
- [scenario/cc-expiry-bug](#scenariocc-expiry-bug) — credit-card expiry-year off-by-N bug

---

## paymentRetryStorm

**Type**: flagd | **Difficulty**: hard | **Signals**: traces + metrics + logs (cross-signal)

**Trigger**: Set `paymentRetryStorm` to `on` in flagd. **Also set `paymentFailure` to `25%` or higher** — without an underlying failure, retries do nothing.

**Root cause**: Checkout's `chargeCard()` retries the payment gRPC call up to 5 times with no backoff and no jitter when this flag is on. Combined with a non-zero `paymentFailure`, this multiplies payment service load by ~3-5x and dramatically increases tail latency for checkouts whose first attempt fails.

**Expected signals**:
- **Jaeger**: a single checkout trace contains 1–5 sibling `payment.Charge` gRPC spans. Successful charges have 1; transient failures lead to 2-5. Search: `service=checkoutservice operation=oteldemo.PaymentService/Charge`.
- **Grafana / Prometheus**: payment service RPC rate climbs 3-5x baseline once a few percent of users hit the retry path. Look at `rpc_server_duration_count{service="payment"}` rate.
- **OpenSearch**: payment-service error logs ("Payment request failed...") scale up roughly proportionally. Checkout logs show no special pattern — that's part of the puzzle.

**Required co-flags**: `paymentFailure >= 25%` (otherwise no signal).

**SRE-agent expected conclusion**: Payment failures aren't the root cause — the root cause is checkout's retry policy. Evidence: trace shows multiple Charge spans per checkout; payment QPS far exceeds checkout QPS.

---

## adGcThrash

**Type**: flagd | **Difficulty**: medium | **Signals**: JVM GC metrics + p99 sawtooth

**Trigger**: Set `adGcThrash` to `on` in flagd. Allow 2-5 min for sawtooth to develop.

**Root cause**: Ad service allocates ~1MB byte arrays into a bounded queue per request, evicting periodically. Allocation churn forces frequent minor GCs every few seconds (distinct from the existing `adManualGc` flag, which is a one-off full GC).

**Expected signals**:
- **Grafana / Prometheus**: `jvm_gc_pause_seconds_count` rate climbs from ~0.1/s to >1/s. `jvm_memory_used_bytes{area="heap"}` shows sawtooth. Ad service p99 latency wobbles in sync with GC pauses.
- **Jaeger**: ad spans p99 increases; some traces have noticeably longer ad spans when they coincide with a GC pause.
- **OpenSearch**: nothing special — GC isn't error-logged at default levels.

**SRE-agent expected conclusion**: Ad service is GC-thrashing. Heap allocation pattern problem, not CPU or downstream. Evidence: high GC rate metric + memory sawtooth + p99 correlation with pause timing.

---

## scenario/currency-service-creep

**Type**: branch | **Difficulty**: medium | **Signals**: traces (p99 drift) + cascade

**Trigger**: Deploy the `scenario/currency-service-creep` branch.

**Root cause**: Currency service's conversion RPC has a uniform-random 200–500ms sleep inserted into every request handler. Since every checkout flow invokes currency conversion for cart items, the latency tax compounds.

**Expected signals**:
- **Jaeger**: `currency.Convert` spans average ~350ms (vs. ~5ms baseline). Checkout traces show the currency call as the dominant time consumer.
- **Grafana**: checkout p99 latency shifts up by 300–500ms steadily. Currency service p99 stays bounded (no spike — just a higher floor).
- **OpenSearch**: no error logs. Everything succeeds.

**SRE-agent expected conclusion**: Currency service has degraded — every conversion takes 200-500ms when it used to take ms. No errors anywhere; must be diagnosed from trace timeline + p99 trend.

---

## scenario/email-unicode-crash

**Type**: branch | **Difficulty**: medium | **Signals**: logs + error traces + checkout 5xx

**Trigger**: Deploy the `scenario/email-unicode-crash` branch. Affected orders are those containing the seeded Unicode-named product.

**Root cause**: Ruby email service `raise`s when a product name in the order payload contains certain Unicode codepoints. The crash propagates back to checkout (email is sync HTTP), causing checkout 5xx for affected orders.

**Expected signals**:
- **OpenSearch**: Ruby stack trace logs from email service: `EncodingError` or similar, with product name in context. Filter by `service.name=email`.
- **Jaeger**: error spans on email service tagged with the failing product ID. Checkout span also errors.
- **Grafana**: checkout error rate ticks up for the affected product subset.

**SRE-agent expected conclusion**: Email service crashes on specific product names containing Unicode. Affected orders correlate with one product ID (or product-name pattern). Fix is in email service's Unicode handling, not checkout or payment.

---

## scenario/cart-stale-after-checkout

**Type**: branch | **Difficulty**: medium-hard | **Signals**: missing trace span + business metric (no error logs)

**Trigger**: Deploy the `scenario/cart-stale-after-checkout` branch. Run several checkouts and re-load the cart for the same user.

**Root cause**: Checkout's `EmptyCart` call to the cart service is fire-and-forget (`go func() { _ = client.EmptyCart(...) }()`) with no error handling. When the checkout pod's context cancels (e.g. response already sent), the goroutine sometimes dies before completing. ~10-30% of carts retain stale items.

**Expected signals**:
- **Jaeger**: ~10-30% of checkout traces are *missing* the `EmptyCart` child span. Other spans look normal.
- **Grafana**: business metric "cart-still-has-items after checkout" anomaly (if defined). Otherwise visible only via user-report or sampling.
- **OpenSearch**: **no error logs**. This is the hard part — the bug is silent.

**Note for SRE-agent grading**: This scenario tests the agent's ability to investigate when classic error signals are absent. Without a user report or business metric as a seed, the agent may not even know to investigate. Document the user-report or business signal that should be fed as the agent's starting prompt.

**SRE-agent expected conclusion**: `EmptyCart` is being called in a fire-and-forget pattern; the call is sometimes dropped when the checkout response cycle completes early. Evidence: missing `EmptyCart` span in ~20% of traces; no error logs anywhere. Fix is to await the call or detach with proper context.

---

## scenario/cc-expiry-bug

**Type**: branch | **Difficulty**: medium | **Signals**: logs (Node error) + traces (payment error spans)

**Trigger**: Deploy the `scenario/cc-expiry-bug` branch. All credit cards with 4-digit expiry years are evaluated against a buggy `year - 17` normalization, marking many valid cards as expired.

**Root cause**: `src/payment/charge.js` normalizes 4-digit expiry years by subtracting 17 (the bug — the original intent was unclear, and three iterative "fixes" tweak the offset between 17 and 20). All current-year cards are misjudged.

**Expected signals**:
- **OpenSearch**: payment service error logs `The credit card (...) expired on M/YYYY.`
- **Jaeger**: payment Charge spans erroring on most checkouts.
- **Grafana**: checkout error rate elevated, payment failure rate near 100%.

**SRE-agent expected conclusion**: Payment is rejecting cards as expired even though their expiry is in the future. Root cause is the year normalization logic in charge.js. The branch's three "fix" commits are red herrings — they tune the wrong knob.

---

## scenario/shippingTimeout

**Type**: branch | **Difficulty**: medium | **Signals**: traces (DeadlineExceeded) + logs (WARN with processing_ms)

**Trigger**: Deploy the `scenario/shippingTimeout` branch (commit `50f70ca`). Bug fires on ~15% of `/ship-order` calls.

**Root cause**: Rust shipping service injects a 2000-3000ms `tokio::time::sleep` into 15% of `ship_order` handler invocations. The caller (checkout) has a sub-2s gRPC client deadline, so those calls return `DeadlineExceeded` even though shipping eventually responds with HTTP 200.

**Expected signals**:
- **Jaeger**: ~15% of `shipping` spans show ERROR status with attribute `app.shipping.processing_ms` in 2000-3000 range. Cross-cut: ~15% of `checkout` traces show `DeadlineExceeded` on the `ShippingService/ShipOrder` call.
- **Grafana / Prometheus**: checkout error rate elevated; shipping p99 spikes.
- **OpenSearch**: WARN logs `"shipping handler took Nms - likely past caller deadline"` with structured fields `processing_ms` and `address_country`.

**SRE-agent expected conclusion**: ~15% of shipping requests take 2-3s, exceeding caller deadline. Loud signal is on both server and client side; agent should correlate the WARN log's `processing_ms` field with the caller-side `DeadlineExceeded` errors via trace IDs.

---

## scenario/recommendationTimeoutOnLargeContext

**Type**: branch | **Difficulty**: medium | **Signals**: traces (DeadlineExceeded) + WARN logs + span events

**Trigger**: Deploy the `scenario/recommendationTimeoutOnLargeContext` branch (commit `99cbad6`). Bug fires only when caller passes more than 10 product IDs in `request.product_ids`.

**Root cause**: Python recommendation service's `ListRecommendations` enters an O(n²) loop with a 20ms sleep per outer iteration when `len(request.product_ids) > 10`. At context_size=12 the handler takes ~240ms, past the default 200ms gRPC deadline.

**Expected signals**:
- **Jaeger**: slow-path traces show span attribute `app.recommendation.context_size > 10`, span event `slow_path_entered`, and span status ERROR if the deadline trips. Caller sees `DeadlineExceeded`.
- **Grafana**: recommendation p99 latency bimodal — most requests fast, large-context ones slow.
- **OpenSearch**: WARN log `"recommendation slow-path triggered for context_size=N (will likely exceed deadline)"` with structured `context_size`. On exceptions: ERROR log with `exception_class`.

**SRE-agent expected conclusion**: The bug is input-correlated, not load-correlated. Recommendation requests fail only when the context size exceeds 10. The agent should identify the disambiguating `context_size` attribute and find the slow_path_entered event — not conclude "recommendation under load."

---

## scenario/checkoutKafkaPublishFailure

**Type**: branch | **Difficulty**: hard (silent on user side) | **Signals**: reconciliation + dedicated error span + ERROR log

**Trigger**: Deploy the `scenario/checkoutKafkaPublishFailure` branch (commit `4ce9c16`). Bug fires on ~10% of orders. User sees no error.

**Root cause**: Go checkout's `sendToPostProcessor` skips the real Kafka publish for 10% of `OrderResult` events (synthetic `rand.Float64() < 0.10`). Payment has already succeeded; checkout returns success to the user. Accounting (the Kafka consumer) simply never sees those orders.

**Expected signals**:
- **Jaeger**: ~10% of orders carry a dedicated `checkout.KafkaPublishFailure` span with attribute `order.id`, ERROR status, and recorded exception.
- **Reconciliation**: payment.Charge success count exceeds accounting consumer offset growth on the `orders` topic by ~10% over any given window.
- **OpenSearch**: ERROR log `"kafka publish failed for order_id=X: ... — order processed but accounting will not see it"`.
- **User side**: NO error. The bug is invisible to the user.

**SRE-agent expected conclusion**: Recognise this as a reconciliation problem, not a request-path error. Walk: counts checkout.PlaceOrder successes vs Kafka consumer offset delta → finds ~10% gap → log-search for "kafka publish failed for order_id=" → confirm via `checkout.KafkaPublishFailure` span for missing order IDs. Fix is to make publish non-skippable.

---

## scenario/productCatalogSlowSearch

**Type**: branch | **Difficulty**: medium | **Signals**: input-correlated latency + WARN logs + span events

**Trigger**: Deploy the `scenario/productCatalogSlowSearch` branch (commit `5f1f47b`). Bug fires only when `len(req.Query) >= 4`.

**Root cause**: Go product-catalog's `SearchProducts` enters a slow path when the query string is 4+ characters: 5ms sleep per product iterated. With ~10 products in the demo catalog, that's 50-100ms (vs ~1ms baseline). Shorter queries are unaffected.

**Expected signals**:
- **Jaeger**: slow-path traces show span attributes `app.product_catalog.query` and `app.product_catalog.query_len`, plus a `slow_search_triggered` span event. Search-span durations bimodal by query length.
- **Grafana**: product-catalog p99 climbs whenever search traffic includes long queries.
- **OpenSearch**: WARN log `"search slow-path triggered"` with structured fields `query` and `query_len`.

**SRE-agent expected conclusion**: Search latency is correlated with query length, not load. Agent should isolate the `app.product_catalog.query` attribute as the disambiguator and identify queries of 4+ chars as the trigger.

**Traffic-shape note**: load-generator may not exercise long search queries heavily under default config; if zero loud-log hits appear after deploy, augment the load gen to send `q=...` search traffic or lower the threshold from `>= 4` to `>= 2` for testing.

---

## scenario/accountingConsumerLag

**Type**: branch | **Difficulty**: medium | **Signals**: consumer lag + ERROR Activity + WARN logs (sustained-load-correlated)

**Trigger**: Deploy the `scenario/accountingConsumerLag` branch (commit `cde6b80`). Bug fires after sustained `>20 msg/min` consumption for ≥60s.

**Root cause**: .NET accounting consumer maintains a rolling 60-second window of consumed-message timestamps. When the window count exceeds 20, each subsequent `ProcessMessage` sleeps 50-200ms before the real DB write. This produces visible Kafka consumer lag accumulation under sustained load. Light load: no lag.

**Expected signals**:
- **Jaeger**: `order-consumed` Activities with ERROR status, attributes `app.accounting.processing_ms`, `app.accounting.recent_msg_count`, `app.accounting.order_id`.
- **Kafka consumer offset metrics**: lag grows on the `orders` topic for the accounting consumer group.
- **OpenSearch**: WARN logs `"accounting consumer lag: processing_ms=N recent_msg_count=N order_id=..."` with structured fields.

**SRE-agent expected conclusion**: Consumer lag is load-correlated, not message-correlated. Agent should correlate the `recent_msg_count` attribute with the lag growth — identifying that lag begins only when sustained throughput crosses ~20 msg/min. Fix is to parallelize or batch the per-message processing.

---

## scenario/cartRedisPoolExhaustion

**Type**: branch | **Difficulty**: medium | **Signals**: gRPC RESOURCE_EXHAUSTED + ERROR Activity + WARN log

**Trigger**: Deploy the `scenario/cartRedisPoolExhaustion` branch (commit `8f2b570`). Bug fires when >5 concurrent `AddItem` calls are in flight.

**Root cause**: .NET cart service simulates a 5-slot Redis connection pool via a static `SemaphoreSlim(5)` around `AddItem`. The 6th+ concurrent caller waits up to 100ms, then is rejected with gRPC `RESOURCE_EXHAUSTED`.

**Expected signals**:
- **Jaeger**: `cart.AddItem` Activities with ERROR status, attributes `app.cart.pool_permits_in_use`, `app.cart.pool_size=5`, and gRPC code `RESOURCE_EXHAUSTED`.
- **Grafana**: cart error rate scales with concurrent load.
- **OpenSearch**: WARN logs `"redis pool exhausted: user_id=X permits_in_use=N pool_size=5"`.

**SRE-agent expected conclusion**: Cart is the source of the load-correlated RESOURCE_EXHAUSTED errors (not checkout, which sees them as upstream errors). The disambiguating attribute is `app.cart.pool_permits_in_use`. Fix is to grow the pool or queue beyond 100ms.

**Note**: the SemaphoreSlim is process-local; the demo runs single-replica.

---

## scenario/adCircuitBreakerFlap

**Type**: branch | **Difficulty**: hard (silent successful responses) | **Signals**: span attribute mismatch + WARN log + content-level inspection

**Trigger**: Deploy the `scenario/adCircuitBreakerFlap` branch (commit `0836e6a`). Bug fires deterministically on ~25% of `getAds` calls (bucket by `Math.abs(req.hashCode()) % 100 < 25`).

**Root cause**: Java ad service short-circuits 25% of `getAds` calls and returns a hardcoded `Ad{text="FALLBACK_AD - please retry", redirect_url="/cart"}`. The gRPC call succeeds with status OK. The caller has no error to detect — only the *content* is wrong.

**Expected signals**:
- **Jaeger**: ad spans tagged either `app.ad.source=catalog` (75%) or `app.ad.source=fallback` (25%). Fallback spans carry a `circuit_breaker_fallback` event with bucket attribute.
- **OpenSearch**: WARN logs `"ad circuit breaker tripped: returning fallback ad context_keys=[...] bucket=N"`.
- **Grafana**: no error rate increase. Ad service still 200-OKs everything.
- **Content inspection**: rendered ads on the storefront show the canned "FALLBACK_AD" string ~25% of the time.

**SRE-agent expected conclusion**: This is a successful-response / wrong-content bug. Agent must not stop at "ad service is healthy" — must look at the `app.ad.source` span attribute or the response text itself. Fix is in the ad service's fallback logic.

---

## scenario/frontendStaleProductRace

**Type**: branch | **Difficulty**: hard (silent successful responses) | **Signals**: span attribute mismatch + structured stdout WARN + content inspection

**Trigger**: Deploy the `scenario/frontendStaleProductRace` branch (commit `432dc78`). Bug fires on ~10% of `/api/products/[productId]` requests after the first.

**Root cause**: Next.js frontend's product-detail API route caches the last-fetched product in a module-scope variable and returns it instead of fetching for 10% of incoming requests. The HTTP response is 200 with a valid Product schema — but the served product ID does not match the requested product ID.

**Expected signals**:
- **Jaeger**: ~10% of `/api/products/[productId]` spans carry attributes `app.frontend.cache_mismatch=true`, `app.frontend.requested_id`, `app.frontend.served_id`, and span status ERROR.
- **OpenSearch / container logs**: structured JSON `console.warn` lines `{"event":"product_detail_cache_mismatch","requested_id":"X","served_id":"Y","app":"frontend"}`.
- **No error code**: the HTTP response is 200 with valid data — just the wrong product.

**SRE-agent expected conclusion**: This is a successful-response / wrong-content bug seeded by a real user/QA report ("URL says product X but page shows product Y"). Agent must identify the requested vs served ID mismatch (either via the `app.frontend.requested_id` / `served_id` attributes or by inspecting the response body). Fix is to remove the module-scope cache.

---

## Verification per scenario

For each scenario, after triggering:
1. Wait the required time (instant for paymentRetryStorm/emailUnicodeCrash; 2-5 min for adGcThrash and currency-service-creep; sustained for cart-stale-after-checkout).
2. Open Jaeger, Grafana, and OpenSearch and confirm the documented signals appear.
3. Confirm signals listed as *absent* (e.g. no error logs for cartStaleAfterCheckout) really are absent.
4. If running against the AI SRE agent, the agent's root-cause output should match "SRE-agent expected conclusion."
