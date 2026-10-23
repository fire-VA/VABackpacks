using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers.Camp
{
    // One camp per character was only checked in the player's own game
    // (their m_customData), so an edited profile or a second client could deploy more. The SERVER now decides: it counts the
    // live camp chests whose ZDO carries va_camp_owner = this player (BackpackCampMarker.KEY_OWNER, set at every deploy) and
    // answers. The client deploys only on a yes. On a server or in single player the same count runs locally. A server that
    // never answers (older VABackpacks) falls back after 3 s to the old client-side rule, so a version mismatch can't block
    // camping entirely. RPCs register on EVERY ZNet.Awake (one per ZRoutedRpc instance; tonight's team audit rule).
    internal static class ServerCampGate
    {
        private const string RpcAsk = "VAB_CampAsk";
        private const string RpcAnswer = "VAB_CampAnswer";
        private const float AnswerTimeout = 3f;

        private static ZRoutedRpc s_registeredOn;
        private static ItemDrop s_pendingDrop;
        private static Func<ItemDrop, bool> s_retry;
        private static float s_askedAt;
        private static ItemDrop s_allowedDrop;
        private static float s_allowedAt;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class Register_Patch
        {
            private static void Postfix()
            {
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null || ReferenceEquals(rpc, s_registeredOn)) return;
                rpc.Register<long>(RpcAsk, OnAsk);
                rpc.Register<bool>(RpcAnswer, OnAnswer);
                s_registeredOn = rpc;
                s_pendingDrop = null; s_allowedDrop = null;
            }
        }

        /// <summary>True when this deploy may go ahead now. False = refused, or asked the server (the deploy re-runs on its yes).</summary>
        internal static bool Allowed(Player player, ItemDrop drop, Func<ItemDrop, bool> retry)
        {
            if (player == null || drop == null) return false;
            if (s_allowedDrop == drop && Time.realtimeSinceStartup - s_allowedAt < 10f) { s_allowedDrop = null; return true; }

            ZNet net = ZNet.instance;
            if (net == null || net.IsServer() || ZRoutedRpc.instance == null)
            {
                int live = LiveCampsOf(player.GetPlayerID());
                if (live == 0) return true;
                Refuse(live);
                return false;
            }

            if (s_pendingDrop != null && Time.realtimeSinceStartup - s_askedAt < AnswerTimeout) return false;   // already asked
            s_pendingDrop = drop; s_retry = retry; s_askedAt = Time.realtimeSinceStartup;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAsk, player.GetPlayerID());
            Debug.Log($"[VABackpacks] camp limit: asked the server for player {player.GetPlayerID()} (answer within {AnswerTimeout:0} s, else the client-side check)");
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Checking your camps with the server...");
            if (BackpackCampManager.Instance != null) BackpackCampManager.Instance.StartCoroutine(Timeout(drop));
            return false;
        }

        private static IEnumerator Timeout(ItemDrop drop)
        {
            yield return new WaitForSecondsRealtime(AnswerTimeout);
            if (s_pendingDrop != drop) yield break;
            Debug.LogWarning("[VABackpacks] camp limit: the server did not answer (older VABackpacks there?); this deploy uses the client-side check");
            Grant(drop);
        }

        // Server: count this player's live camps and answer the asker.
        private static void OnAsk(long sender, long playerId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int live = LiveCampsOf(playerId);
            Debug.Log($"[VABackpacks] camp limit: player {playerId} asks to deploy; {live} live camp(s) -> {(live == 0 ? "allowed" : "refused")}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAnswer, live == 0);
        }

        // Client: the server's answer.
        private static void OnAnswer(long sender, bool allowed)
        {
            ItemDrop drop = s_pendingDrop;
            if (drop == null) return;
            if (!allowed) { s_pendingDrop = null; Refuse(-1); return; }
            Debug.Log("[VABackpacks] camp limit: server said yes, deploying");   // The yes is logged so the client shows it
            Grant(drop);
        }

        private static void Grant(ItemDrop drop)
        {
            Func<ItemDrop, bool> retry = s_retry;
            s_pendingDrop = null; s_retry = null;
            if (drop == null) return;   // the bag was picked up or destroyed meanwhile
            s_allowedDrop = drop; s_allowedAt = Time.realtimeSinceStartup;
            try { retry?.Invoke(drop); }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp deploy after the server's yes failed: {ex.Message}"); }
        }

        private static void Refuse(int live)
        {
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "You already have a camp deployed. Pack it up first.");
            Debug.Log($"[VABackpacks] camp limit: deploy refused ({(live < 0 ? "server said no" : live + " live camp(s) here")})");
        }

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        /// <summary>Camp chests in the world owned by this player (only camp chests carry va_camp_owner).</summary>
        internal static int LiveCampsOf(long playerId)
        {
            if (playerId == 0L || ZDOMan.instance == null) return 0;
            int n = 0;
            try
            {
                foreach (ZDO zdo in ObjectsById(ZDOMan.instance).Values)
                    if (zdo != null && zdo.GetLong(BackpackCampMarker.KEY_OWNER, 0L) == playerId) n++;
            }
            catch (Exception ex) { Debug.LogWarning($"[VABackpacks] camp limit: world scan failed ({ex.Message}); allowing"); return 0; }
            return n;
        }
    }
}
