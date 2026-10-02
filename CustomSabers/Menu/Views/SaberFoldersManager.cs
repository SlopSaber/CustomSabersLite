using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CustomSabersLite.Services;
using CustomSabersLite.Utilities.Common;
using SabersCore.Models;
using SabersCore.Services;
using Zenject;

namespace CustomSabersLite.Menu.Views;

internal class SaberFoldersManager : IInitializable, IDisposable
{
    private readonly DirectoryManager directoryManager;
    private readonly ISaberMetadataCache saberMetadataCache;
    private readonly ISaberMetadataLoader saberMetadataLoader;
    private readonly List<DirectoryInfo> customSabersSubDirs = [];
    private CancellationTokenSource? cancellation;
    private Task<string[]>? worker;
    private long revision;
    private bool disposed;

    public SaberFoldersManager(DirectoryManager directoryManager, ISaberMetadataCache saberMetadataCache,
        ISaberMetadataLoader saberMetadataLoader)
    {
        this.directoryManager = directoryManager;
        this.saberMetadataCache = saberMetadataCache;
        this.saberMetadataLoader = saberMetadataLoader;
        CurrentDirectory = directoryManager.CustomSabers;
    }

    public Task Ready { get; private set; } = Task.CompletedTask;
    public DirectoryInfo CurrentDirectory { get; set; }
    public DirectoryInfo ParentDirectory => CurrentDirectory.Parent ?? directoryManager.CustomSabers;
    public bool InTopDirectory => CurrentDirectory.FullName == directoryManager.CustomSabers.FullName;
    public IEnumerable<DirectoryInfo> CurrentDirectorySubDirectories =>
        customSabersSubDirs.Where(dir => dir.Parent?.FullName == CurrentDirectory.FullName);

    public void Initialize()
    {
        saberMetadataLoader.LoadingProgressChanged += LoadingProgressChanged;
        Refresh();
    }

    public void Dispose()
    {
        disposed = true;
        ++revision;
        cancellation?.Cancel();
        saberMetadataLoader.LoadingProgressChanged -= LoadingProgressChanged;
    }

    public void Refresh()
    {
        if (disposed) return;
        cancellation?.Cancel();
        Ready = RefreshAsync(++revision);
    }

    private async Task RefreshAsync(long requestRevision)
    {
        await directoryManager.Ready;
        await UnityAsync.SwitchToUnity();
        CheckCurrent();
        while (worker != null)
        {
            var previous = worker;
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            await UnityAsync.SwitchToUnity();
            if (ReferenceEquals(worker, previous)) worker = null;
            CheckCurrent();
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            CustomSaberMetadata[] metadata;
            try
            {
                metadata = saberMetadataCache is ISaberMetadataSnapshotCache snapshots
                    ? await snapshots.GetRefreshedMetadataAsync(source.Token).ConfigureAwait(false)
                    : saberMetadataCache.GetRefreshedMetadata().ToArray();
            }
            finally { await UnityAsync.SwitchToUnity(); }
            CheckCurrent();
            var request = new FolderRequest(metadata.Select(meta => meta.SaberFile.FileInfo.DirectoryName).ToArray(),
                directoryManager.CustomSabers.FullName, source.Token);
            var task = Task.Run(() => PrepareFolders(request), source.Token);
            worker = task;
            string[] paths;
            try { paths = await task.ConfigureAwait(false); }
            finally
            {
                await UnityAsync.SwitchToUnity();
                if (ReferenceEquals(worker, task)) worker = null;
            }
            CheckCurrent();
            customSabersSubDirs.Clear();
            customSabersSubDirs.AddRange(paths.Select(path => new DirectoryInfo(path)));
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            source.Dispose();
        }

        void CheckCurrent()
        {
            if (disposed || requestRevision != revision) throw new OperationCanceledException();
        }
    }

    private static string[] PrepareFolders(FolderRequest request)
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (string? path in request.Paths)
        {
            request.Token.ThrowIfCancellationRequested();
            var directory = path == null ? null : new DirectoryInfo(path);
            while (directory != null && directory.FullName != request.Root)
            {
                request.Token.ThrowIfCancellationRequested();
                if (distinct.Add(directory.FullName)) paths.Add(directory.FullName);
                directory = directory.Parent;
            }
        }
        return paths.ToArray();
    }

    private void LoadingProgressChanged(MetadataLoaderProgress progress)
    {
        if (progress.Completed) Refresh();
    }

    private sealed record FolderRequest(string?[] Paths, string Root, CancellationToken Token);
}
