using System.Reflection;
using UnityEngine;
using BepInEx;
using HarmonyLib;
using LethalLib.Modules;
using BepInEx.Logging;
using System.IO;
using SCP106.Configuration;
using System;
using System.Collections.Generic;
using DunGen;
using GameNetcodeStuff;

namespace SCP106 {
    [BepInPlugin(ModGUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)] 
    public class Plugin : BaseUnityPlugin {
        // It is a good idea for our GUID to be more unique than only the plugin name. Notice that it is used in the BepInPlugin attribute.
        // The GUID is also used for the config file name by default.
        public const string ModGUID = "Dackie." + PluginInfo.PLUGIN_NAME;
        internal static new ManualLogSource Logger;
        internal static PluginConfig BoundConfig { get; private set; } = null;
        public static AssetBundle ModAssets;
        private readonly Harmony harmony = new Harmony(ModGUID);

        private void Awake() {
            Logger = base.Logger;

            // If you don't want your mod to use a configuration file, you can remove this line, Configuration.cs, and other references.
            BoundConfig = new PluginConfig(this);

            // This should be ran before Network Prefabs are registered.
            InitializeNetworkBehaviours();

            // We load the asset bundle that should be next to our DLL file, with the specified name.
            // You may want to rename your asset bundle from the AssetBundle Browser in order to avoid an issue with
            // asset bundle identifiers being the same between multiple bundles, allowing the loading of only one bundle from one mod.
            // In that case also remember to change the asset bundle copying code in the csproj.user file.
            var bundleName = "scp106";
            ModAssets = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Info.Location), bundleName));
            if (ModAssets == null) {
                Logger.LogError($"Failed to load custom assets.");
                return;
            }

            // We load our assets from our asset bundle. Remember to rename them both here and in our Unity project.
            var SCP106 = ModAssets.LoadAsset<EnemyType>("SCP106");
            var SCP106TN = ModAssets.LoadAsset<TerminalNode>("SCP106TN");
            var SCP106TK = ModAssets.LoadAsset<TerminalKeyword>("SCP106TK");
            var PocketDimension = (GameObject)ModAssets.LoadAsset("pocketdimension");
            var personalAudio = (GameObject)ModAssets.LoadAsset("pdPersonalAudio");
            var corrosionDecalProjector = (GameObject)ModAssets.LoadAsset("corrosionDecalProjector");
            
            // Network Prefabs need to be registered. See https://docs-multiplayer.unity3d.com/netcode/current/basics/object-spawning/
            // LethalLib registers prefabs on GameNetworkManager.Start.
            NetworkPrefabs.RegisterNetworkPrefab(SCP106.enemyPrefab);
            NetworkPrefabs.RegisterNetworkPrefab(PocketDimension);
            NetworkPrefabs.RegisterNetworkPrefab(personalAudio);
            NetworkPrefabs.RegisterNetworkPrefab(corrosionDecalProjector); // HDRP Decal Projector
			//Enemies.RegisterEnemy(SCP106, BoundConfig.SpawnWeight.Value, Levels.LevelTypes.All, Enemies.SpawnType.Default, SCP106TN, SCP106TK);
            harmony.PatchAll();

            // Spawn Weight by Level
            (Dictionary<Levels.LevelTypes, int> spawnRateByLevelType, Dictionary<string, int> spawnRateByCustomLevelType) = GetSpawnRarities(BoundConfig.SpawnWeight.Value);

            // Check if the spawn rate parsed correctly. If failed, default to spawn rate 25.
            if (spawnRateByLevelType != null && spawnRateByCustomLevelType != null){
			    Enemies.RegisterEnemy(SCP106, Enemies.SpawnType.Default, spawnRateByLevelType, spawnRateByCustomLevelType, SCP106TN, SCP106TK);
            } else{
                Logger.LogInfo("Failed to parse Spawn Rate Configuration. Defaulted to Global Spawn Rate of 25 for SCP-106.");
                Enemies.RegisterEnemy(SCP106,25,Levels.LevelTypes.All,Enemies.SpawnType.Default,SCP106TN,SCP106TK);
            }
            
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        // Input config is of the format "LevelName:Value;"
        private (Dictionary<Levels.LevelTypes, int> levelRarities, Dictionary<string, int> customLevelRarities) GetSpawnRarities(string config){
            Dictionary<Levels.LevelTypes, int> levelRare = [];
            Dictionary<string, int> customRare = [];
            string[] moonAndWeights = config.Split(';');
            foreach (string item in moonAndWeights)
            {
                try{
                    string[] splitItem = item.Split(':');
                    string moon = splitItem[0];
                    string weight = splitItem[1];
                    // Check if it's a standard level
                    if(Enum.TryParse<Levels.LevelTypes>(moon, true, out Levels.LevelTypes levelType)){
                        // Set weight to given integer. If not parseable, set to 0.
                        levelRare[levelType] = int.TryParse(weight, out int spawnRate) ? spawnRate : 25;
                    }
                    // Else, add as custom
                    else{
                        customRare[moon] = int.TryParse(weight, out int spawnRate) ? spawnRate : 25;
                    }
                }catch{
                    return (null,null);
                }
            }

            return (levelRare,customRare);
        }

        private static void InitializeNetworkBehaviours() {
            // See https://github.com/EvaisaDev/UnityNetcodePatcher?tab=readme-ov-file#preparing-mods-for-patching
            var types = Assembly.GetExecutingAssembly().GetTypes();
            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);
                    if (attributes.Length > 0)
                    {
                        method.Invoke(null, null);
                    }
                }
            }
        }
        [HarmonyPatch(typeof(OutOfBoundsTrigger))]
        [HarmonyPatch("OnTriggerEnter")]
        internal class OutOfBoundsTriggersPatch{
            // NOTE: Possible Transpiler fix needed, instead of prefix
            static bool Prefix(Collider other){
                PlayerControllerB player = other.GetComponent<PlayerControllerB>();
                PocketDimController controller = FindObjectOfType<PocketDimController>();
                // Check if Player was the collider, and that Pocket Dimension has spawned
                if (controller != null && player != null){
                    // Check if the Player was inside the Pocket Dimension when this trigger was called
                    if(controller.playerIsInPD[(int)player.playerClientId]){
                        return false;
                    }
                }
                return true;
            }
        }
    }
}