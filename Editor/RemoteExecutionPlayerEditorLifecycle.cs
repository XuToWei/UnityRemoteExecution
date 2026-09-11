using UnityEditor;

namespace RemoteExecution
{
    [InitializeOnLoad]
    internal static class RemoteExecutionPlayerEditorLifecycle
    {
        static RemoteExecutionPlayerEditorLifecycle()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += RemoteExecutionPlayerApi.ShutdownEditorPlayer;
            EditorApplication.quitting += RemoteExecutionPlayerApi.ShutdownEditorPlayer;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                RemoteExecutionPlayerApi.ShutdownEditorPlayer();
        }
    }
}
