using HarmonyLib;

namespace Modzified_Cinematics.Patches;

/// <summary>
/// EWP/manual RPC entry point: <c>RPC_PlayModzifiedCinematics(string ruleName)</c>.
/// Registered once at <c>ZNet.Awake</c>, no permission check inside the handler — mirrors Jere
/// Kuusela's <c>player_scaling</c> RPC shape (see docs/mod-networking-rpc.md). Handler hands off to
/// the same <see cref="TriggerEngine"/> Fire() path every other trigger kind uses, so cooldown/oneTime
/// and combat protection apply automatically.
/// </summary>
internal static class RpcPatches
{
  [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
  private static class ZNetAwakePatch
  {
    [HarmonyPostfix]
    private static void Postfix()
    {
      ZRoutedRpc.instance.Register<string>("RPC_PlayModzifiedCinematics", RPC_PlayModzifiedCinematics);
    }
  }

  private static void RPC_PlayModzifiedCinematics(long sender, string ruleName)
  {
    TriggerEngine.OnClientRpc(ruleName);
  }
}
