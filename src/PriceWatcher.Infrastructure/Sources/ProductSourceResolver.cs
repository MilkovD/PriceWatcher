using PriceWatcher.Domain.Interfaces;
using static System.Web.HttpUtility;

namespace PriceWatcher.Infrastructure.Sources;

public class ProductSourceResolver(IEnumerable<IProductSource> sources) : IProductSourceResolver
{
    private readonly IProductSource[] _sources = sources.ToArray();

    public IProductSource? TryResolve(string url)
    {
        return _sources.FirstOrDefault(s => s.CanHandle(url));
    }

    public string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        // Remove common tracking parameters
        var query = ParseQueryString(uri.Query);
        var keysToRemove = query.AllKeys
            .Where(k => k != null && (
                k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("from", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("ref", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("fbclid", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("gclid", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var key in keysToRemove)
        {
            query.Remove(key);
        }

        builder.Query = query.ToString();
        return builder.Uri.ToString().TrimEnd('/');
    }
}
