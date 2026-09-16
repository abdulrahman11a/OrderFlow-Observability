using ShopFlow.Api.Models;
using ShopFlow.Api.Telemetry;
using System.Diagnostics;

namespace ShopFlow.Api.Services;

public interface IOrderService
{
    Task<Order> CreateOrderAsync(CreateOrderRequest request);
}

// POST /api/orders -> Validate -> GetProduct -> CheckInventory -> ProcessPayment -> SaveOrder
// Every step below gets its own span AND its own structured log line.
// This is deliberately the "long chain" from the whiteboard diagrams — it's what makes
// Jaeger's waterfall view actually mean something to the students.
public class OrderService : IOrderService
{
    private readonly IProductService _products;
    private readonly IPaymentService _payments;
    private readonly ILogger<OrderService> _logger;
    private static int _nextOrderId = 1000;

    public OrderService(IProductService products, IPaymentService payments, ILogger<OrderService> logger)
    {
        _products = products;
        _payments = payments;
        _logger = logger;
    }

    public async Task<Order> CreateOrderAsync(CreateOrderRequest request)
    {
        using var activity =
            ShopFlowTelemetry.ActivitySource.StartActivity(
                "OrderService.CreateOrder",
                ActivityKind.Internal);

        activity?.SetTag("product.id", request.ProductId);

        _logger.LogInformation("Creating order for Customer {CustomerId}, Product {ProductId}, Qty {Quantity}",
            request.CustomerId, request.ProductId, request.Quantity);

        // 1. Validate
        using (ShopFlowTelemetry.ActivitySource.StartActivity("Validate"))
        {
            if (request.Quantity <= 0)
                throw new ArgumentException("Quantity must be positive");
        }

        // 2. Get Product
        Product product;

        using (ShopFlowTelemetry.ActivitySource.StartActivity("GetProduct"))
        {
            product = _products.GetById(request.ProductId)
                ?? throw new KeyNotFoundException(
                    $"Product {request.ProductId} not found");
        }


        // 3. Check Inventory
        using (ShopFlowTelemetry.ActivitySource.StartActivity("CheckInventory"))
        {
            if (product.Stock < request.Quantity)
            {
                ShopFlowTelemetry.OrdersFailed.Add(1, new KeyValuePair<string, object?>("reason", "out_of_stock"));
                _logger.LogWarning("Order rejected: Product {ProductId} out of stock (have {Stock}, need {Needed})",
                    product.Id, product.Stock, request.Quantity);
                throw new InsufficientStockException(product.Id);
            }
        }

        // 4. Process Payment  <-- this is the step that will "blow up" during the incident demo
        var total = product.Price * request.Quantity;
        using var paymentActivity =
            ShopFlowTelemetry.ActivitySource.StartActivity(
                "ProcessPayment",
                ActivityKind.Internal);

        paymentActivity?.SetTag("payment.method", request.PaymentMethod);
        paymentActivity?.SetTag("payment.amount", total);

        try
        {
            await _payments.ChargeAsync(
                request.CustomerId,
                total,
                request.PaymentMethod);
        }
        catch (PaymentFailedException ex)
        {
            paymentActivity?.SetStatus(
                ActivityStatusCode.Error,
                ex.Message);

            ShopFlowTelemetry.OrdersFailed.Add(
                1,
                new KeyValuePair<string, object?>(
                    "reason",
                    "payment_failed"));

            throw;
        }

        // 5. Save Order
        Order order;
        using (ShopFlowTelemetry.ActivitySource.StartActivity("SaveOrder"))
        {
            _products.ReduceStock(product.Id, request.Quantity);
            order = new Order(_nextOrderId++, request.CustomerId, product.Id, request.Quantity, total, "Created");
        }

        ShopFlowTelemetry.OrdersCreated.Add(1, new KeyValuePair<string, object?>("payment_method", request.PaymentMethod));
        _logger.LogInformation("Order {OrderId} created successfully for Customer {CustomerId}, Total {Total}",
            order.Id, order.CustomerId, order.Total);

        return order;
    }
}
