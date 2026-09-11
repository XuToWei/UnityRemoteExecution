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
                case RuntimePlatform.WindowsEditor:
                    return IntPtr.Size == 8 ? "StandaloneWindows64" : "StandaloneWindows";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor: return "StandaloneOSX";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor: return "StandaloneLinux64";
                default: return Application.platform.ToString();
            }
        }
    }
}
