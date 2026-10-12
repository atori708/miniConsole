using UnityEditor;

namespace MiniConsole
{
    [FilePath("UserSettings/MiniConsoleSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class ConsoleSettings : ScriptableSingleton<ConsoleSettings>
    {
        public SeverityMask visibleSeverities = SeverityMask.All;
        public bool collapse;
        public bool clearOnPlay = true;
        public bool clearOnBuild = true;
        public bool clearOnRecompile;
        public bool fullPath;
        public bool showTimestamp;

        public void SaveSettings() => Save(true);
    }
}
