using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Queue;

public sealed class CsfdFetchQueue : IAsyncDisposable
{
    private readonly Channel<CsfdFetchRequest> _channel = Channel.CreateUnbounded<CsfdFetchRequest>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly ICsfdFetchProcessor _processor;
    private readonly ILogger<CsfdFetchQueue> _logger;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _started;
    private int _count;

    public int Count => _count;

    public CsfdFetchQueue(ICsfdFetchProcessor processor, ILogger<CsfdFetchQueue> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
        _logger.LogInformation("CSFD fetch queue started");
    }

    public bool Enqueue(CsfdFetchRequest request)
    {
        if (!_started)
        {
            Start();
        }

        if (_channel.Writer.TryWrite(request))
        {
            Interlocked.Increment(ref _count);
            return true;
        }
        return false;
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var request))
            {
                Interlocked.Decrement(ref _count);
                try
                {
                    var result = await _processor.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error processing CSFD fetch for {ItemId}", request.ItemId);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
        {
            return;
        }

        _channel.Writer.TryComplete();
        cts.Cancel();
        try
        {
            if (_worker != null)
            {
                await _worker.ConfigureAwait(false);
                _worker = null;
            }
        }
        catch (OperationCanceledException)
        {
            // expected when stopping
        }
        finally
        {
            cts.Dispose();
            _started = false;
        }
    }
}
