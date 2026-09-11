using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RemoteExecution
{
    internal static class RemoteExecutionTaskExtensions
    {
        internal static void Forget(this Task task)
        {
            task.ContinueWith(completed => Debug.LogException(completed.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
