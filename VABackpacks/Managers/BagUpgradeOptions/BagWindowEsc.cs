// BagWindowKit: the bag windows' (fuel pouch, pickers) Core gate. The windows are Core KitWindows; Core closes the top one on Esc
// and keeps the inventory open on that press (KitWindow.ConsumedEscThisFrame), so a Core without that guard runs the stopgaps instead.
// Core (FiresUnifiedCore) is a SOFT dependency: nothing here touches a Core type.

using System;

namespace VerdantsAscent.Managers
{
    internal static class BagWindowKit
    {
        internal const string CoreGuid = "com.Fire.FiresUnifiedCore";
        private const string KitWindowType = "FiresCore.UI.Kit.KitWindow, FiresUnifiedCore";

        private const string EscGuardMember = "ConsumedEscThisFrame";   // the Core that keeps the inventory open on the window's Esc

        /// <summary>Null when the windows can run (Core loaded, with KitWindow and its Esc guard); otherwise why not.</summary>
        internal static string Missing()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(CoreGuid)) return "FiresUnifiedCore is not loaded";
            Type t = null;
            try { t = Type.GetType(KitWindowType, false); } catch { }
            if (t == null) return "FiresUnifiedCore has no FiresCore.UI.Kit.KitWindow (an older Core)";
            return t.GetProperty(EscGuardMember) == null ? "FiresUnifiedCore's KitWindow has no Esc guard (an older Core: Esc would close the inventory)" : null;
        }
    }

}
