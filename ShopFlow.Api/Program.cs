using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using ShopFlow.Api.Models;
using ShopFlow.Api.Services;
using ShopFlow.Api.Telemetry;

// ---------- 1) Serilog: structured console logs, enriched with TraceId/SpanId ----------
// The TraceId here is what lets you jump from "a weird log line" straight to
// the matching waterfall in Jaeger. That link is the whole point of Correlation.
// Loki URL comes from an env var so this works both in docker-compose (http://loki:3100)
// and locally without Loki running at all (falls back to Console-only).
var lokiUrl = Environment.GetEnvironmentVariable("LOKI_URL");

var loggerConfig = new LoggerConfiguration()
    #region Log Context

// Adds properties from LogContext to every log event.
// Example: OrderId = 123

/// Adds properties from LogContext to every log event.
/// Example: OrderId = 123
///
/// </summary>  .Enrich.FromLogContext()

#endregion
   
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", ShopFlowTelemetry.ServiceName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] ({TraceId}) {Message:lj}{NewLine}{Exception}");

if (!string.IsNullOrWhiteSpace(lokiUrl))
{
    // Only "app" is a Loki label (low-cardinality, on purpose).
    // TraceId/CustomerId/OrderId stay as structured fields inside the log line
    // and get queried in Grafana with:  {app="shopflow-api"} | json | TraceId="..."
    loggerConfig = loggerConfig.WriteTo.GrafanaLoki(
        lokiUrl,
        labels: new[] { new LokiLabel { Key = "app", Value = ShopFlowTelemetry.ServiceName } });
}

Log.Logger = loggerConfig.CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

#region Dependency Injection
builder.Services.AddSingleton<IProductService, ProductService>();
builder.Services.AddSingleton<IPaymentService, PaymentService>();
builder.Services.AddScoped<IOrderService, OrderService>();
#endregion

// ---------- 3) OpenTelemetry: one pipeline, three signals ----------
// Traces  -> exported over OTLP to Jaeger (localhost:4317 inside docker-compose network)
// Metrics -> exposed on /metrics for Prometheus to scrape, AND exported to Jaeger's demo collector
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(ShopFlowTelemetry.ServiceName))
    .WithTracing(tracing =>
    {
        tracing
              #region Sampling
             
             // Keep all traces.
             // In production, sampling policies can keep important traces
             // (e.g. errors/slow requests) while reducing normal traffic.
             //
             // Sampler -> Which traces do we keep?
        #endregion

             .SetSampler(new AlwaysOnSampler())
            // Listen to our custom ActivitySource
            .AddSource(ShopFlowTelemetry.ServiceName)      // our manual spans (Validate, CheckInventory, ...)
            .AddAspNetCoreInstrumentation()                 // automatic: incoming HTTP requests
            #region HTTP Client Instrumentation

          // Automatically creates spans for outgoing HTTP calls.
          // No need to create StartActivity() manually for each HttpClient request.
          //
          // Example:
          // ShopFlow API → Payment API
          //
          // OpenTelemetry tracks the outgoing request automatically.
        #endregion
            .AddHttpClientInstrumentation()                 // automatic: outgoing HTTP calls (real Payment API later)
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"]);
            });
    })

    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(ShopFlowTelemetry.ServiceName)        // our business metrics (orders.created, payment.duration)
            .AddAspNetCoreInstrumentation()                 // request count/duration, automatically
            .AddRuntimeInstrumentation()                     // GC, threadpool, memory
            .AddPrometheusExporter();                        // exposes GET /metrics
    });

// ---------- 4) Health checks: liveness vs readiness ----------
builder.Services.AddHealthChecks();

var app = builder.Build();

// ---------- 5) Request logging middleware: gives every request a visible duration + status ----------
#region Custom Application Logging

// OpenTelemetry already collects standard HTTP telemetry.
// We use custom logging when we need application-specific context.
//
// Example:
// A multi-tenant application may need to know:
// UserId        → Who made the request?
// TenantId      → Which company/tenant?
// CorrelationId → Which business workflow?
// RequestId     → Which specific request?
//
// This information can then be sent to Loki for searching and debugging.

#endregion

app.Use(async (context, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        sw.Stop();
        Log.Information("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.ElapsedMilliseconds);
    }
});

app.MapPrometheusScrapingEndpoint(); // GET /metrics

app.MapHealthChecks("/health");      // GET /health

// ---------- 6) The API itself ----------
app.MapGet("/api/products", (IProductService products) => products.GetAll());

app.MapGet("/api/products/{id:int}", (int id, IProductService products) =>
    products.GetById(id) is { } product ? Results.Ok(product) : Results.NotFound());

app.MapPost("/api/orders", async (CreateOrderRequest request, IOrderService orders) =>
{
    try
    {
        var order = await orders.CreateOrderAsync(request);
        return Results.Created($"/api/orders/{order.Id}", order);
    }
    catch (InsufficientStockException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (PaymentFailedException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status402PaymentRequired);
    }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---------- 7) 5xx Error Demo Endpoints ----------

// 500 - Unhandled exception
app.MapGet("/api/debug/500", () =>
{
    throw new Exception("Simulated internal server error");
});

// 500 - NullReferenceException
app.MapGet("/api/debug/null-reference", () =>
{
    Product? product = null;

    return product!.Name;
});

// 500 - InvalidOperationException
app.MapGet("/api/debug/invalid-operation", () =>
{
    throw new InvalidOperationException(
        "Simulated invalid operation inside the application");
});

// 500 - Database-like failure
app.MapGet("/api/debug/database", () =>
{
    throw new Exception(
        "Database connection failed: simulated database failure");
});

// 503 - Service Unavailable
app.MapGet("/api/debug/503", () =>
{
    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

// 504 - Gateway Timeout
app.MapGet("/api/debug/504", () =>
{
    return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
});

app.MapGet("/api/debug/slow", async () =>
{
    await Task.Delay(500);

    return Results.Ok(new
    {
        message = "Slow response"
    });
});

app.Run();
