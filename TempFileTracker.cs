using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyBoiUploadFile;

/// <summary>
/// Tracks in-flight upload temp files so the startup cleaner never deletes an active transfer,
/// and coordinates concurrent or restarting requests for the same destination part file.
/// </summary>
internal static class TempFileTracker
{
    private sealed class ActiveTransfer : IDisposable
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public CancellationTokenSource? Cts { get; set; }

        public void Dispose()
        {
            Lock.Dispose();
            Cts?.Dispose();
        }
    }

    private static readonly ConcurrentDictionary<string, ActiveTransfer> Transfers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Acquires exclusive coordination for the given part path. If a prior transfer is still active
    /// for this path, cancels that prior transfer and waits for its stream to be released.
    /// Returns an IDisposable handle that must be disposed when the transfer finishes.
    /// </summary>
    public static async Task<IDisposable> AcquireTransferLockAsync(string path, CancellationToken incomingToken)
    {
        var transfer = Transfers.GetOrAdd(path, _ => new ActiveTransfer());

        // If another request is currently running for this file, signal it to cancel and release its file handle.
        try
        {
            transfer.Cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        // Wait to acquire exclusive access
        await transfer.Lock.WaitAsync(incomingToken).ConfigureAwait(false);

        // Reset the CTS for this new transfer
        transfer.Cts = CancellationTokenSource.CreateLinkedTokenSource(incomingToken);

        return new TransferScope(path, transfer);
    }

    /// <summary>
    /// Gets the cancellation token for the active transfer on the specified path, linked with incomingToken.
    /// </summary>
    public static CancellationToken GetTransferToken(string path, CancellationToken fallbackToken)
    {
        if (Transfers.TryGetValue(path, out var transfer) && transfer.Cts != null)
        {
            return transfer.Cts.Token;
        }
        return fallbackToken;
    }

    /// <summary>
    /// Returns whether the given temp file is currently being transferred.
    /// </summary>
    public static bool IsActive(string path) => Transfers.ContainsKey(path);

    private sealed class TransferScope : IDisposable
    {
        private readonly string _path;
        private readonly ActiveTransfer _transfer;
        private int _disposed;

        public TransferScope(string path, ActiveTransfer transfer)
        {
            _path = path;
            _transfer = transfer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _transfer.Cts?.Dispose();
                    _transfer.Cts = null;
                }
                catch { }

                try
                {
                    _transfer.Lock.Release();
                }
                catch { }

                // If no other request is waiting, we can clean up the dictionary entry
                if (_transfer.Lock.CurrentCount > 0)
                {
                    Transfers.TryRemove(_path, out _);
                }
            }
        }
    }
}
