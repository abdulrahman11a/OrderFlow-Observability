using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ShopFlow.Api.Telemetry;

// This is the "hand-written" telemetry layer.
// OpenTelemetry auto-instrumentation (AspNetCore/Http) gives you traces & metrics for free,
// but the manual spans/metrics here are what let a student SEE their own business steps
// (Validate -> GetProduct -> CheckInventory -> ProcessPayment -> SaveOrder) inside Jaeger.
public static class ShopFlowTelemetry
{
    public const string ServiceName = "ShopFlow.Api";

    // ActivitySource = "where custom spans/traces come from"
    public static readonly ActivitySource ActivitySource = new(ServiceName);

    // Meter = "where custom metrics come from"
    public static readonly Meter Meter = new(ServiceName);

    public static readonly Counter<long> OrdersCreated =
        Meter.CreateCounter<long>("orders.created", description: "Number of orders successfully created");

    public static readonly Counter<long> OrdersFailed =
        Meter.CreateCounter<long>("orders.failed", description: "Number of orders that failed (stock/payment)");

    public static readonly Histogram<double> PaymentDuration =
        Meter.CreateHistogram<double>("payment.duration", unit: "ms", description: "Time spent calling the Payment dependency");


    #region Custom Business Metrics

    // OpenTelemetry already measures technical operations automatically.
    // We create a custom metric when we want to measure a business operation
    // as a separate metric and analyze it across many requests.
    //
    // Example:
    // ProcessPayment() may contain multiple internal steps.
    // We can measure the total business operation:
    //
    // ProcessPayment → 900ms
    //
    // This allows us to monitor:
    // P50, P95, P99
    // and detect when the payment process becomes slower.
    //
    // If Payment is only a single HTTP call,
    // AddHttpClientInstrumentation() already measures its duration,
    // so this custom metric may not be necessary.
    //
    // If we want to know what happened INSIDE the operation
    // and how long each step took, we use Tracing instead:
    //
    // ProcessPayment 900ms
    // │
    // ├── ValidateCard      20ms
    // ├── CheckBalance      50ms
    // ├── PaymentGateway   700ms
    // ├── SavePayment       80ms
    // └── PublishEvent      30ms
    //
    // Tracing  → What happened inside the operation?
    // Metrics  → How is the operation performing across many requests?

    #endregion
}
