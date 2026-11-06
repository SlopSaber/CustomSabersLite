using System;
using System.Threading;
using System.Threading.Tasks;
using CustomSabersLite.Configuration;
using CustomSabersLite.Services;
using IPA.Loader;
using SabersCore.Components;
using SabersCore.Models;
using SabersCore.Services;
using SabersCore.Utilities.Common;
using UnityEngine;
using Zenject;

namespace CustomSabersLite.Menu;

/// <summary>
/// Shows the selected sabers on the menu controllers in place of the default pointer handles.
/// </summary>
internal class MenuPointerSabers : IInitializable, IDisposable
{
    private const string LegacyPluginId = "CustomMenuPointers";

    private readonly PluginConfig config;
    private readonly MenuPointers menuPointers;
    private readonly ISaberInstanceFactory saberInstanceFactory;
    private readonly ITrailFactory trailFactory;
    private readonly PlayerDataModel playerDataModel;

    private SaberInstanceSet? sabers;
    private CustomSaberTrail[] trails = [];
    private CancellationTokenSource? loading;
    private bool disabledByLegacyPlugin;

    public MenuPointerSabers(
        PluginConfig config,
        MenuPointers menuPointers,
        ISaberInstanceFactory saberInstanceFactory,
        ITrailFactory trailFactory,
        PlayerDataModel playerDataModel)
    {
        this.config = config;
        this.menuPointers = menuPointers;
        this.saberInstanceFactory = saberInstanceFactory;
        this.trailFactory = trailFactory;
        this.playerDataModel = playerDataModel;
    }

    public void Initialize()
    {
        if (PluginManager.GetPluginFromId(LegacyPluginId) != null)
        {
            disabledByLegacyPlugin = true;
            Logger.Info($"{LegacyPluginId} is installed; its menu pointers are used instead. Remove it to use the built-in option.");
            return;
        }

        SaberSelection.SelectedSaberChanged += OnSelectedSaberChanged;
        SaberSelection.HeldPreviewChanged += OnHeldPreviewChanged;
        _ = Reload();
    }

    public void Dispose()
    {
        SaberSelection.SelectedSaberChanged -= OnSelectedSaberChanged;
        SaberSelection.HeldPreviewChanged -= OnHeldPreviewChanged;
        CancelLoading();
        DestroySabers();
    }

    public async Task Reload()
    {
        if (disabledByLegacyPlugin) return;

        CancelLoading();
        DestroySabers();

        var hash = SaberSelection.SelectedSaberHash;
        if (!config.EnableMenuPointers || hash is null)
        {
            UpdateVisibility(true);
            return;
        }

        var token = (loading = new CancellationTokenSource()).Token;
        SaberInstanceSet set;
        try
        {
            set = await saberInstanceFactory.CreateSaberSet(hash, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not create menu pointer sabers for {hash}\n{ex}");
            UpdateVisibility(true);
            return;
        }

        if (token.IsCancellationRequested)
        {
            set.Dispose();
            return;
        }

        var (leftParent, rightParent) = menuPointers.Parents;
        sabers = set;
        Attach(set.LeftSaber, leftParent);
        Attach(set.RightSaber, rightParent);

        var settings = new TrailSettings(
            config.DisableWhiteTrail,
            config.OverrideTrailWidth,
            config.TrailWidth,
            config.OverrideTrailDuration,
            config.TrailDuration);
        var left = AddTrails(set.LeftSaber, set.LeftTrails, SaberType.SaberA, settings);
        var right = AddTrails(set.RightSaber, set.RightTrails, SaberType.SaberB, settings);
        trails = [.. left, .. right];

        RefreshColors();
        UpdateVisibility(false);
    }

    private void RefreshColors()
    {
        if (sabers is null) return;

        var colorScheme = playerDataModel.playerData.colorSchemesSettings.GetSelectedColorScheme();
        sabers.LeftSaber?.SetColorScheme(colorScheme);
        sabers.RightSaber?.SetColorScheme(colorScheme);
        foreach (var trail in trails)
        {
            if (trail != null) trail.SetColor(colorScheme);
        }
    }

    private static void Attach(ISaber? saber, Transform parent)
    {
        if (saber is null) return;

        saber.SetParent(parent);
        foreach (var collider in saber.GameObject.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }
    }

    private CustomSaberTrail[] AddTrails(ISaber? saber, ITrailData[] trailData, SaberType saberType, in TrailSettings settings)
    {
        if (saber is null) return [];

        if (trailData.Length == 0)
        {
            trailData = [trailFactory.CreateDefaultTrailData(saberType)];
        }

        var instances = trailFactory.AddTrailsTo(saber, trailData, 1f);
        instances.ConfigureTrails(settings);
        return instances;
    }

    private void OnSelectedSaberChanged(string? hash) => _ = Reload();

    private void OnHeldPreviewChanged(bool visible) => UpdateVisibility(sabers is null);

    private void UpdateVisibility(bool showHandles)
    {
        var showSabers = sabers is not null && !SaberSelection.HeldPreviewVisible;
        sabers?.SetActive(showSabers);

        // The held preview manages the handles while it is shown.
        if (SaberSelection.HeldPreviewVisible) return;

        menuPointers.SetPointerVisibility(showHandles || !showSabers);
    }

    private void CancelLoading()
    {
        if (loading is null) return;
        loading.Cancel();
        loading.Dispose();
        loading = null;
    }

    private void DestroySabers()
    {
        sabers?.Dispose();
        sabers = null;
        trails = [];
    }
}
