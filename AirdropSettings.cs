using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using System;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Airdrop Settings", "VisEntities", "1.1.0")]
    [Description("Allows customization of airdrops and cargo planes.")]
    public class AirdropSettings : RustPlugin
    {
        #region Fields

        private static AirdropSettings _plugin;
        private static Configuration _config;
        private Harmony _harmony;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Cargo Plane Spawn Height")]
            public float CargoPlaneSpawnHeight { get; set; }

            [JsonProperty("Cargo Plane Speed Level")]
            [JsonConverter(typeof(StringEnumConverter))]
            public SpeedLevel CargoPlaneSpeedLevel { get; set; }

            [JsonProperty("Instant Drop Without Plane")]
            public bool InstantDropWithoutPlane { get; set; }

            [JsonProperty("Airdrop Fall Speed Level")]
            [JsonConverter(typeof(StringEnumConverter))]
            public SpeedLevel AirdropFallSpeedLevel { get; set; }

            [JsonProperty("Remove Airdrop Parachute")]
            public bool RemoveAirdropParachute { get; set; }

            [JsonProperty("Drop Airdrop Exactly At Supply Signal Position")]
            public bool DropAirdropExactlyAtSupplySignalPosition { get; set; }

            [JsonProperty("Supply Signal Smoke Duration Seconds")]
            public float SupplySignalSmokeDurationSeconds { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            if (string.Compare(_config.Version, "1.1.0") < 0)
            {
                _config.DropAirdropExactlyAtSupplySignalPosition = defaultConfig.DropAirdropExactlyAtSupplySignalPosition;
                _config.SupplySignalSmokeDurationSeconds = defaultConfig.SupplySignalSmokeDurationSeconds;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                CargoPlaneSpawnHeight = 250,
                CargoPlaneSpeedLevel = SpeedLevel.Normal,
                InstantDropWithoutPlane = false,
                AirdropFallSpeedLevel = SpeedLevel.Normal,
                RemoveAirdropParachute = false,
                DropAirdropExactlyAtSupplySignalPosition = false,
                SupplySignalSmokeDurationSeconds = 210f
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            _harmony = new Harmony(Name + "PATCH");
            _harmony.PatchAll();
        }

        private void Unload()
        {
            _harmony.UnpatchAll(Name + "PATCH");
            _config = null;
            _plugin = null;
        }

        private object OnCargoPlaneUpdateDropPosition(CargoPlane cargoPlane, Vector3 newDropPosition)
        {
            float x = TerrainMeta.Size.x;
            float y = TerrainMeta.HighestPoint.y + _config.CargoPlaneSpawnHeight;
            cargoPlane.startPos = Vector3Ex.Range(-1f, 1f);
            cargoPlane.startPos.y = 0f;
            cargoPlane.startPos.Normalize();
            cargoPlane.startPos *= x * 2f;
            cargoPlane.startPos.y = y;
            cargoPlane.endPos = cargoPlane.startPos * -1f;
            cargoPlane.endPos.y = cargoPlane.startPos.y;
            cargoPlane.startPos += newDropPosition;
            cargoPlane.endPos += newDropPosition;
            cargoPlane.secondsToTake = Vector3.Distance(cargoPlane.startPos, cargoPlane.endPos) / 50f / GetCargoPlaneSpeed(_config.CargoPlaneSpeedLevel);
            cargoPlane.secondsToTake *= UnityEngine.Random.Range(0.95f, 1.05f);
            cargoPlane.transform.position = cargoPlane.startPos;
            cargoPlane.transform.rotation = Quaternion.LookRotation(cargoPlane.endPos - cargoPlane.startPos);
            cargoPlane.dropPosition = newDropPosition;
            Interface.CallHook("OnAirdrop", cargoPlane, newDropPosition);

            return true;
        }

        private void OnAirdrop(CargoPlane cargoPlane, Vector3 dropPosition)
        {
            if (_config.InstantDropWithoutPlane)
            {
                // Adjust the drop position height to simulate the altitude from which the cargo plane would drop the airdrop.
                float y = TerrainMeta.HighestPoint.y + _config.CargoPlaneSpawnHeight;
                dropPosition.y = y;

                BaseEntity supplyDrop = GameManager.server.CreateEntity(cargoPlane.prefabDrop.resourcePath, dropPosition, Quaternion.identity);
                if (supplyDrop != null)
                {
                    supplyDrop.globalBroadcast = true;
                    supplyDrop.Spawn();
                    // Call it manually since the cargo plane is killed and won't trigger it.
                    Interface.CallHook("OnSupplyDropDropped", supplyDrop, cargoPlane);
                }

                // Delay the killing of the cargo plane to ensure it's available for plugins using the 'OnSupplyDropDropped' hook.
                NextTick(() =>
                {
                    cargoPlane.Kill();
                });
            }
        }

        private void OnEntitySpawned(SupplyDrop supplyDrop)
        {
            if (supplyDrop == null)
                return;

            Rigidbody rigidbody = supplyDrop.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                if (_config.RemoveAirdropParachute)
                {
                    supplyDrop.RemoveParachute();
                    rigidbody.drag = 0.5f; // To simulate free fall without parachute.
                }
                else
                {
                    rigidbody.drag = GetAirdropFallSpeed(_config.AirdropFallSpeedLevel);
                }
            }
        }

        private object OnSupplySignalExplode(SupplySignal supplySignal)
        {
            CargoPlane cargoPlane = GameManager.server.CreateEntity(supplySignal.EntityToCreate.resourcePath, supplySignal.transform.position, Quaternion.identity) as CargoPlane;
            if (cargoPlane != null)
            {
                Vector3 dropPosition = supplySignal.transform.position;

                if (!_config.DropAirdropExactlyAtSupplySignalPosition)
                {
                    Vector3 randomOffset = new Vector3(UnityEngine.Random.Range(-20f, 20f), 0f, UnityEngine.Random.Range(-20f, 20f));
                    dropPosition += randomOffset;
                }

                cargoPlane.InitDropPosition(dropPosition);
                cargoPlane.Spawn();
                // Call it manually since we're blocking the original method.
                Interface.CallHook("OnCargoPlaneSignaled", cargoPlane, supplySignal);
            }

            supplySignal.Invoke(new Action(supplySignal.FinishUp), _config.SupplySignalSmokeDurationSeconds);
            supplySignal.SetFlag(BaseEntity.Flags.On, true, false, true);
            supplySignal.SendNetworkUpdateImmediate(false);

            return true;
        }

        #endregion Oxide Hooks

        #region Cargo Plane and Airdrop Speed

        public enum SpeedLevel
        {
            VerySlow,
            Slow,
            Normal,
            Fast,
            VeryFast
        }

        private float GetCargoPlaneSpeed(SpeedLevel speedLevel)
        {
            switch (speedLevel)
            {
                case SpeedLevel.VerySlow:
                    return 0.5f;
                case SpeedLevel.Slow:
                    return 0.75f;
                case SpeedLevel.Normal:
                    return 1f;
                case SpeedLevel.Fast:
                    return 2f;
                case SpeedLevel.VeryFast:
                    return 4f;
                default:
                    return 1f;
            }
        }

        private float GetAirdropFallSpeed(SpeedLevel speedLevel)
        {
            // Drag value must be at least 0.5f to prevent airdrops from falling through terrain.
            switch (speedLevel)
            {
                case SpeedLevel.VerySlow:
                    return 5f;
                case SpeedLevel.Slow:
                    return 4f;
                case SpeedLevel.Normal:
                    return 3f;
                case SpeedLevel.Fast:
                    return 1f;
                case SpeedLevel.VeryFast:
                    return 0.5f;
                default:
                    return 3f;
            }
        }

        #endregion Cargo Plane and Airdrop Speed

        #region Harmony Patches

        [HarmonyPatch(typeof(CargoPlane), "UpdateDropPosition")]
        public static class CargoPlane_UpdateDropPosition_Patch
        {
            public static bool Prefix(CargoPlane __instance, Vector3 newDropPosition)
            {
                if (Interface.CallHook("OnCargoPlaneUpdateDropPosition", __instance, newDropPosition) != null)
                {
                    // Return a non-null value to block the original method, null to allow it.
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(SupplySignal), "Explode")]
        public static class SupplySignal_Explode_Patch
        {
            public static bool Prefix(SupplySignal __instance)
            {
                if (Interface.CallHook("OnSupplySignalExplode", __instance) != null)
                {
                    // Return a non-null value to block the original method, null to allow it.
                    return false;
                }

                return true;
            }
        }

        #endregion Harmony Patches
    }
}