using System;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using IPA.Utilities.Async;

namespace CustomSabersLite.Utilities.Common;

internal static class UnityAsync
{
    public static Task<TResult> StartUnitySafeTask<TResult>(Func<TResult> lambda) =>
        UnityMainThreadTaskScheduler.Factory.StartNew(lambda);
    public static MainThreadSwitch SwitchToUnity() => new();

    public readonly struct MainThreadSwitch : INotifyCompletion
    {
        public MainThreadSwitch GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => UnityMainThreadTaskScheduler.Factory.StartNew(continuation);
    }
}
