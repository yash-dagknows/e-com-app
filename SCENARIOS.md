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

## Verification per scenario

For each scenario, after triggering:
1. Wait the required time (instant for paymentRetryStorm/emailUnicodeCrash; 2-5 min for adGcThrash and currency-service-creep; sustained for cart-stale-after-checkout).
2. Open Jaeger, Grafana, and OpenSearch and confirm the documented signals appear.
3. Confirm signals listed as *absent* (e.g. no error logs for cartStaleAfterCheckout) really are absent.
4. If running against the AI SRE agent, the agent's root-cause output should match "SRE-agent expected conclusion."
