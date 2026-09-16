# ShopFlow Observability — The Complete Line-by-Line Guide

This document explains **every meaningful line of code** in the project — what it does, and why it's written that way. The goal is that you can open any file and understand exactly what it's doing without having to memorize it.

> **Note:** The other, shorter `README.md` covers *how to run* the project.
> This document is the "why it's built this way" guide, not a quick-start.

---

## Table of Contents

1. [`Telemetry/ShopFlowTelemetry.cs` — The Real Starting Point](#1-telemetryshopflowtelemetrycs--the-real-starting-point)
2. [`Models/Models.cs` — The Contracts Between Components](#2-modelsmodelscs--the-contracts-between-components)
3. [`Services/ProductService.cs` — The Fake "Database"](#3-servicesproductservicecs--the-fake-database)
4. [`Services/PaymentService.cs` — The Incident's "Time Bomb"](#4-servicespaymentservicecs--the-incidents-time-bomb)
5. [`Services/OrderService.cs` — The Heart of the Trace](#5-servicesorderservicecs--the-heart-of-the-trace)
6. [`Program.cs` — The Wiring Between Everything](#6-programcs--the-wiring-between-everything)
7. [`docker-compose.yml` — How Everything Talks to Each Other](#7-docker-composeyml--how-everything-talks-to-each-other)
8. [`prometheus.yml`](#8-prometheusyml)
9. [`load-test.js` (k6)](#9-load-testjs-k6)
10. [Summary: What File Do I Touch When...](#summary-what-file-do-i-touch-when)

---

## 1. `Telemetry/ShopFlowTelemetry.cs` — The Real Starting Point

```csharp
public const string ServiceName = "ShopFlow.Api";

public static readonly ActivitySource ActivitySource = new(ServiceName);
public static readonly Meter Meter = new(ServiceName);
```

Think of `ActivitySource` and `Meter` as **factories**:

- `ActivitySource` — the factory that produces every **Span** (a piece of a Trace).
- `Meter` — the factory that produces every **Metric** (a number).

**Why are they `static readonly`, in their own dedicated class?**

Because OpenTelemetry works on a strict opt-in principle: *"Any span/metric produced by an `ActivitySource` named X will only be exported if you explicitly tell the pipeline (in `Program.cs`) to `.AddSource("X")`."* That means the name `ServiceName` here has to match, character for character, whatever you write in `Program.cs`. Keeping it as a single `const` in one place — instead of hardcoding the string in every file — is what prevents a typo from silently breaking telemetry.

```csharp
public static readonly Counter<long> OrdersCreated =
    Meter.CreateCounter<long>("orders.created", description: "...");
```

A `Counter<long>` is a number that only ever goes up (like a car-counter on a highway). Every time an order is created successfully, the code calls `OrdersCreated.Add(1, ...)`. What actually happens:

1. The value accumulates inside the `Meter`.
2. When Prometheus scrapes `/metrics`, it sees something like:
   ```
   orders_created_total{payment_method="card"} 1240
   ```
3. From there, you can graph it directly in Grafana.

```csharp
public static readonly Histogram<double> PaymentDuration =
    Meter.CreateHistogram<double>("payment.duration", unit: "ms", ...);
```

A `Histogram` is different from a `Counter`: instead of a single ever-increasing number, it records a **distribution** of values (how many milliseconds each individual payment took), so you can later compute P50/P95/P99 in Prometheus/Grafana. If we'd used a `Counter` here we would only know "how many payments happened" — not "how long each one actually took."

**If you deleted this entire file:** the app would keep running fine (because `.AddAspNetCoreInstrumentation()` alone already gives you automatic traces/metrics for raw HTTP), but you'd lose all visibility into your *business* logic — `orders.created`, `payment.duration`, and named spans like `"CheckInventory"`. You'd be back to generic monitoring instead of real observability.

---

## 2. `Models/Models.cs` — The Contracts Between Components

```csharp
public record Product(int Id, string Name, decimal Price, int Stock);
```

We deliberately used `record` instead of `class`: a `record` gives you `Equals`/`ToString` for free, which matters here because logging sometimes prints the object directly — a much cleaner experience with `record`.

```csharp
public class InsufficientStockException(int productId)
    : Exception($"Product {productId} is out of stock");

public class PaymentFailedException(string reason)
    : Exception($"Payment failed: {reason}");
```

This is C# 12's **primary constructor** syntax — equivalent to:

```csharp
public class InsufficientStockException : Exception
{
    public InsufficientStockException(int productId)
        : base($"Product {productId} is out of stock") { }
}
```

...just shorter. **Why bother with dedicated exception types instead of a plain `throw new Exception("...")`?**

Because in `Program.cs` we can `catch` each type individually and return the right HTTP status code for it — `409 Conflict` for stock issues, `402 Payment Required` for payment failures. If we'd used a generic `Exception`, we'd return `500` for everything, which would be logically wrong.

---

## 3. `Services/ProductService.cs` — The Fake "Database"

```csharp
private readonly List<Product> _products = new()
{
    new Product(1, "Wireless Mouse", 250, 40),
    new Product(2, "Mechanical Keyboard", 950, 15),
    new Product(3, "USB-C Hub", 400, 0), // out of stock, on purpose
};
```

Product #3 has zero stock **on purpose** — that's not a bug. It lets you exercise the failure path (`InsufficientStockException`) without needing any extra setup or data changes.

```csharp
public Product? GetById(int id)
{
    using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("ProductService.GetById");
    activity?.SetTag("product.id", id);
    Thread.Sleep(10);
    ...
}
```

- `using var activity = ...StartActivity(...)`: opens a span named `ProductService.GetById`. Because of `using`, the span closes itself automatically — and its duration gets recorded — the moment the method's scope ends. You never have to call `activity.Stop()` manually.
- `activity?.SetTag(...)`: attaches an attribute to the span, e.g. `product.id = 3`. In Jaeger, opening that span shows this under "Tags" — it tells you exactly which product a given request was asking about when something went wrong.
- The `?` after `activity`: if nothing is listening to this `ActivitySource`, `StartActivity` returns `null` (to save on overhead), so `?.` prevents a `NullReferenceException`.
- `Thread.Sleep(10)`: a deliberate stand-in for "a real database query that takes some time." If you replaced this class with real EF Core, this line would go away — and EF Core's own automatic instrumentation would surface the *actual* query duration instead.

---

## 4. `Services/PaymentService.cs` — The Incident's "Time Bomb"

```csharp
_delayMs = int.TryParse(Environment.GetEnvironmentVariable("SHOPFLOW_PAYMENT_DELAY_MS"), out var d) ? d : 150;
_failRate = double.TryParse(Environment.GetEnvironmentVariable("SHOPFLOW_PAYMENT_FAIL_RATE"), out var f) ? f : 0.0;
```

Settings come from environment variables, not hardcoded values. This is what lets you trigger a "production incident" while the app is already running: change a value in `docker-compose.yml`, run `docker compose up -d` again, and that's it — no C# code touched. This mirrors real-world practice, where a lot of production configuration changes happen through config/env, not through redeploying code.

```csharp
using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("PaymentService.Charge", ActivityKind.Client);
```

Notice the difference from `ProductService`: here we explicitly pass `ActivityKind.Client`. This tells OpenTelemetry "this span represents us waiting on a response from something external" (like a third-party API). In more advanced tracing tools, that distinction gets its own color and its own index, so you can filter for "show me every slow Client call" on its own.

```csharp
await Task.Delay(_delayMs); // this line IS the "External Payment API"
```

This line is, literally, the "External Payment API" you see in all the architecture diagrams. There's no real external API here — we're just waiting. That's entirely sufficient to teach the concept, and there's no need to build a whole second service just for a demo.

```csharp
if (_random.NextDouble() < _failRate)
{
    ...
    activity?.SetStatus(ActivityStatusCode.Error, "Payment declined");
    ...
    throw new PaymentFailedException("card declined");
}
```

- `_random.NextDouble()` returns a number between 0 and 1. If `_failRate = 0.2`, there's a 20% chance the condition is true and the payment "fails" — like rolling a die on every single payment.
- `activity?.SetStatus(ActivityStatusCode.Error, ...)`: **this line matters a lot.** It's what makes the span show up **red** in Jaeger. Without it, the span would look normal even though an exception was thrown, and you'd waste time hunting for the problem yourself instead of Jaeger pointing straight at it.

```csharp
ShopFlowTelemetry.PaymentDuration.Record(
    sw.Elapsed.TotalMilliseconds,
    new KeyValuePair<string, object?>("outcome", "failed"));
```

We record the duration **even when the operation fails**, tagged with `outcome=failed`. That lets you filter in Grafana for just `outcome="failed"` and answer "how long did *failed* payments actually take?" — not just "how many of them failed."

---

## 5. `Services/OrderService.cs` — The Heart of the Trace

```csharp
using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("OrderService.CreateOrder");
```

This is the **parent span**. Every other span (Validate, CheckInventory, `PaymentService.Charge`, SaveOrder) automatically nests **inside** this one in the trace tree — .NET tracks the "current activity" for you, with no manual linking required. This is exactly what produces the tree structure you see in Jaeger:

```
OrderService.CreateOrder
  ├── Validate
  ├── ProductService.GetById
  ├── CheckInventory
  ├── PaymentService.Charge
  └── SaveOrder
```

```csharp
using (ShopFlowTelemetry.ActivitySource.StartActivity("Validate"))
{
    if (request.Quantity <= 0)
        throw new ArgumentException("Quantity must be positive");
}
```

Notice the parenthesized `using (...)` here, instead of `using var`. The difference: with braces, the span closes at the exact end of that block (right after the closing `}`) — not when the whole method finishes. This gives you finer control over exactly when a given step in the trace actually started and ended.

```csharp
try
{
    await _payments.ChargeAsync(request.CustomerId, total, request.PaymentMethod);
}
catch (PaymentFailedException)
{
    ShopFlowTelemetry.OrdersFailed.Add(1, new KeyValuePair<string, object?>("reason", "payment_failed"));
    throw;
}
```

Two things happen here: (1) we record the metric that the order failed *specifically because of payment* (not just "failed" in general, which would be far less useful), and (2) `throw;` — not `throw ex;`. That difference matters a lot: `throw;` preserves the original stack trace, while `throw ex;` erases it, leaving you looking at the line where you wrote `throw ex` instead of where the error actually occurred. A small detail that makes a real difference when you're debugging in production.

```csharp
_logger.LogInformation("Order {OrderId} created successfully for Customer {CustomerId}, Total {Total}",
    order.Id, order.CustomerId, order.Total);
```

Note that we did **not** write:

```csharp
_logger.LogInformation($"Order {order.Id} created for {order.CustomerId}");
```

That's the difference between **structured logging** and plain string interpolation:

- With the approach we used, Serilog keeps `OrderId` and `CustomerId` as **separate fields** inside the log entry (not merged into one blob of text). That means you can later run something like `WHERE OrderId = 456` in a tool like Seq or Loki.
- If you use `$"..."`, everything collapses into one fixed string, and you lose the ability to filter/search precisely. This is exactly the distinction we made earlier between "what should never be logged this way" — the difference between a throwaway log line and one that's actually analyzable.

---

## 6. `Program.cs` — The Wiring Between Everything

### a. Serilog

```csharp
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", ShopFlowTelemetry.ServiceName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}) {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

- `.Enrich.FromLogContext()`: this is what makes `{TraceId}` in the template actually resolve to something. Without it, Serilog has no idea a `TraceId` even exists, and the placeholder would print empty.
- `outputTemplate`: controls the **shape** of each log line as printed to the console. Try changing it and see the difference — for instance, removing `({TraceId})` would mean you can no longer connect a log line back to its trace at a glance.
- `builder.Host.UseSerilog();` (the following line): this tells ASP.NET Core "use Serilog instead of your default logger, everywhere."

### b. OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ShopFlowTelemetry.ServiceName))
    .WithTracing(tracing => { ... })
    .WithMetrics(metrics => { ... });
```

- `.ConfigureResource(r => r.AddService(...))`: stamps an "identity card" on everything this server emits, so that once it reaches Jaeger/Prometheus, you know it specifically came from `ShopFlow.Api` (crucial once you have more than one service).

```csharp
tracing
    .AddSource(ShopFlowTelemetry.ServiceName)
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(o => { o.Endpoint = new Uri(...); });
```

- `.AddSource(...)`: this is exactly where we say "listen for any span produced by our `ActivitySource`." Forget this line, and none of the manual spans (Validate, CheckInventory...) will ever show up in Jaeger — only the automatic ones will.
- `.AddAspNetCoreInstrumentation()`: a ready-made library that automatically creates a span for every incoming HTTP request, with zero extra code from you.
- `.AddHttpClientInstrumentation()`: the same idea, but for any `HttpClient` you use to call a real external API. It's not doing anything right now (since Payment is faked), but if you wired up a real Payment API behind an `HttpClient`, you'd get its span automatically.
- `.AddOtlpExporter(...)`: this is what actually ships every trace to Jaeger, over a protocol called OTLP (OpenTelemetry Protocol), on port 4317.

```csharp
metrics
    .AddMeter(ShopFlowTelemetry.ServiceName)
    .AddAspNetCoreInstrumentation()
    .AddRuntimeInstrumentation()
    .AddPrometheusExporter();
```

- `.AddMeter(...)`: the metrics equivalent of `.AddSource` — without it, `orders.created` and `payment.duration` never appear on `/metrics`.
- `.AddRuntimeInstrumentation()`: gives you .NET runtime metrics for free (GC, threads, memory) — extremely useful during an incident to rule out "is the problem the server itself," exactly as covered in the "CPU 35%, Memory 40%" step.
- `.AddPrometheusExporter()`: opens an endpoint called `/metrics` in a format Prometheus understands directly (no OTLP involved here — Prometheus works by *pulling*, not by being pushed to).

### c. Request-Logging Middleware

```csharp
app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try { await next(); }
    finally
    {
        sw.Stop();
        Log.Information("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms", ...);
    }
});
```

- Why `try/finally` and not `try/catch`? Because we're deliberately *not* trying to swallow the error — we still want it to propagate normally (so ASP.NET Core can, say, return a `500`). We just want to guarantee the log line gets written either way. `finally` runs in both the success and failure cases.
- `await next();`: this continues the pipeline to the remaining middlewares, down to the actual endpoint. Without it, the request would stop right here and never reach the API at all.

### d. Endpoints

```csharp
app.MapPost("/api/orders", async (CreateOrderRequest request, IOrderService orders) =>
{
    try
    {
        var order = await orders.CreateOrderAsync(request);
        return Results.Created($"/api/orders/{order.Id}", order);
    }
    catch (InsufficientStockException ex) => Results.Conflict(new { error = ex.Message });
    ...
});
```

Notice the endpoint itself **has nothing to do with telemetry directly** — all of it lives inside `OrderService`. This matters architecturally: the endpoint's only job is to translate a result/error into the right HTTP status code. Anyone reading the endpoint can understand "what should be returned to the client" without getting lost in tracing details.

---

## 7. `docker-compose.yml` — How Everything Talks to Each Other

```yaml
api:
  build: ./ShopFlow.Api
  ports:
    - "5000:8080"
  environment:
    - Otlp__Endpoint=http://jaeger:4317
```

- `"5000:8080"`: means "port 8080 inside the container (where the API is actually running) is exposed on your machine as port 5000." That's why you open `localhost:5000`, not `localhost:8080`.
- `Otlp__Endpoint`: note the double underscore (`__`). This is .NET's convention for converting `Otlp:Endpoint` (as it would appear in `appsettings.json`) into something valid as an environment-variable name (since `:` isn't allowed in env var names on every OS). .NET converts it back automatically.
- `http://jaeger:4317`: we use the service name (`jaeger`), not `localhost`. Inside a Docker Compose network, every service can reach every other service by name — Docker handles the internal DNS for you.

```yaml
jaeger:
  image: jaegertracing/all-in-one:1.57
  ports:
    - "16686:16686"
    - "4317:4317"
```

`all-in-one` means this single image bundles every Jaeger component (Collector, Query, UI, in-memory storage) into one container — great for learning/demos, but not how you'd run it in a real production setup (there, storage is typically split out to Elasticsearch/Cassandra, for example).

```yaml
prometheus:
  volumes:
    - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
```

We mount our own config file into the container. `:ro` means read-only — the container can read the file but can't modify it.

---

## 8. `prometheus.yml`

```yaml
scrape_configs:
  - job_name: "shopflow-api"
    static_configs:
      - targets: ["api:8080"]
```

Prometheus works on a **pull** model (the opposite of you pushing data to it). Every `scrape_interval` (5 seconds here), Prometheus itself reaches out to `api:8080/metrics` and pulls the values. If the API is down at scrape time, you'll see a gap in the graph — and that gap is, in itself, useful information (the server was down).

---

## 9. `load-test.js` (k6)

```javascript
export const options = {
  vus: 20,
  duration: '30s',
};
```

`vus` = Virtual Users. k6 will simulate 20 users hitting the API **simultaneously**, sustained for 30 seconds. This is what generates enough load to actually see a meaningful difference between the average and the P95/P99.

```javascript
check(orderRes, { 'POST /orders is 201 or handled error': (r) => r.status < 500 });
```

`check` doesn't stop the test on failure — it just records a pass/fail ratio in k6's final report. Here we accept any status below 500 (so even a `409 Conflict` or `402 Payment Required` counts as fine), because we know some scenarios are *meant* to fail (the out-of-stock product). The real failure we care about is `500` — meaning the server itself actually broke.

---

## Summary: What File Do I Touch When...

| I want to... | Go to... |
|---|---|
| Add a new endpoint | `Program.cs` |
| Add a new step to the order flow (e.g. Send Notification) | `OrderService.cs` |
| Add a new metric/counter | `ShopFlowTelemetry.cs`, then use it wherever you need |
| Change incident behavior (slowness/failures) | `docker-compose.yml` → the `api` service's env vars |
| Change how logs look | `Program.cs` → `outputTemplate` |
| Add a real dependency (SQL/Redis) | `ProductService.cs` (swap it for real EF Core) + `docker-compose.yml` (add the service) + health checks in `Program.cs` |
