using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CustomSabersLite.Utilities.Common;
using SabersCore.Models;
using SabersCore.Services;

namespace CustomSabersLite.Services;

internal sealed class SaberDeletionService(ISaberMetadataCache metadataCache, IPrefabCache prefabCache,
    DirectoryManager directories) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private Task<bool>? fileTask;
    private bool disposed;

    public async Task DeleteSaberAsync(string hash)
    {
        await gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        await UnityAsync.SwitchToUnity();
        try
        {
            if (disposed) throw new OperationCanceledException();
            await directories.Ready;
            await UnityAsync.SwitchToUnity();
            CustomSaberMetadata? metadata;
            if (metadataCache is ISaberMetadataSnapshotCache snapshots)
            {
                CustomSaberMetadata[] snapshot;
                try { snapshot = await snapshots.GetRefreshedMetadataAsync(cancellation.Token).ConfigureAwait(false); }
                finally { await UnityAsync.SwitchToUnity(); }
                metadata = snapshot.FirstOrDefault(meta => meta.SaberFile.Hash == hash);
            }
            else metadata = metadataCache.GetOrDefault(hash);
            if (disposed) throw new OperationCanceledException();
            if (metadata == null) return;
            var request = new DeleteRequest(metadata.SaberFile.FileInfo.FullName,
                Path.Combine(directories.DeletedSabers.FullName, metadata.SaberFile.FileInfo.Name));
            var task = Task.Run(() => DeleteFile(request));
            fileTask = task;
            bool moved;
            try { moved = await task.ConfigureAwait(false); }
            finally
            {
                await UnityAsync.SwitchToUnity();
                if (ReferenceEquals(fileTask, task)) fileTask = null;
            }
            if (disposed || !moved) return;
            metadataCache.Remove(hash);
            prefabCache.UnloadPrefab(hash);
        }
        finally
        {
            await UnityAsync.SwitchToUnity();
            gate.Release();
        }
    }

    public void Dispose()
    {
        disposed = true;
        cancellation.Cancel();
        // An accepted file move completes; its app scope alone can publish cache changes.
    }

    private static bool DeleteFile(DeleteRequest request)
    {
        if (!File.Exists(request.Source)) return false;
        if (File.Exists(request.Destination)) File.Delete(request.Destination);
        File.Move(request.Source, request.Destination);
        return true;
    }

    private sealed record DeleteRequest(string Source, string Destination);
}
