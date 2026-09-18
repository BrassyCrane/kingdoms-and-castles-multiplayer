using System;
using Harmony;
using UnityEngine;
using UnityEngine.Rendering;

namespace KaCMultiplayer.LoadSaveOverrides
{
    internal static class BuildMenuMaterials
    {
        // Vanilla skips inactive tabs. Network loads happen with the entire menu hidden,
        // leaving previews on world shaders instead of the dedicated build-menu material.
        public static void Refresh(bool log)
        {
            if (BuildUI.inst == null || World.inst == null) return;
            Player local = Player.inst;
            SessionPlayer session;
            if (Main.kCPlayers.TryGetValue(Main.PlayerSteamID, out session) && session.inst != null)
                local = session.inst;
            if (local == null || local.PlayerLandmassOwner == null) return;
            int banner = local.PlayerLandmassOwner.bannerIdx;
            if (banner < 0 || banner >= World.inst.liverySets.Count) return;
            Material material = World.inst.liverySets[banner].buildUIMaterial;
            if (material == null) return;

            int changed = 0, missingModels = 0, disabled = 0, missingMeshes = 0;
            var buttons = BuildUI.inst.GetComponentsInChildren<BuildingCostUpdater>(true);
            foreach (var button in buttons)
            {
                Transform mount = button.transform.Find("modelDisplay");
                if (mount == null) continue;
                var renderers = mount.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers.Length == 0) missingModels++;
                foreach (var renderer in renderers)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeSelf) disabled++;
                    var mesh = renderer.GetComponent<MeshFilter>();
                    if (mesh == null || mesh.sharedMesh == null) missingMeshes++;
                    Material[] slots = renderer.sharedMaterials;
                    bool replace = false;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        Material current = slots[i];
                        if (current == null || current.shader == null) continue;
                        string shader = current.shader.name;
                        bool buildingMaterial = shader == "Custom/Building" || shader == "Custom/Snow2";
                        if (!buildingMaterial)
                            for (int livery = 0; livery < World.inst.liverySets.Count; livery++)
                                if (current == World.inst.liverySets[livery].buildUIMaterial)
                                {
                                    buildingMaterial = true;
                                    break;
                                }
                        if (!buildingMaterial) continue;
                        if (current == material) continue;
                        slots[i] = material;
                        replace = true;
                        changed++;
                    }
                    if (replace) renderer.sharedMaterials = slots;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }
            if (log)
                Main.helper.Log($"[BUILDUI] refreshed local banner={banner}: buttons={buttons.Length}, materialSlotsFixed={changed}, missingModels={missingModels}, disabledModels={disabled}, missingMeshes={missingMeshes}");
        }

        [HarmonyPatch(typeof(BuildUI), "UpdateMaterials")]
        internal class RefreshHiddenTabs
        {
            public static void Postfix()
            {
                if (!NetClient.client.IsConnected && !SessionSave.Unpacking) return;
                try { Refresh(false); }
                catch (Exception ex) { Main.LogEx("refreshing hidden build-menu materials", ex); }
            }
        }

        [HarmonyPatch(typeof(BuildUI), "Start")]
        internal class RefreshNewMenu
        {
            public static void Postfix()
            {
                if (!NetClient.client.IsConnected && !SessionSave.Unpacking) return;
                try { Refresh(false); }
                catch (Exception ex) { Main.LogEx("initializing build-menu materials", ex); }
            }
        }
    }
}
