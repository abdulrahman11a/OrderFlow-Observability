namespace ShopFlow.Api.Models;

public record Product(int Id, string Name, decimal Price, int Stock);

public record CreateOrderRequest(int CustomerId, int ProductId, int Quantity, string PaymentMethod);

public record Order(int Id, int CustomerId, int ProductId, int Quantity, decimal Total, string Status);

public class InsufficientStockException(int productId) : Exception($"Product {productId} is out of stock");

public class PaymentFailedException(string reason) : Exception($"Payment failed: {reason}");
