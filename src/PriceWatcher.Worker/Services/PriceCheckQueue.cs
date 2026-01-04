using System.Threading.Channels;

namespace PriceWatcher.Worker.Services;

public class PriceCheckQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    public int QueueLength => _channel.Reader.Count;

    public void Enqueue(int itemId)
    {
        _channel.Writer.TryWrite(itemId);
    }

    public void EnqueueRange(IEnumerable<int> itemIds)
    {
        foreach (var id in itemIds)
        {
            _channel.Writer.TryWrite(id);
        }
    }

    public async Task<int> DequeueAsync(CancellationToken ct)
    {
        return await _channel.Reader.ReadAsync(ct);
    }

    public bool TryDequeue(out int itemId)
    {
        return _channel.Reader.TryRead(out itemId);
    }
}
