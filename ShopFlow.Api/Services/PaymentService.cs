using System.Diagnostics;
using ShopFlow.Api.Models;
using ShopFlow.Api.Telemetry;

namespace ShopFlow.Api.Services;

public interface IPaymentService
{
    Task ChargeAsync(int customerId, decimal amount, string method);
}

// This class is the "external Payment API" from the diagrams.
// Its latency and failure rate are controlled by env vars so you can turn a healthy
// system into a Production Incident live, without touching code:
//
//   SHOPFLOW_PAYMENT_DELAY_MS=2500   -> every payment call takes ~2.5s   (latency incident)
//   SHOPFLOW_PAYMENT_FAIL_RATE=0.2   -> 20% of payments throw            (error-rate incident)
public class PaymentService : IPaymentService
{
    private readonly ILogger<PaymentService> _logger;
    private readonly int _delayMs;
    private readonly double _failRate;
    private static readonly Random _random = new();

    public PaymentService(ILogger<PaymentService> logger, IConfiguration config)
    {
        _logger = logger;
        _delayMs = int.TryParse(Environment.GetEnvironmentVariable("SHOPFLOW_PAYMENT_DELAY_MS"), out var d) ? d : 150;
        _failRate = double.TryParse(Environment.GetEnvironmentVariable("SHOPFLOW_PAYMENT_FAIL_RATE"), out var f) ? f : 0.0;
    }

    public async Task ChargeAsync(int customerId, decimal amount, string method)
    {
        using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("PaymentService.Charge", ActivityKind.Client);
        activity?.SetTag("payment.method", method);
        activity?.SetTag("payment.amount", amount);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Calling Payment API for customer {CustomerId}, amount {Amount}", customerId, amount);

        await Task.Delay(_delayMs); // this line IS the "External Payment API" in the diagrams

        if (_random.NextDouble() < _failRate)
        {
            sw.Stop();
            ShopFlowTelemetry.PaymentDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", "failed"));
            activity?.SetStatus(ActivityStatusCode.Error, "Payment declined");
            _logger.LogWarning("Payment declined for customer {CustomerId} after {ElapsedMs}ms", customerId, sw.ElapsedMilliseconds);
            throw new PaymentFailedException("card declined");
        }

        sw.Stop();
        ShopFlowTelemetry.PaymentDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", "success"));
        _logger.LogInformation("Payment succeeded for customer {CustomerId} in {ElapsedMs}ms", customerId, sw.ElapsedMilliseconds);
    }
}
