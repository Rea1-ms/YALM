using Microsoft.Win32;

namespace YALM.Mechrevo;

internal static class GcuKeyboardStorage
{
    public static GcuKeyboardSavedProfile? Read(MachineInfo machine, GcuKeyboardSettings? live)
    {
        if (!machine.SupportsModeControl || live?.HasEffectParameters != true) return null;
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var settings = registry.OpenSubKey(@"SOFTWARE\OEM\GamingCenter2\RGBKeyboard\SingleZone", writable: false);
            if (settings is null) return null;
            var candidates = new List<string>();
            foreach (string name in settings.GetValueNames())
            {
                const string suffix = "_LastEffect";
                if (name.EndsWith(suffix, StringComparison.Ordinal) &&
                    int.TryParse(name[..^suffix.Length], out int project) && project >= 0 &&
                    settings.GetValue(name) is string json) candidates.Add(json);
            }
            return GcuKeyboardSavedProfile.Match(live, candidates);
        }
        catch { return null; }
    }
}
