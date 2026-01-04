using PriceWatcher.Domain.Models;

namespace PriceWatcher.Domain.Interfaces;

public interface IProductSource
{
    string SourceKey { get; }
    bool CanHandle(string url);
    Task<ProductSnapshot> FetchAsync(string url, CancellationToken ct = default);
}
