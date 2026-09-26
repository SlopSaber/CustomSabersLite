using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CustomSabersLite.Utilities.Common;
using CustomSabersLite.Utilities.Extensions;
using Newtonsoft.Json;
using SabersCore.Models;
using Zenject;

namespace CustomSabersLite.Services;

internal class FavouritesManager : IInitializable
{
    private readonly HashSet<string> favouriteSaberHashes = [];
    private readonly FileInfo favouritesFile;
    
    public FavouritesManager(DirectoryManager directoryManager)
    {
        favouritesFile = new(Path.Combine(directoryManager.UserData.FullName, "favourites.json"));
    }

    private CancellationTokenSource updateFavouritesTokenSource = new();
    private readonly SemaphoreSlim saveLock = new(1, 1);

    public void Initialize() => ReadFavourites();

    public void AddFavourite(SaberFileInfo saberFile)
    {
        favouriteSaberHashes.Add(saberFile.Hash);
        SaveFavourites();
    }

    public void RemoveFavourite(SaberFileInfo saberFile)
    {
        favouriteSaberHashes.Remove(saberFile.Hash);
        SaveFavourites();
    }

    public bool IsFavourite(SaberFileInfo saberFile) => 
        favouriteSaberHashes.Contains(saberFile.Hash);

    private void ReadFavourites()
    {
        if (!favouritesFile.Exists) return;
        using var favouritesStream = favouritesFile.OpenRead();
        var savedFavourites = favouritesStream.DeserializeStream<string[]>();
        if (savedFavourites is null) return;
        favouriteSaberHashes.Clear();
        foreach (var hash in savedFavourites)
        {
            favouriteSaberHashes.Add(hash);
        }
    }
    
    private void SaveFavourites()
    {
        updateFavouritesTokenSource.CancelThenDispose();
        updateFavouritesTokenSource = new();
        var token = updateFavouritesTokenSource.Token;
        var snapshot = new string[favouriteSaberHashes.Count];
        favouriteSaberHashes.CopyTo(snapshot);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token);
                await saveLock.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await using var streamWriter = favouritesFile.CreateText();
                    using var jsonWriter = new JsonTextWriter(streamWriter);
                    JsonSerializer.CreateDefault().Serialize(jsonWriter, snapshot);
                    await jsonWriter.FlushAsync(CancellationToken.None);
                }
                finally
                {
                    saveLock.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Logger.Error($"Problem encountered while updating favourites file:\n{e}");
            }
        });
    }
}
