using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using CustomSabersLite.Utilities.Common;
using SabersCore.Models;
using Zenject;

namespace CustomSabersLite.Services;

internal class FavouritesManager : IInitializable, ITickable, IDisposable
{
    private readonly HashSet<string> favouriteSaberHashes = [];
    private readonly PersistenceState persistence;
    private readonly DirectoryManager directoryManager;
    private readonly TaskCompletionSource<bool> ready = new();
    private Task? worker;
    private bool loadPublished;
    private bool dirty;
    private long changedAt;
    private bool disposed;
    private bool initializing;

    public FavouritesManager(DirectoryManager directoryManager)
    {
        this.directoryManager = directoryManager;
        persistence = new(Path.Combine(directoryManager.UserData.FullName, "favourites.json"));
    }

    public Task Ready => ready.Task;
    public bool IsReady => ready.Task.Status == TaskStatus.RanToCompletion;
    public long Revision { get; private set; }

    public async void Initialize()
    {
        if (initializing || disposed) return;
        initializing = true;
        try
        {
            await directoryManager.Ready;
            await UnityAsync.SwitchToUnity();
            if (disposed) return;
            var state = persistence;
            worker = Task.Run(() => RunPersistence(state));
        }
        catch (Exception e)
        {
            await UnityAsync.SwitchToUnity();
            if (!disposed) ready.TrySetException(e);
        }
    }

    public void AddFavourite(SaberFileInfo saberFile)
    {
        if (disposed || !IsReady) return;
        if (favouriteSaberHashes.Add(saberFile.Hash)) ++Revision;
        SaveFavourites();
    }

    public void RemoveFavourite(SaberFileInfo saberFile)
    {
        if (disposed || !IsReady) return;
        if (favouriteSaberHashes.Remove(saberFile.Hash)) ++Revision;
        SaveFavourites();
    }

    public bool IsFavourite(SaberFileInfo saberFile) => favouriteSaberHashes.Contains(saberFile.Hash);

    public void Tick()
    {
        if (disposed) return;
        if (!loadPublished && persistence.Loaded.Task.IsCompleted)
        {
            loadPublished = true;
            try
            {
                var saved = persistence.Loaded.Task.GetAwaiter().GetResult();
                if (saved != null) favouriteSaberHashes.UnionWith(saved);
                ++Revision;
                ready.SetResult(true);
            }
            catch (Exception e)
            {
                Logger.Error($"Problem encountered while reading favourites file:\n{e}");
                ready.SetException(e);
            }
        }

        while (persistence.Errors.TryDequeue(out var error)) Logger.Error(error);
        if (dirty && IsReady && (Stopwatch.GetTimestamp() - changedAt) * 1000d / Stopwatch.Frequency >= 150)
        {
            dirty = false;
            try { persistence.Submit(CaptureSave()); }
            catch (Exception e) { Logger.Error($"Problem encountered while updating favourites file:\n{e}"); }
        }
    }

    private void SaveFavourites()
    {
        dirty = true;
        changedAt = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (dirty && IsReady) persistence.Submit(CaptureSave());
        }
        catch (Exception e) { Logger.Error($"Problem encountered while updating favourites file:\n{e}"); }
        finally
        {
            ready.TrySetCanceled();
            persistence.Complete();
            // Keep the physical worker while its accepted final write drains.
        }
    }

    private SaveRequest CaptureSave()
    {
        var snapshot = new string[favouriteSaberHashes.Count];
        favouriteSaberHashes.CopyTo(snapshot);
        if (JsonConvert.DefaultSettings == null) return new(snapshot, null);

        // Custom global converters and callbacks keep their owner context.
        using var text = new StringWriter();
        using var json = new JsonTextWriter(text);
        JsonSerializer.CreateDefault().Serialize(json, snapshot);
        json.Flush();
        return new(null, text.ToString());
    }

    private static async Task RunPersistence(PersistenceState state)
    {
        try
        {
            try
            {
                state.ReadCancellation.Token.ThrowIfCancellationRequested();
                string[]? saved = null;
                if (File.Exists(state.Path))
                {
                    using var stream = File.OpenRead(state.Path);
                    using var text = new StreamReader(stream);
                    using var json = new JsonTextReader(text);
                    saved = new JsonSerializer().Deserialize<string[]>(json);
                }
                state.ReadCancellation.Token.ThrowIfCancellationRequested();
                state.Loaded.SetResult(saved);
            }
            catch (OperationCanceledException) { state.Loaded.SetCanceled(); }
            catch (Exception e) { state.Loaded.SetException(e); }

            while (true)
            {
                await state.Wakeup.WaitAsync().ConfigureAwait(false);
                var request = state.Take();
                if (request != null)
                {
                    try
                    {
                        using var text = File.CreateText(state.Path);
                        if (request.Json != null) text.Write(request.Json);
                        else
                        {
                            using var json = new JsonTextWriter(text);
                            new JsonSerializer().Serialize(json, request.Hashes);
                        }
                    }
                    catch (Exception e)
                    {
                        state.Errors.Enqueue($"Problem encountered while updating favourites file:\n{e}");
                    }
                }
                if (state.IsComplete && !state.HasPending) return;
            }
        }
        finally
        {
            state.ReadCancellation.Dispose();
            state.Wakeup.Dispose();
        }
    }

    private sealed record SaveRequest(string[]? Hashes, string? Json);

    private sealed class PersistenceState(string path)
    {
        private readonly object gate = new();
        private SaveRequest? pending;
        private bool complete;
        public readonly string Path = path;
        public readonly CancellationTokenSource ReadCancellation = new();
        public readonly TaskCompletionSource<string[]?> Loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<string> Errors = new();
        public readonly SemaphoreSlim Wakeup = new(0, 1);
        public bool IsComplete { get { lock (gate) return complete; } }
        public bool HasPending { get { lock (gate) return pending != null; } }

        public void Submit(SaveRequest request)
        {
            lock (gate)
            {
                pending = request;
                if (Wakeup.CurrentCount == 0) Wakeup.Release();
            }
        }

        public SaveRequest? Take()
        {
            lock (gate)
            {
                var request = pending;
                pending = null;
                return request;
            }
        }

        public void Complete()
        {
            lock (gate)
            {
                complete = true;
                ReadCancellation.Cancel();
                if (Wakeup.CurrentCount == 0) Wakeup.Release();
            }
        }
    }
}
