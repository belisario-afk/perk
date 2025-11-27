using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PerkMachines", "KillaDome (fixed by Copilot)", "2.1.0")]
    [Description("Perk machines: walk up, press E to buy perks via external currency plugin, plus perk effects & HUD.")]
    public class PerkMachines : RustPlugin
    {
        #region Plugin Reference

        [PluginReference] private Plugin ImageLibrary;

        #endregion

        #region Config/Data

        private class PerkConfig
        {
            // Correct tea shortname (in case you still want to use items later)
            public string TeaShortname = "maxhealthtea.pure";

            // Machine skins per perk
            public Dictionary<string, ulong> MachineSkins = new Dictionary<string, ulong>
            {
                {"Juggernog", 3613126822},
                {"SpeedCola", 3613111201},
                {"QuickRevive", 3613119446},
                {"DoubleTap", 3613106495}
            };

            // Item skin → perk; also used for machine skin reverse lookup
            public Dictionary<ulong, string> SkinToPerk = new Dictionary<ulong, string>
            {
                {3613126822, "Juggernog"},
                {3613111201, "SpeedCola"},
                {3613119446, "QuickRevive"},
                {3613106495, "DoubleTap"}
            };

            // Default price (can be overridden by economy plugin)
            public int DefaultPrice = 100;
            public string DefaultCurrencyLabel = "scrap"; // label only, real charging is external

            // Perk effects
            public float JuggernogBonus = 50f;
            public float JuggernogMaxCap = 200f;
            public float BasePlayerHealth = 100f; // Base player health before Juggernog
            public float DoubleTapMultiplier = 2f;
            public float QuickReviveRespawnHealth = 100f;

            public bool LockSpawnedMachines = true;
            public bool Debug = false;
            public int MaxPerks = 4;

            public Dictionary<string, string> PerkIconUrls = new Dictionary<string, string>
            {
                {"Juggernog", "https://yourserver.com/icons/jugg.png"},
                {"SpeedCola", "https://yourserver.com/icons/speed.png"},
                {"QuickRevive", "https://yourserver.com/icons/revive.png"},
                {"DoubleTap", "https://yourserver.com/icons/double.png"}
            };
        }

        private PerkConfig cfg;

        private class PlayerPerkData
        {
            public HashSet<string> Active = new HashSet<string>();
            public Dictionary<string, double> ExpireAt = new Dictionary<string, double>();
            public float ExtraHealthGiven = 0f;
        }

        private readonly Dictionary<ulong, PlayerPerkData> playerPerks = new Dictionary<ulong, PlayerPerkData>();

        private const string IL_CATEGORY = "PerkIcons";
        private bool ilReady;

        #endregion

        #region Lifecycle

        protected override void LoadDefaultConfig()
        {
            cfg = new PerkConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                cfg = Config.ReadObject<PerkConfig>() ?? new PerkConfig();
            }
            catch
            {
                cfg = new PerkConfig();
            }

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(cfg, true);

        private void OnServerInitialized()
        {
            timer.Once(2f, LoadImages);
            timer.Every(1f, CheckExpired);
            timer.Every(1f, UpdateHealthUI); // Update health bar periodically
            Puts("PerkMachines v2.1.0 loaded");
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, "perk_ui_container");
                CuiHelper.DestroyUi(player, "perk_health_ui");
            }

            playerPerks.Clear();
        }

        private void UpdateHealthUI()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                var d = GetPerkData(player.userID);
                if (d.Active.Contains("Juggernog") && d.ExtraHealthGiven > 0)
                {
                    RefreshHealthUI(player);
                }
            }
        }

        private void RefreshHealthUI(BasePlayer player)
        {
            if (player == null)
                return;

            var d = GetPerkData(player.userID);
            CuiHelper.DestroyUi(player, "perk_health_ui");

            if (!d.Active.Contains("Juggernog") || d.ExtraHealthGiven <= 0)
                return;

            var container = new CuiElementContainer();

            // Background panel for extended health bar
            var healthBgPanel = new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.8" },
                RectTransform = { AnchorMin = "0.318 0.027", AnchorMax = "0.42 0.055" },
                CursorEnabled = false
            };
            container.Add(healthBgPanel, "Hud", "perk_health_ui");

            // Calculate fill based on extra health remaining
            // Show the actual bonus health given, not calculated from current health
            float maxExtra = cfg.JuggernogBonus;
            float currentExtra = d.ExtraHealthGiven;
            
            // If player has taken damage, calculate remaining bonus health
            if (player.health < cfg.BasePlayerHealth + d.ExtraHealthGiven)
            {
                currentExtra = Math.Max(0f, player.health - cfg.BasePlayerHealth);
            }
            
            float fillPercent = Mathf.Clamp01(currentExtra / maxExtra);

            // Extended health bar fill (red color to match Rust's health bar)
            var healthFill = new CuiPanel
            {
                Image = { Color = "0.8 0.2 0.2 1" },
                RectTransform = 
                { 
                    AnchorMin = "0.02 0.15", 
                    AnchorMax = $"{0.02f + 0.96f * fillPercent} 0.85" 
                },
                CursorEnabled = false
            };
            container.Add(healthFill, "perk_health_ui", "perk_health_fill");

            // Label showing extra health
            var healthLabel = new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1"
                },
                Text =
                {
                    Text = $"+{Mathf.RoundToInt(currentExtra)}",
                    FontSize = 10,
                    Color = "1 1 1 1",
                    Align = TextAnchor.MiddleCenter
                }
            };
            container.Add(healthLabel, "perk_health_ui");

            CuiHelper.AddUi(player, container);
        }

        private void LoadImages()
        {
            if (ImageLibrary == null)
            {
                PrintWarning("ImageLibrary not found — perk icons will not load.");
                return;
            }

            var urls = new List<string>();
            foreach (var kv in cfg.PerkIconUrls)
                urls.Add(kv.Value);

            try
            {
                ImageLibrary.Call("AddImageList", IL_CATEGORY, urls, (ulong)0);
                ilReady = true;
                Puts($"[PerkMachines] Registered {urls.Count} icon URLs with ImageLibrary.");
            }
            catch (Exception ex)
            {
                PrintWarning($"ImageLibrary.AddImageList failed: {ex.Message}");
                ilReady = false;
            }
        }

        #endregion

        #region Utilities

        private PlayerPerkData GetPerkData(ulong uid)
        {
            if (!playerPerks.TryGetValue(uid, out var d))
            {
                d = new PlayerPerkData();
                playerPerks[uid] = d;
            }

            return d;
        }

        private void DebugMsg(string msg)
        {
            if (cfg.Debug)
                Puts("[PerkMachines] " + msg);
        }

        private BasePlayer FindPlayerByName(string name)
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p.displayName != null &&
                    p.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return p;
            }

            return null;
        }

        #endregion

        #region Perk granting & expiration

        private void GrantPerk(BasePlayer player, string perk)
        {
            if (player == null || string.IsNullOrEmpty(perk))
                return;

            var d = GetPerkData(player.userID);
            if (d.Active.Contains(perk))
            {
                player.ChatMessage($"Perk {perk} already active");
                return;
            }

            var can = Interface.CallHook("CanApplyPerk", player, perk);
            if (can is bool b && !b)
            {
                DebugMsg($"CanApplyPerk blocked for {player.userID} {perk}");
                return;
            }

            float duration = 300f;
            if (perk == "QuickRevive")
                duration = 0f;

            d.Active.Add(perk);
            if (duration > 0f)
                d.ExpireAt[perk] = Time.realtimeSinceStartup + duration;

            ApplyPerkEffect(player, perk, true);
            player.ChatMessage($"Perk activated: {perk}");
            Interface.CallHook("OnPerkGranted", player, perk);
            RefreshUI(player);
            DebugMsg($"Granted {perk} to {player.userID}");
        }

        private void RevokePerk(BasePlayer player, string perk)
        {
            if (player == null || string.IsNullOrEmpty(perk))
                return;

            var d = GetPerkData(player.userID);
            if (!d.Active.Contains(perk))
                return;

            d.Active.Remove(perk);
            d.ExpireAt.Remove(perk);

            ApplyPerkEffect(player, perk, false);
            Interface.CallHook("OnPerkExpired", player, perk);
            player.ChatMessage($"Perk expired: {perk}");
            RefreshUI(player);
            DebugMsg($"Revoked {perk} from {player.userID}");
        }

        private void CheckExpired()
        {
            var now = Time.realtimeSinceStartup;
            var toRevoke = new List<(ulong uid, string perk)>();

            foreach (var kv in playerPerks)
            {
                foreach (var ex in kv.Value.ExpireAt)
                {
                    if (ex.Value <= now)
                        toRevoke.Add((kv.Key, ex.Key));
                }
            }

            foreach (var t in toRevoke)
            {
                var p = BasePlayer.FindByID(t.uid);
                if (p != null)
                {
                    RevokePerk(p, t.perk);
                }
                else if (playerPerks.TryGetValue(t.uid, out var d))
                {
                    d.Active.Remove(t.perk);
                    d.ExpireAt.Remove(t.perk);
                }
            }
        }

        #endregion

        #region Perk effects

        private void ApplyPerkEffect(BasePlayer player, string perk, bool apply)
        {
            if (player == null)
                return;

            var d = GetPerkData(player.userID);

            switch (perk)
            {
                case "Juggernog":
                    if (apply)
                    {
                        float give = cfg.JuggernogBonus;
                        float currentMax = player.health + d.ExtraHealthGiven;

                        if (currentMax + give > cfg.JuggernogMaxCap)
                            give = Math.Max(0f, cfg.JuggernogMaxCap - currentMax);

                        d.ExtraHealthGiven += give;
                        player.health = Mathf.Min(player.health + give, cfg.JuggernogMaxCap);
                        player.SendNetworkUpdate(); // Sync health to client
                        player.ChatMessage($"Juggernog active (+{give} HP)");
                    }
                    else
                    {
                        player.health = Mathf.Max(1f, player.health - d.ExtraHealthGiven);
                        d.ExtraHealthGiven = 0f;
                        player.ChatMessage("Juggernog expired");
                    }
                    break;

                case "SpeedCola":
                    if (apply)
                        player.ChatMessage("Speed Cola active: instant reload");
                    else
                        player.ChatMessage("Speed Cola expired");
                    break;

                case "DoubleTap":
                    if (apply)
                        player.ChatMessage("Double Tap active: increased damage");
                    else
                        player.ChatMessage("Double Tap expired");
                    break;

                case "QuickRevive":
                    if (apply)
                    {
                        TryInstantRevive(player);
                        player.ChatMessage("Quick Revive applied");
                    }
                    else
                    {
                        player.ChatMessage("Quick Revive expired");
                    }
                    break;
            }
        }

        private void TryInstantRevive(BasePlayer player)
        {
            try
            {
                if (player == null)
                    return;

                if (player.IsWounded())
                {
                    // Stop the wounded state properly
                    player.StopWounded();
                    player.health = cfg.QuickReviveRespawnHealth;
                    if (player.metabolism != null)
                        player.metabolism.bleeding.value = 0f;
                    player.SendNetworkUpdate();
                    DebugMsg($"Instant revived {player.displayName}");
                }
            }
            catch (Exception ex)
            {
                DebugMsg("TryInstantRevive error: " + ex.Message);
            }
        }

        // Hook for when player becomes wounded - auto-revive if they have QuickRevive
        private void OnPlayerWound(BasePlayer player)
        {
            if (player == null)
                return;

            var d = GetPerkData(player.userID);
            if (d.Active.Contains("QuickRevive"))
            {
                // Use a short timer to let the wound state fully apply first
                timer.Once(0.5f, () =>
                {
                    if (player != null && player.IsConnected && player.IsWounded())
                    {
                        TryInstantRevive(player);
                        // QuickRevive is consumed after use
                        RevokePerk(player, "QuickRevive");
                    }
                });
            }
        }

        #endregion

        #region Combat hooks

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Protect perk vending machines from all damage
            var vm = entity as VendingMachine;
            if (vm != null && cfg.SkinToPerk.ContainsKey(vm.skinID))
            {
                DebugMsg($"Blocked damage to perk machine: {vm.shopName}");
                return true; // Block all damage to perk machines
            }

            if (info?.InitiatorPlayer == null)
                return null;

            var inst = info.InitiatorPlayer;
            var d = GetPerkData(inst.userID);

            if (d.Active.Contains("DoubleTap"))
            {
                info.damageTypes.ScaleAll(cfg.DoubleTapMultiplier);
                DebugMsg($"DoubleTap: Scaled damage by {cfg.DoubleTapMultiplier}x for {inst.displayName}");
            }

            return null;
        }

        // SpeedCola: Faster reload by adding ammo directly and reducing reload time
        private void OnReloadWeapon(BasePlayer player, BaseProjectile weapon)
        {
            if (player == null || weapon == null)
                return;

            var d = GetPerkData(player.userID);
            if (!d.Active.Contains("SpeedCola"))
                return;

            // Speed up reload by immediately adding ammo
            timer.Once(0.1f, () =>
            {
                if (player == null || !player.IsConnected || weapon == null || weapon.IsDestroyed)
                    return;

                try
                {
                    var ammoType = weapon.primaryMagazine?.ammoType;
                    if (ammoType == null)
                        return;

                    // Find ammo in player inventory
                    int needed = weapon.primaryMagazine.capacity - weapon.primaryMagazine.contents;
                    if (needed <= 0)
                        return;

                    int found = player.inventory.GetAmount(ammoType.itemid);
                    int toLoad = Math.Min(needed, found);

                    if (toLoad > 0)
                    {
                        player.inventory.Take(null, ammoType.itemid, toLoad);
                        weapon.primaryMagazine.contents += toLoad;
                        weapon.SendNetworkUpdateImmediate();
                        DebugMsg($"SpeedCola: Fast-loaded {toLoad} ammo for {player.displayName}");
                    }
                }
                catch (Exception ex)
                {
                    DebugMsg($"SpeedCola reload error: {ex.Message}");
                }
            });
        }

        #endregion

        #region Perk machine spawn (visual only)

        [ChatCommand("spawnperk")]
        private void SpawnPerkCommand(BasePlayer player, string cmd, string[] args)
        {
            if (player == null)
                return;

            if (args == null || args.Length == 0)
            {
                player.ChatMessage("Usage: /spawnperk <jug|speed|revive|double>");
                return;
            }

            string key = args[0].ToLower();
            string perkName = key switch
            {
                "jug" => "Juggernog",
                "speed" => "SpeedCola",
                "revive" => "QuickRevive",
                "double" => "DoubleTap",
                _ => null
            };

            if (perkName == null)
            {
                player.ChatMessage("Invalid perk. Use: jug/speed/revive/double");
                return;
            }

            var pos = player.transform.position + player.transform.forward * 2f + Vector3.up * 0.1f;
            var rot = Quaternion.LookRotation(-player.transform.forward, Vector3.up);

            SpawnPerkMachine(pos, rot, perkName);
        }

        private void SpawnPerkMachine(Vector3 pos, Quaternion rot, string perkName)
        {
            DebugMsg($"SpawnPerkMachine called for '{perkName}' at {pos}");

            if (!cfg.MachineSkins.ContainsKey(perkName))
            {
                DebugMsg($"SpawnPerkMachine: missing skin for {perkName}");
                return;
            }

            const string prefab = "assets/prefabs/deployable/vendingmachine/vendingmachine.deployed.prefab";

            var entity = GameManager.server.CreateEntity(prefab, pos, rot, true);
            if (entity == null)
            {
                PrintError($"SpawnPerkMachine: CreateEntity returned null for prefab '{prefab}'");
                return;
            }

            var vm = entity as VendingMachine;
            if (vm == null)
            {
                PrintError($"SpawnPerkMachine: entity is not a VendingMachine (type = {entity.GetType().FullName})");
                entity.Kill();
                return;
            }

            vm.Spawn();

            vm.skinID = cfg.MachineSkins[perkName];
            vm.shopName = $"{perkName} Machine";

            // Clear any sell orders and inventory: we don't use vending UI
            if (vm.sellOrders != null && vm.sellOrders.sellOrders != null)
                vm.sellOrders.sellOrders.Clear();
            vm.inventory?.Clear();

            if (cfg.LockSpawnedMachines)
            {
                try
                {
                    vm.SetFlag(BaseEntity.Flags.Locked, true);
                }
                catch (Exception ex)
                {
                    PrintWarning($"Failed to lock vending machine: {ex.Message}");
                }
            }

            vm.SendNetworkUpdateImmediate();

            Interface.CallHook("OnPerkVendingSpawned", vm, perkName);
            DebugMsg($"Spawned {perkName} machine (entID={vm.net?.ID}) at {vm.transform.position}");
        }

        #endregion

        #region Interaction: press E to buy

        // When a player tries to loot (press E) a vending machine
        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null)
                return null;

            var vm = entity as VendingMachine;
            if (vm == null)
                return null;

            // We only handle machines whose skinID maps to a perk
            if (!cfg.SkinToPerk.TryGetValue(vm.skinID, out var perkName))
                return null; // not a perk machine, allow default behavior

            // This is our perk machine: handle purchase directly, block UI
            TryBuyPerkFromMachine(player, vm, perkName);
            return false; // block the normal vending UI
        }

        private void TryBuyPerkFromMachine(BasePlayer player, VendingMachine vm, string perkName)
        {
            if (player == null || vm == null || string.IsNullOrEmpty(perkName))
                return;

            // Optional external pre-check: CanBuyPerk(buyer, perk, vm, null)
            var can = Interface.CallHook("CanBuyPerk", player, perkName, vm, null);
            if (can is bool b && !b)
            {
                DebugMsg($"External CanBuyPerk blocked {perkName} for {player.displayName}");
                return;
            }

            int price = cfg.DefaultPrice;
            string currencyLabel = cfg.DefaultCurrencyLabel;

            // External plugin can override price/label: OnPerkPurchaseCost(player, perk, out cost, out label)
            try
            {
                var result = Interface.CallHook("OnPerkPurchaseCost", player, perkName, price, currencyLabel);
                if (result is object[] arr && arr.Length >= 2)
                {
                    if (arr[0] is int p) price = p;
                    if (arr[1] is string lab) currencyLabel = lab;
                }
            }
            catch (Exception ex)
            {
                DebugMsg($"OnPerkPurchaseCost hook error: {ex.Message}");
            }

            // External plugin actually charges currency: OnPerkPurchaseCharge(player, perk, price, currencyLabel)
            bool charged = true;
            try
            {
                var res = Interface.CallHook("OnPerkPurchaseCharge", player, perkName, price, currencyLabel);
                if (res is bool cb)
                    charged = cb;
            }
            catch (Exception ex)
            {
                DebugMsg($"OnPerkPurchaseCharge hook error: {ex.Message}");
                charged = false;
            }

            if (!charged)
            {
                player.ChatMessage($"You cannot afford {perkName} ({price} {currencyLabel}).");
                return;
            }

            // If charge succeeded, grant perk
            GrantPerk(player, perkName);
            Interface.CallHook("OnPerkPurchased", player, perkName, vm);
        }

        #endregion

        #region HUD / ImageLibrary UI

        private void RefreshUI(BasePlayer player)
        {
            if (player == null)
                return;

            CuiHelper.DestroyUi(player, "perk_ui_container");

            var d = GetPerkData(player.userID);

            // Update the health UI
            RefreshHealthUI(player);

            // Separate perk icons UI (top left)
            if (d.Active.Count == 0)
                return;

            var perkContainer = new CuiElementContainer();

            var mainPanel = new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.015 0.85", AnchorMax = "0.25 0.97" },
                CursorEnabled = false
            };

            perkContainer.Add(mainPanel, "Hud", "perk_ui_container");

            float y = 0.8f;
            foreach (var perk in d.Active)
            {
                cfg.PerkIconUrls.TryGetValue(perk, out var url);
                string rawImage = GetImageCached(url) ?? string.Empty;

                var iconElement = new CuiElement
                {
                    Parent = "perk_ui_container",
                    Components =
                    {
                        new CuiRawImageComponent { Png = rawImage, Color = "1 1 1 1" },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"0 {y}",
                            AnchorMax = $"0.15 {y + 0.15f}"
                        }
                    }
                };
                perkContainer.Add(iconElement);

                double rem = d.ExpireAt.ContainsKey(perk)
                    ? d.ExpireAt[perk] - Time.realtimeSinceStartup
                    : -1;

                string text = rem > 0 ? $"{perk}: {Math.Round(rem)}s" : perk;

                var label = new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = $"0.16 {y}",
                        AnchorMax = $"1 {y + 0.15f}"
                    },
                    Text =
                    {
                        Text = text,
                        FontSize = 14,
                        Color = "1 1 1 1",
                        Align = TextAnchor.MiddleLeft
                    }
                };

                perkContainer.Add(label, "perk_ui_container");

                y -= 0.18f;
            }

            CuiHelper.AddUi(player, perkContainer);
        }

        private string GetImageCached(string url)
        {
            if (ImageLibrary == null || !ilReady || string.IsNullOrEmpty(url))
                return null;

            try
            {
                return ImageLibrary.Call<string>("GetImage", IL_CATEGORY, url);
            }
            catch
            {
                return null;
            }
        }

        private void RefreshAllUI()
        {
            foreach (var p in BasePlayer.activePlayerList)
                RefreshUI(p);
        }

        #endregion

        #region Commands: status / grant

        [ChatCommand("perks")]
        private void PerksCommand(BasePlayer player, string cmd, string[] args)
        {
            if (player == null)
                return;

            if (args == null || args.Length == 0)
            {
                player.ChatMessage("PerkMachines: /perks status | /perks list | /perks grant <perk> <player>");
                return;
            }

            var sub = args[0].ToLower();

            if (sub == "status")
            {
                var d = GetPerkData(player.userID);
                if (d.Active.Count == 0)
                {
                    player.ChatMessage("No active perks");
                    return;
                }

                foreach (var pk in d.Active)
                {
                    double rem = d.ExpireAt.ContainsKey(pk)
                        ? d.ExpireAt[pk] - Time.realtimeSinceStartup
                        : -1;

                    if (rem > 0)
                        player.ChatMessage($"{pk} - {Math.Round(rem)}s remaining");
                    else
                        player.ChatMessage($"{pk} - permanent/one-shot");
                }

                return;
            }

            if (sub == "list")
            {
                var listed = new HashSet<string>();
                foreach (var s in cfg.SkinToPerk.Values)
                {
                    if (listed.Add(s))
                        player.ChatMessage(s);
                }

                return;
            }

            if (sub == "grant" && args.Length >= 3)
            {
                if (!player.IsAdmin)
                {
                    player.ChatMessage("Admin only");
                    return;
                }

                var perk = args[1];
                var target = FindPlayerByName(args[2]);

                if (target == null)
                {
                    player.ChatMessage("Player not found");
                    return;
                }

                GrantPerk(target, perk);
                player.ChatMessage($"Granted {perk} to {target.displayName}");
                return;
            }

            player.ChatMessage("Unknown subcommand");
        }

        #endregion
    }
}