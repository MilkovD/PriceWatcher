namespace PriceWatcher.Domain.Interfaces;

public interface IProductSourceResolver
{
    IProductSource? TryResolve(string url);
    string NormalizeUrl(string url);
}
