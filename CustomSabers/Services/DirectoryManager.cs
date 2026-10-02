using System.IO;
using IPA.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;
using SabersCore.Services;

namespace CustomSabersLite.Services;

internal class DirectoryManager : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly string customSabersPath = Path.Combine(UnityGame.InstallPath, "CustomSabers");
    private readonly string userDataPath = Path.Combine(UnityGame.UserDataPath, "Custom Sabers Lite");

    public DirectoryManager(ISaberDirectoryReadiness readiness)
    {
        CustomSabers = new(customSabersPath);
        UserData = new(userDataPath);
        DeletedSabers = new(Path.Combine(userDataPath, "Deleted Sabers"));
        Ready = readiness.EnsureDirectoriesAsync([customSabersPath, userDataPath, DeletedSabers.FullName], cancellation.Token);
    }
    
    public DirectoryInfo CustomSabers { get; }
    public DirectoryInfo UserData { get; }
    public DirectoryInfo DeletedSabers { get; }

    public Task Ready { get; }

    public void Dispose() => cancellation.Cancel();
}
