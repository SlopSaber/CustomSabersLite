using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using CustomSabersLite.Configuration;
using CustomSabersLite.Menu.Components;
using CustomSabersLite.Models;
using CustomSabersLite.Services;
using CustomSabersLite.Utilities.Extensions;
using HMUI;
using SabersCore.Services;
using TMPro;
using UnityEngine;
using Zenject;
using static CustomSabersLite.Utilities.Common.PluginResources;
using static CustomSabersLite.Utilities.Common.UnityAsync;

namespace CustomSabersLite.Menu.Views;

[HotReload(RelativePathToLayout = "../BSML/saberList.bsml")]
[ViewDefinition("CustomSabersLite.Menu.BSML.saberList.bsml")]
internal class SaberListViewController : BSMLAutomaticViewController
{
    [Inject] private readonly PluginConfig config = null!;
    [Inject] private readonly DirectoryManager directoryManager = null!;
    [Inject] private readonly ISaberMetadataLoader saberMetadataLoader = null!;
    [Inject] private readonly ISaberMetadataCache saberMetadataCache = null!;
    [Inject] private readonly FavouritesManager favouritesManager = null!;
    [Inject] private readonly SaberListManager saberListManager = null!;
    [Inject] private readonly SaberPreviewManager previewManager = null!;

    [UIComponent("saber-list")] private readonly SaberListTableData saberList = null!;
    [UIComponent("delete-saber-modal")] private readonly ModalView deleteSaberModal = null!;
    [UIComponent("delete-saber-modal-text")] private readonly TextMeshProUGUI deleteSaberModalText = null!;
    [UIComponent("search-input")] private readonly BsInputField searchBsInputField = null!;
    [UIComponent("favourite-toggle")] private readonly FavouriteToggle favouriteToggle = null!;

    [UIComponent("sort-direction-button")] private readonly ImageView sortDirectionButtonImage = null!;
    [UIComponent("preview-button")] private readonly ImageView previewButtonImage = null!;
    
    [UIObject("loading-icon")] private readonly GameObject loadingIcon = null!;

    private CancellationTokenSource saberPreviewTokenSource = new();
    private SaberListType currentSaberList = SaberListType.Sabers;
    private SaberValue? previewSaberValue;
    private SaberValue? previewTrailValue;
    private SaberValue? requestedPreviewSaberValue;
    private SaberValue? requestedPreviewTrailValue;
    private Task? previewTask;
    private long listRevision;
    private bool destroyed;
    private CancellationTokenSource? listTokenSource;
    private bool deleting;
    private bool viewRetired;

    [UIAction("#post-parse")]
    public void PostParse()
    {
        saberMetadataLoader.LoadingProgressChanged += LoadingProgressChanged;

        searchBsInputField.Text = SearchFilter;
        searchBsInputField.AddInputChangedListener(inp => SearchFilter = inp.text);

        loadingIcon.SetActive(!saberMetadataLoader.CurrentProgress.Completed);
    }

    private List<object> orderByChoices = [.. Enum.GetNames(typeof(OrderBy))];
    public string OrderByFilter
    {
        get => config.OrderByFilter.ToString();
        set
        {
            config.OrderByFilter = Enum.TryParse(value, out OrderBy orderBy) ? orderBy : config.OrderByFilter;
            RefreshList();
            saberList.ScrollToTop();
        }
    }

    public string OrderByFormatter(string value) => !Enum.TryParse<OrderBy>(value, out var orderBy) ? string.Empty
        : orderBy switch
        {
            OrderBy.Name => "Name",
            OrderBy.Author => "Author",
            OrderBy.RecentlyAdded => "Most Recent",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public string SearchFilter
    {
        get => config.SearchFilter;
        set
        {
            config.SearchFilter = value;
            RefreshList();
            saberList.ScrollToTop();
        }
    }

    public void ListSelected(SegmentedControl segmentedControl, int idx)
    {
        currentSaberList = (SaberListType)idx;
        RefreshList();
    }

    public async void ListCellSelected(TableView tableView, int row)
    {
        if (!saberListManager.TrySelectSorted(row, out var saberListCell)) return;
        if (saberListCell.TryGetCellDirectory(out var directoryInfo))
        {
            saberListManager.OpenFolder(directoryInfo);
            RefreshList();
        }
        else if (saberListCell is ListFavouritesCellInfo)
        {
            saberListManager.ShowFavourites = true;
            RefreshList();
        }
        else if (saberListCell.TryGetSaberValue(out var saberValue))
        {
            SelectedSaberValue = saberValue;
            favouriteToggle.Interactable = saberValue is SaberHash;
        }
        else
        {
            favouriteToggle.Interactable = false;
        }
        
        NotifyPropertyChanged(nameof(FavouriteButtonValue));
        await GeneratePreview();
    }

    public void ListDirectionButtonPressed()
    {
        config.ReverseSort = !config.ReverseSort;
        sortDirectionButtonImage.sprite = config.ReverseSort ? SortAscendingIcon : SortDescendingIcon;        
        RefreshList();
    }

    public bool FavouriteButtonValue
    {
        get => SelectedSaberValue.TryGetSaberHash(out var saberHash) 
               && saberMetadataCache.TryGetMetadata(saberHash.Hash, out var meta)  
               && favouritesManager.IsFavourite(meta.SaberFile);
        set
        {
            if (!favouritesManager.IsReady) return;
            if (!SelectedSaberValue.TryGetSaberHash(out var saberHash)
                || !saberMetadataCache.TryGetMetadata(saberHash.Hash, out var meta)) return;
            
            if (saberList.Data.TryGetElementAt(saberListManager.IndexForSaberValue(saberHash), out var cell)
                && cell is ListInfoCellInfo infoCell) infoCell.IsFavourite = value;
            
            if (value) favouritesManager.AddFavourite(meta.SaberFile);
            else favouritesManager.RemoveFavourite(meta.SaberFile);
        }
    }

    public void FolderButtonPressed()
    {
        Process.Start(directoryManager.CustomSabers.FullName);
    }

    public void DeleteButtonPressed()
    {
        if (deleting) return;
        if (SelectedSaberValue.TryGetSaberHash(out var saberHash))
        {
            var meta = saberMetadataCache.GetOrDefault(saberHash.Hash);
            deleteSaberModalText.text = meta?.Descriptor.SaberName.FullName ?? "Unknown";
            deleteSaberModal.Show(true);
        }
    }

    public void DeleteCancelPressed()
    {
        deleteSaberModal.Hide(true);
    }

    public async void DeleteConfirmPressed()
    {
        deleteSaberModal.Hide(true);
        if (deleting || !SelectedSaberValue.TryGetSaberHash(out var deletedSaberHash)) return;
        deleting = true;
        long revision = listRevision;
        int deletedSaberIndex = saberListManager.IndexForSaberValue(deletedSaberHash);
        try
        {
            await saberListManager.DeleteSaberAsync(deletedSaberHash.Hash);
            await SwitchToUnity();
            if (destroyed || viewRetired) return;
            if (revision == listRevision && Equals(SelectedSaberValue, deletedSaberHash)
                && saberListManager.TrySelectSorted(deletedSaberIndex - 1, out var cell)
                && cell.TryGetSaberValue(out var saberValue)) SelectedSaberValue = saberValue;
            RefreshList();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            await SwitchToUnity();
            if (!destroyed) Logger.Error($"Problem encountered while deleting saber:\n{e}");
        }
        finally
        {
            await SwitchToUnity();
            deleting = false;
        }
    }

    public void PreviewButtonPressed()
    {
        config.EnableMenuSabers = !config.EnableMenuSabers;
        previewButtonImage.sprite = config.EnableMenuSabers ? PreviewHeldIcon : PreviewStaticIcon;
        previewManager.UpdateActivePreviewAnimated();
    }

    public async void ReloadButtonPressed()
    {
        saberListManager.Refresh();
        saberList.Data.Clear();
        saberList.ReloadDataKeepingPosition();
     
        previewManager.SetPreviewActive(false);
        
        // this will invoke an event on completion that gets used to refresh the list
        await saberMetadataLoader.ReloadAsync();
    }

    private async void RefreshList()
    {
        if (destroyed || viewRetired) return;
        var revision = ++listRevision;
        listTokenSource?.Cancel();
        var source = new CancellationTokenSource();
        listTokenSource = source;
        try
        {
            var options = new SaberListFilterOptions(config.SearchFilter, config.OrderByFilter, config.ReverseSort,
                currentSaberList == SaberListType.Trails, false);
            var cells = await saberListManager.UpdateListAsync(options, source.Token);
            await SwitchToUnity();
            if (destroyed || viewRetired || revision != listRevision || source.IsCancellationRequested) return;
            saberListManager.PublishSorted(cells);
            saberList.Data.Clear();
            saberList.Data.AddRange(cells);
            saberList.ReloadData();
            if (saberListManager.CurrentListContains(SelectedSaberValue))
                saberList.SelectCellWithIdx(saberListManager.IndexForSaberValue(SelectedSaberValue));
            else saberList.ClearSelection();
            StartUnitySafeTask(GeneratePreview);
            NotifyPropertyChanged(nameof(FavouriteButtonValue));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            await SwitchToUnity();
            if (!destroyed && revision == listRevision) Logger.Error($"Problem encountered while refreshing saber list:\n{e}");
        }
        finally
        {
            await SwitchToUnity();
            if (ReferenceEquals(listTokenSource, source)) listTokenSource = null;
            source.Dispose();
        }
    }

    private void LoadingProgressChanged(MetadataLoaderProgress progress)
    {
        if (progress.Completed)
        {
            previewSaberValue = null;
            previewTrailValue = null;
            RefreshList();
        }
        loadingIcon.SetActive(!progress.Completed);
    }

    private async Task GeneratePreview()
    {
        try
        {
            previewManager.SetPreviewActive(true);
            var selectedSaber = config.CurrentlySelectedSaber;
            var selectedTrail = config.CurrentlySelectedTrail;
            if (previewSaberValue == selectedSaber && previewTrailValue == selectedTrail)
            {
                return;
            }

            if (previewTask is { IsCompleted: false }
                && !saberPreviewTokenSource.IsCancellationRequested
                && requestedPreviewSaberValue == selectedSaber
                && requestedPreviewTrailValue == selectedTrail)
            {
                await previewTask;
                return;
            }
            
            saberPreviewTokenSource.CancelThenDispose();
            saberPreviewTokenSource = new();
            previewSaberValue = null;
            previewTrailValue = null;
            requestedPreviewSaberValue = selectedSaber;
            requestedPreviewTrailValue = selectedTrail;
            var task = previewManager.GeneratePreview(saberPreviewTokenSource.Token);
            previewTask = task;
            await task;
            previewSaberValue = selectedSaber;
            previewTrailValue = selectedTrail;
        }
        catch (OperationCanceledException) { }
    }

    private SaberValue SelectedSaberValue
    {
        get => currentSaberList switch
        {
            SaberListType.Sabers => config.CurrentlySelectedSaber,
            SaberListType.Trails => config.CurrentlySelectedTrail,
            _ => throw new ArgumentOutOfRangeException(nameof(currentSaberList))
        };
        set
        {
            if (currentSaberList == SaberListType.Sabers) config.CurrentlySelectedSaber = value;
            else config.CurrentlySelectedTrail = value;
        }
    }
    
    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
        viewRetired = false;

        saberListManager.OpenFolder(directoryManager.CustomSabers);
        RefreshList();
        
        previewManager.SetPreviewActive(true);
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        viewRetired = true;
        ++listRevision;
        listTokenSource?.Cancel();
        saberPreviewTokenSource.Cancel();
        previewManager.SetPreviewActive(false);
    }

    protected override void OnDestroy()
    {
        destroyed = true;
        ++listRevision;
        listTokenSource?.Cancel();
        saberMetadataLoader.LoadingProgressChanged -= LoadingProgressChanged;
        saberPreviewTokenSource.Dispose();
        base.OnDestroy();
    }
}
