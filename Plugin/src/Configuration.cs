using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace SCP106.Configuration {
    public class PluginConfig
    {
        // For more info on custom configs, see https://lethal.wiki/dev/intermediate/custom-configs
        public ConfigEntry<int> SpawnWeight;
        public ConfigEntry<bool> Stunnable;
        public ConfigEntry<int> NonDeadlyInteractions;
        public ConfigEntry<bool> CanGoOutside;
        public ConfigEntry<bool> CanGoInsideShip;

        public PluginConfig(BaseUnityPlugin plugin)
        {
            SpawnWeight = plugin.Config.Bind("SCP-106", "SpawnWeight", 25,
                "The spawn chance weight for SCP-106, relative to other existing enemies.\n" +
                "Goes up from 0, lower is more rare, 100 and up is very common.");

            Stunnable = plugin.Config.Bind("SCP-106", "Stunnable", true,
                "Toggles if SCP-106 can be stunned or not. Does not apply for Stun-Gun yet.");

            NonDeadlyInteractions = plugin.Config.Bind("SCP-106", "NonDeadlyInteractions", 15,
                "Chance to perform non-deadly interactions with players. Goes from 0 (%) to 100 (%).");
            
            CanGoOutside = plugin.Config.Bind("SCP-106", "CanGoOutside", false,
                "Toggles if SCP-106 can go outside or not.");

            CanGoInsideShip = plugin.Config.Bind("SCP-106", "CanGoInsideShip", false,
                "Toggles if SCP-106 can inside the ship. Does nothing if he can't go outside.");
            
            ClearUnusedEntries(plugin);
        }

        private void ClearUnusedEntries(BaseUnityPlugin plugin) {
            // Normally, old unused config entries don't get removed, so we do it with this piece of code. Credit to Kittenji.
            PropertyInfo orphanedEntriesProp = plugin.Config.GetType().GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);
            var orphanedEntries = (Dictionary<ConfigDefinition, string>)orphanedEntriesProp.GetValue(plugin.Config, null);
            orphanedEntries.Clear(); // Clear orphaned entries (Unbinded/Abandoned entries)
            plugin.Config.Save(); // Save the config file to save these changes
        }
    }
}