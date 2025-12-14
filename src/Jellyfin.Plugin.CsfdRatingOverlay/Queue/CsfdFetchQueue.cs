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

        return _channel.Writer.TryWrite(request);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var request))
            {
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
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            if (_worker != null)
            {
                await _worker.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected when stopping
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
