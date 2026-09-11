using System;
using UnityEngine;

namespace RemoteExecution
{
    internal static class RemoteExecutionPlatform
    {
        internal static string GetPlayerTarget()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.IPhonePlayer: return "iOS";
                case RuntimePlatform.WindowsPlayer:
                    return IntPtr.Size == 8 ? "StandaloneWindows64" : "StandaloneWindows";
                case RuntimePlatform.OSXPlayer: return "StandaloneOSX";
                case RuntimePlatform.LinuxPlayer: return "StandaloneLinux64";
                default: return Application.platform.ToString();
            }
        }
    }
}
