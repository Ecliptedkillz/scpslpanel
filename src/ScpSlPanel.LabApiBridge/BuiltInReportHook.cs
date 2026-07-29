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
                ReferenceEquals(Member(player, "ReferenceHub"), hub));
            // The report RPC identifies its target by Mirror network ID, not by
            // the Remote Admin PlayerId shown in the player list.
            var target = Player.ReadyList.FirstOrDefault(player => NetworkId(player) == __0);
            if (reporter != null && target != null)
            {
                _client.RecordReport(reporter, target, __1);
                Logger.Info($"SCP Control forwarded built-in report from {reporter.UserId} about {target.UserId}.");
            }
            else
                Logger.Warn($"SCP Control could not resolve built-in report players "
                    + $"(reporter={reporter != null}, targetNetId={__0}, target={target != null}).");
        }
        catch (Exception exception)
        {
            Logger.Warn($"SCP Control could not capture a built-in report: {exception.Message}");
        }
    }

    private static uint? NetworkId(Player player)
    {
        var direct = Member(player, "NetworkId") ?? Member(player, "NetId");
        if (direct is uint id) return id;
        var hub = Member(player, "ReferenceHub");
        var identity = hub is null ? null
            : Member(hub, "networkIdentity") ?? Member(hub, "netIdentity");
        var value = identity is null ? null : Member(identity, "netId");
        return value is uint networkId ? networkId : null;
    }

    private static object? Member(object value, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = value.GetType();
        return type.GetProperty(name, flags)?.GetValue(value)
            ?? type.GetField(name, flags)?.GetValue(value);
    }
}
