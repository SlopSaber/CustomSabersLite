using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CustomSabersLite.Menu.Views;
using CustomSabersLite.Models;
using CustomSabersLite.Services;
using CustomSabersLite.Utilities.Common;
using CustomSabersLite.Utilities.Extensions;
using SabersCore.Models;
using SabersCore.Services;
using Zenject;
using static CustomSabersLite.Utilities.Common.PluginResources;

namespace CustomSabersLite.Menu;

internal class SaberListManager : IInitializable, IDisposable
{
    private readonly ISaberMetadataCache saberMetadataCache;
    private readonly ISaberMetadataLoader metadataLoader;
    private readonly DirectoryManager directoryManager;
    private readonly SaberFoldersManager saberFoldersManager;
    private readonly FavouritesManager favouritesManager;
    private readonly SaberDeletionService deletionService;
    private readonly List<IListCellInfo> sortedList = [];
    private readonly Dictionary<SaberValue, int> sortedListIndexMap = [];
    private readonly List<IListCellInfo> unsortedList = [];
    private readonly Dictionary<SaberValue, int> unsortedListIndexMap = [];
    private readonly PreparationSlot sortedPreparation = new();
    private readonly PreparationSlot unsortedPreparation = new();
    private CustomSaberMetadata[]? metadataSnapshot;
    private bool showFavourites;
    private bool disposed;
    private long metadataRevision;

    private IListCellInfo[] SaberListDefaultChoices { get; } =
    [
        new ListInfoCellInfo("Default", "Beat Games", DefaultCoverImage, new DefaultSaberValue())
    ];
    private IListCellInfo[] TrailListDefaultChoices { get; } =
    [
        new ListInfoCellInfo("Custom", "Use the trail of the selected saber", CustomTrailIcon, new CustomTrailValue()),
        new ListInfoCellInfo("None", "Use no trail", NoTrailIcon, new NoTrailValue()),
        new ListInfoCellInfo("Default", "Beat Games", DefaultCoverImage, new DefaultSaberValue())
    ];

    public SaberListManager(ISaberMetadataCache saberMetadataCache, ISaberMetadataLoader metadataLoader,
        DirectoryManager directoryManager, SaberFoldersManager saberFoldersManager,
        FavouritesManager favouritesManager, SaberDeletionService deletionService)
    {
        this.saberMetadataCache = saberMetadataCache;
        this.metadataLoader = metadataLoader;
        this.directoryManager = directoryManager;
        this.saberFoldersManager = saberFoldersManager;
        this.favouritesManager = favouritesManager;
        this.deletionService = deletionService;
    }

    public void Initialize() => metadataLoader.LoadingProgressChanged += LoadingProgressChanged;

    public void Dispose()
    {
        disposed = true;
        metadataLoader.LoadingProgressChanged -= LoadingProgressChanged;
        InvalidatePreparation();
    }

    public bool ShowFavourites
    {
        get => showFavourites;
        set { showFavourites = value; InvalidatePreparation(); }
    }

    public void Refresh()
    {
        RefreshMetadata();
        sortedList.Clear();
        OpenFolder(directoryManager.CustomSabers);
    }

    public void RefreshMetadata()
    {
        metadataSnapshot = null;
        ++metadataRevision;
    }

    private void LoadingProgressChanged(MetadataLoaderProgress progress)
    {
        if (progress.Completed) RefreshMetadata();
    }

    public Task<IListCellInfo[]> UpdateListAsync(SaberListFilterOptions options, CancellationToken token) =>
        PopulateList(sortedPreparation, options, token);
    public Task<IListCellInfo[]> UpdateUnsortedListAsync(CancellationToken token) =>
        PopulateList(unsortedPreparation, SaberListFilterOptions.Default, token);

    public void PublishSorted(IListCellInfo[] cells) => PublishList(sortedList, sortedListIndexMap, cells);
    public void PublishUnsorted(IListCellInfo[] cells) => PublishList(unsortedList, unsortedListIndexMap, cells);

    private void PublishList(List<IListCellInfo> list, Dictionary<SaberValue, int> indexMap, IListCellInfo[] cells)
    {
        list.Clear();
        indexMap.Clear();
        list.AddRange(cells);
        list.ForEach((cell, index) =>
        {
            if (!cell.TryGetSaberValue(out var value)) return;
            indexMap.Add(value, index);
            if (cell is ListInfoCellInfo info && value.TryGetSaberHash(out var hash)
                && saberMetadataCache.TryGetMetadata(hash.Hash, out var metadata))
                info.IsFavourite = favouritesManager.IsFavourite(metadata.SaberFile);
        });
    }

    public void OpenFolder(DirectoryInfo directory)
    {
        saberFoldersManager.CurrentDirectory = directory;
        ShowFavourites = false;
    }

    public async Task DeleteSaberAsync(string hash)
    {
        await deletionService.DeleteSaberAsync(hash);
        await UnityAsync.SwitchToUnity();
        if (!disposed) RefreshMetadata();
    }

    public bool TrySelectSorted(int row, [NotNullWhen(true)] out IListCellInfo? cell) => TrySelect(sortedList, row, out cell);
    public bool TrySelectUnsorted(int row, [NotNullWhen(true)] out IListCellInfo? cell) => TrySelect(unsortedList, row, out cell);
    public int IndexForSaberValue(SaberValue value) => sortedListIndexMap.TryGetValue(value, out var index)
        ? index : sortedListIndexMap.GetValueOrDefault(new DefaultSaberValue(), 0);
    public int IndexForSaberValueUnsorted(SaberValue value) => unsortedListIndexMap.TryGetValue(value, out var index)
        ? index : unsortedListIndexMap.GetValueOrDefault(new DefaultSaberValue(), 0);
    public bool CurrentListContains(SaberValue value) => sortedListIndexMap.ContainsKey(value);
    public bool UnsortedListContains(SaberValue value) => unsortedListIndexMap.ContainsKey(value);

    private async Task<IListCellInfo[]> PopulateList(PreparationSlot slot, SaberListFilterOptions options, CancellationToken token)
    {
        long revision = ++slot.Revision;
        slot.Cancellation?.Cancel();
        await favouritesManager.Ready;
        await UnityAsync.SwitchToUnity();
        CheckCurrent();
        await saberFoldersManager.Ready;
        await UnityAsync.SwitchToUnity();
        CheckCurrent();

        // Cancellation retires a result; retain the physical slot until it finishes.
        while (slot.Task != null)
        {
            var previous = slot.Task;
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            await UnityAsync.SwitchToUnity();
            if (ReferenceEquals(slot.Task, previous)) slot.Task = null;
            CheckCurrent();
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        slot.Cancellation = cancellation;
        try
        {
            long cacheRevision = metadataRevision;
            var metadata = metadataSnapshot;
            if (metadata == null)
            {
                try
                {
                    metadata = saberMetadataCache is ISaberMetadataSnapshotCache snapshots
                        ? await snapshots.GetRefreshedMetadataAsync(cancellation.Token).ConfigureAwait(false)
                        : saberMetadataCache.GetRefreshedMetadata().ToArray();
                }
                finally { await UnityAsync.SwitchToUnity(); }
                CheckCurrent();
                if (cacheRevision != metadataRevision) throw new OperationCanceledException();
                metadataSnapshot = metadata;
            }

            bool favourites = ShowFavourites;
            string folder = saberFoldersManager.CurrentDirectory.FullName;
            var prefix = new List<IListCellInfo>();
            if (favourites) prefix.Add(new ListUpDirectoryCellInfo(directoryManager.CustomSabers));
            else
            {
                bool top = saberFoldersManager.InTopDirectory;
                prefix.Add(top ? new ListFavouritesCellInfo() : new ListUpDirectoryCellInfo(saberFoldersManager.ParentDirectory));
                prefix.AddRange(saberFoldersManager.CurrentDirectorySubDirectories.Select(dir => new ListDirectoryCellInfo(dir)));
                if (top) prefix.AddRange(options.Trails ? TrailListDefaultChoices : SaberListDefaultChoices);
            }

            while (true)
            {
                CheckCurrent();
                long favouriteRevision = favouritesManager.Revision;
                var rows = metadata.Select((meta, index) => new CatalogRow(index,
                    meta.Descriptor.SaberName.FullName, meta.Descriptor.AuthorName.FullName,
                    meta.SaberFile.DateAdded, meta.SaberFile.FileInfo.DirectoryName,
                    meta.HasTrails, favouritesManager.IsFavourite(meta.SaberFile))).ToArray();
                var request = new CatalogRequest(rows, options with { Favourites = options.Favourites || favourites },
                    favourites ? null : folder, CultureInfo.CurrentCulture.CompareInfo, cancellation.Token);
                var task = Task.Run(() => PrepareCatalog(request), cancellation.Token);
                slot.Task = task;
                int[] indices;
                try { indices = await task.ConfigureAwait(false); }
                finally
                {
                    await UnityAsync.SwitchToUnity();
                    if (ReferenceEquals(slot.Task, task)) slot.Task = null;
                }
                CheckCurrent();
                if (cacheRevision != metadataRevision) throw new OperationCanceledException();
                if (request.Options.Favourites && favouriteRevision != favouritesManager.Revision) continue;

                var list = new List<IListCellInfo>();
                list.AddRange(prefix);
                foreach (int index in indices)
                {
                    var meta = metadata[index];
                    list.Add(new ListInfoCellInfo(meta, favouritesManager.IsFavourite(meta.SaberFile)));
                }
                return list.ToArray();
            }
        }
        finally
        {
            if (ReferenceEquals(slot.Cancellation, cancellation)) slot.Cancellation = null;
            cancellation.Dispose();
        }

        void CheckCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (disposed || revision != slot.Revision) throw new OperationCanceledException();
        }
    }

    private static int[] PrepareCatalog(CatalogRequest request)
    {
        request.Token.ThrowIfCancellationRequested();
        var data = request.Rows.Select(row =>
        {
            request.Token.ThrowIfCancellationRequested();
            return (Row: row,
                Name: Utilities.Common.RegularExpressions.RichTextRegex.Replace(row.Name.Trim(), string.Empty),
                Author: Utilities.Common.RegularExpressions.RichTextRegex.Replace(row.Author.Trim(), string.Empty));
        }).Where(item => (!request.Options.Trails || item.Row.HasTrails)
            && (!request.Options.Favourites || item.Row.Favourite)
            && (request.Folder == null || item.Row.Directory == request.Folder)
            && (string.IsNullOrWhiteSpace(request.Options.SearchFilter)
                || request.CompareInfo.IndexOf(item.Name, request.Options.SearchFilter, CompareOptions.IgnoreCase) >= 0
                || request.CompareInfo.IndexOf(item.Author, request.Options.SearchFilter, CompareOptions.IgnoreCase) >= 0));
        data = request.Options.OrderBy switch
        {
            OrderBy.Name => data.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Author, StringComparer.Ordinal),
            OrderBy.Author => data.OrderBy(item => item.Author, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal),
            OrderBy.RecentlyAdded => data.OrderBy(item => item.Row.DateAdded).ThenBy(item => item.Name, StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Options.OrderBy))
        };
        if (request.Options.ReverseOrder) data = data.Reverse();
        var indices = data.Select(item => item.Row.Index).ToArray();
        request.Token.ThrowIfCancellationRequested();
        return indices;
    }

    private void InvalidatePreparation()
    {
        ++sortedPreparation.Revision;
        ++unsortedPreparation.Revision;
        sortedPreparation.Cancellation?.Cancel();
        unsortedPreparation.Cancellation?.Cancel();
    }

    private static bool TrySelect(List<IListCellInfo> list, int row, [NotNullWhen(true)] out IListCellInfo? cell) =>
        (cell = list.ElementAtOrDefault(row)) != null;

    private sealed class PreparationSlot
    {
        public long Revision;
        public CancellationTokenSource? Cancellation;
        public Task<int[]>? Task;
    }

    private sealed record CatalogRow(int Index, string Name, string Author, DateTime DateAdded,
        string? Directory, bool HasTrails, bool Favourite);
    private sealed record CatalogRequest(CatalogRow[] Rows, SaberListFilterOptions Options,
        string? Folder, CompareInfo CompareInfo, CancellationToken Token);
}
