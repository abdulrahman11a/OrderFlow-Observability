using ShopFlow.Api.Models;
using ShopFlow.Api.Telemetry;

namespace ShopFlow.Api.Services;

public interface IProductService
{
    List<Product> GetAll();
    Product? GetById(int id);
    void ReduceStock(int id, int quantity);
}

// In-memory "database" so the demo runs with zero external dependencies to set up.
// Swap this for real EF Core + SQL Server later and OpenTelemetry.Instrumentation.EntityFrameworkCore
// will show the real SQL spans in Jaeger automatically.
public class ProductService : IProductService
{
    private readonly List<Product> _products = new()
    {
        new Product(1, "Wireless Mouse", 250, 40),
        new Product(2, "Mechanical Keyboard", 950, 15),
        new Product(3, "USB-C Hub", 400, 0), // intentionally out of stock, for the failure demo
    };

    private readonly ILogger<ProductService> _logger;

    public ProductService(ILogger<ProductService> logger) => _logger = logger;

    public List<Product> GetAll()
    {
        using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("ProductService.GetAll");
        // Simulate a tiny, realistic DB round trip
        Thread.Sleep(15);
        return _products;
    }

    public Product? GetById(int id)
    {
        using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("ProductService.GetById");
        activity?.SetTag("product.id", id);
        Thread.Sleep(10);
        var product = _products.FirstOrDefault(p => p.Id == id);
        _logger.LogInformation("Fetched product {ProductId}, found={Found}", id, product is not null);
        return product;
    }

    public void ReduceStock(int id, int quantity)
    {
        using var activity = ShopFlowTelemetry.ActivitySource.StartActivity("ProductService.ReduceStock");
        var index = _products.FindIndex(p => p.Id == id);
        if (index == -1) return;

        var p = _products[index];
        _products[index] = p with { Stock = p.Stock - quantity };
    }
}
