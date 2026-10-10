using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    public static class AdminSyncing
    {
        private static bool _isServer;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class AdminStatusSyncPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                _isServer = __instance.IsServer();
                if (MyMod.Instance == null) return;

                ZRoutedRpc.instance.Register<ZPackage>(MyMod.PluginName + " AdminStatusSync", RPC_AdminStatusSync);

                if (_isServer)
                    __instance.StartCoroutine(WatchAdminListChanges());
            }
        }

        private static IEnumerator WatchAdminListChanges()
        {
            var adminList = (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
            var currentAdmins = new HashSet<string>(adminList.GetList());

            while (true)
            {
                yield return new WaitForSeconds(30f);

                var newAdmins = new HashSet<string>(adminList.GetList());
                if (newAdmins.SetEquals(currentAdmins)) continue;

                var peers = ZNet.instance.GetPeers();
                var adminPeers = peers.Where(p => ZNet.instance.IsAdmin(p.m_rpc.GetSocket().GetHostName())).ToList();
                var nonAdminPeers = peers.Except(adminPeers).ToList();

                SendAdminStatus(nonAdminPeers, false);
                SendAdminStatus(adminPeers, true);

                currentAdmins = newAdmins;
            }
        }

        private static void SendAdminStatus(List<ZNetPeer> peers, bool isAdmin)
        {
            if (!peers.Any()) return;

            var package = new ZPackage();
            package.Write(isAdmin);

            ZNet.instance.StartCoroutine(SendZPackage(peers, package));
        }

        private static void RPC_AdminStatusSync(long sender, ZPackage package)
        {
            if (_isServer)
            {
                var peer = ZNet.instance.GetPeer(sender);
                if (peer == null) return;

                if (!ZNet.instance.IsAdmin(peer.m_rpc.GetSocket().GetHostName())) return;

                var pkg = new ZPackage();
                pkg.Write(true);

                ZRoutedRpc.instance.InvokeRoutedRPC(sender, MyMod.PluginName + " AdminStatusSync", pkg);
                return;
            }

            // Client: Receive admin status
            try
            {
                bool isAdmin = package.ReadBool();
                ConfigSync.lockExempt = isAdmin;
                ConfigSync.RefreshReadOnlyFlagsForAll();

                if (ConfigManager.Instance.configVerboseLogging.Value)
                    Debug.Log($"{MyMod.PluginName}: Admin status received: {(isAdmin ? "ADMIN" : "NON-ADMIN")}");

                // Call the method on the plugin instance
                MyMod.Instance?.GetType()
                    .GetMethod("OnConfigLockChanged", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .Invoke(MyMod.Instance, null);
            }
            catch { }
        }

        private static IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package)
        {
            if (!ZNet.instance) yield break;

            byte[] data = package.GetArray();
            if (data.Length > 10000)
            {
                var compressed = new ZPackage();
                compressed.Write(4);

                using var output = new MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    deflate.Write(data, 0, data.Length);

                compressed.Write(output.ToArray());
                package = compressed;
            }

            foreach (var peer in peers.Where(p => p.IsReady()))
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_server ? 0L : peer.m_uid, MyMod.PluginName + " AdminStatusSync", package);
        }
    }
}