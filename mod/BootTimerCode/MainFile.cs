using System;
using System.Diagnostics;
using System.Reflection;

using Godot;

using MegaCrit.Sts2.Core.Modding;

namespace BootTimer.BootTimerCode;

/// <summary>
/// Diagnostic mod: stamps wall-clock UTC time into the game log at boot milestones.
/// Mod initializer order gives us "everything before us loaded"; Harmony postfixes
/// stamp engine phases; per-mod DLL loads are stamped by prefixing
/// ModManager.TryLoadMod. Lines: [BootTimer] (utc HH:mm:ss.fff) phase.
/// Remove after diagnosis.
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "BootTimer";

    public static void Initialize()
    {
        Mark("BootTimer initializer entered");
        try
        {
            var harmony = new HarmonyLib.Harmony(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Mark("patches applied (TryLoadMod prefix now timestamps every mod load)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[BootTimer] patch failure: {e.Message}");
        }
    }

    internal static void Mark(string phase)
        => MegaCrit.Sts2.Core.Logging.Log.Info($"[BootTimer] (utc {DateTimeOffset.UtcNow:HH:mm:ss.fff}) {phase}");
}

/// <summary>Timestamp every mod load attempt (prefix) and completion (postfix).</summary>
[HarmonyLib.HarmonyPatch(typeof(ModManager), "TryLoadMod")]
internal static class TryLoadModPatch
{
    private static void Prefix(Mod mod)
        => MainFile.Mark($"TryLoadMod START {mod.manifest?.id} ({mod.modSource})");

    private static void Postfix(Mod mod)
        => MainFile.Mark($"TryLoadMod END   {mod.manifest?.id}");
}

/// <summary>LocManager.Initialize postfix: loc tables loaded.</summary>
[HarmonyLib.HarmonyPatch(typeof(MegaCrit.Sts2.Core.Localization.LocManager), "Initialize")]
internal static class LocManagerInitPatch
{
    private static void Postfix() => MainFile.Mark("LocManager.Initialize DONE (all loc tables merged)");
}

/// <summary>Main menu screen is up - boot complete for the user.</summary>
[HarmonyLib.HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu), "_Ready")]
internal static class MainMenuReadyPatch
{
    private static void Postfix() => MainFile.Mark("NMainMenu._Ready = MAIN MENU VISIBLE");
}