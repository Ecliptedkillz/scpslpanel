using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;

namespace ScpSlPanel.LabApiBridge;

internal static class BuiltInReportHook
{
    private const string HarmonyId = "scpslpanel.builtin-reports";
    private static BridgeClient? _client;

    public static void Enable(BridgeClient client)
    {
        _client = client;
        var method = AccessTools.Method(typeof(CheaterReport),
            "UserCode_CmdReport__UInt32__String__Byte[]__Boolean");
        if (method == null)
        {
            Logger.Warn("SCP Control could not find the built-in local report method.");
            return;
        }
        new Harmony(HarmonyId).Patch(method,
            prefix: new HarmonyMethod(typeof(BuiltInReportHook), nameof(BeforeReport)));
        Logger.Info("SCP Control built-in report capture enabled.");
    }

    public static void Disable()
    {
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
        _client = null;
    }

    private static void BeforeReport(CheaterReport __instance, uint __0, string __1, bool __3)
    {
        // The final argument selects Northwood cheater reporting. Only mirror local
        // reports intended for the server's own administrators.
        if (__3 || _client == null) return;
        try
        {
            var hub = AccessTools.Field(typeof(CheaterReport), "_hub")?.GetValue(__instance);
            var reporter = Player.ReadyList.FirstOrDefault(player =>
                ReferenceEquals(player.GetType().GetProperty("ReferenceHub",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(player), hub));
            var target = Player.ReadyList.FirstOrDefault(player => player.PlayerId == (int)__0);
            if (reporter != null && target != null)
                _client.RecordReport(reporter, target, __1);
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control could not capture a built-in report: {exception.Message}");
        }
    }
}
