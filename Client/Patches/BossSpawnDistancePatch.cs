using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BotPlacementSystemClient.Utils;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace BotPlacementSystemClient.Patches;

/// <summary>
/// Vanilla boss spawning picks a point inside the boss zone without checking where players are, so bosses
/// (and seasonal event bosses like the Halloween cultists) can spawn right next to a player.
/// Runs after vanilla has chosen the zone and points, before anything spawns:
/// - chosen point far enough from every human player: unchanged
/// - too close: swap in a far-enough point from the same zone (followers kept together)
/// - no far-enough point in the zone: hand the wave back to vanilla's delay queue to retry later
/// - no human player in the raid yet (start-of-raid bosses on a Fika/headless host spawn before clients load in):
///   wait for one to appear, then run the check, instead of letting the boss spawn unchecked
/// ABPS PMCs never reach this method (PmcSpawnHookPatch spawns them itself).
/// </summary>
internal class BossSpawnDistancePatch : ModulePatch
{
    private const float FollowerGroupRadius = 20f;

    // Waves already held back once waiting for players; if the wait timed out they spawn unchecked rather than looping
    private static readonly HashSet<BossLocationSpawn> WaitedForPlayers = new();

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BotBossSpawn), nameof(BotBossSpawn.SpawnBossAndFollowers));
    }

    [PatchPrefix]
    private static bool PatchPrefix(BotBossSpawn __instance, BotCreationData creationData, BossLocationSpawn wave, BotSpawnParams spawnParams, int followersCount, BotZone botZone, List<ISpawnPoint> openedPositions, ref Task __result)
    {
        try
        {
            if (!Plugin.BossSpawnDistanceCheck || wave == null || openedPositions == null || openedPositions.Count == 0) return true;
            if (wave.BossType is WildSpawnType.pmcUSEC or WildSpawnType.pmcBEAR) return true;
            // Triggered waves (Reserve lever Raiders, Halloween summoning, Kaban's snipers...) are meant to spawn near the trigger
            if (Plugin.BossSpawnDistanceIgnoreTriggered && !string.IsNullOrEmpty(wave.TriggerName)) return true;

            var humans = GetHumanPlayers();
            if (humans.Count == 0)
            {
                if (!WaitedForPlayers.Add(wave))
                {
                    WaitedForPlayers.Remove(wave);
                    Plugin.LogSource.LogWarning($"BossSpawnDistance | {wave.BossType} | Still no human players in the raid, spawning without the distance check");
                    return true;
                }

                Plugin.LogSource.LogInfo($"BossSpawnDistance | {wave.BossType} | No human players in the raid yet, holding the spawn until one loads in");
                __result = WaitForPlayersThenSpawn(__instance, creationData, wave, spawnParams, followersCount, botZone, openedPositions);
                return false;
            }
            WaitedForPlayers.Remove(wave);

            var distance = GetDistanceForMap(Utility.CurrentLocation);
            if (IsValid(openedPositions[0], humans, distance))
            {
                Plugin.LogSource.LogInfo($"BossSpawnDistance | {wave.BossType} | Spawn point OK in zone {botZone?.NameZone} | Nearest player: {NearestPlayerDistance(openedPositions[0], humans):F1}m (min {distance}m)");
                return true;
            }

            var replacement = GetReplacementPoints(botZone, humans, distance, openedPositions.Count);
            if (replacement.Count > 0)
            {
                Plugin.LogSource.LogInfo($"BossSpawnDistance | {wave.BossType} | Spawn point within {distance}m of a player, moved within zone {botZone?.NameZone} | Nearest player: {NearestPlayerDistance(replacement[0], humans):F1}m");

                openedPositions.Clear();
                openedPositions.AddRange(replacement);
                return true;
            }

            Plugin.LogSource.LogInfo($"BossSpawnDistance | {wave.BossType} | No spawn point in zone {botZone?.NameZone} at least {distance}m from players, delaying spawn");

            __instance.DelayBossSpawn(wave, spawnParams, creationData);
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogError($"BossSpawnDistancePatch EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return true;
        }
    }

    private static async Task WaitForPlayersThenSpawn(BotBossSpawn bossSpawn, BotCreationData creationData, BossLocationSpawn wave, BotSpawnParams spawnParams, int followersCount, BotZone botZone, List<ISpawnPoint> openedPositions)
    {
        await Utility.WaitForHumanPlayer();
        // Re-enters PatchPrefix, which now sees the players (or, after a timeout, lets the spawn through)
        await bossSpawn.SpawnBossAndFollowers(creationData, wave, spawnParams, followersCount, botZone, openedPositions);
    }

    private static List<Player> GetHumanPlayers()
    {
        var gameWorld = Singleton<GameWorld>.Instance;
        if (gameWorld?.AllAlivePlayersList == null) return new List<Player>();

        return gameWorld.AllAlivePlayersList
            .Where(p => p != null && !p.IsAI && !Utility.IsPlayerHeadless(p))
            .ToList();
    }

    private static List<ISpawnPoint> GetReplacementPoints(BotZone botZone, IReadOnlyCollection<Player> humans, float distance, int neededPoints)
    {
        var result = new List<ISpawnPoint>();
        if (botZone == null) return result;

        var zonePoints = Utility.GetZoneSpawnPoints(botZone);
        if (zonePoints.Count == 0)
        {
            zonePoints = botZone.SpawnPoints
                .Where(x => x.Categories == ESpawnCategoryMask.All || x.Categories.ContainBotCategory())
                .ToList();
        }

        var candidates = zonePoints
            .Where(p => IsValid(p, humans, distance))
            .OrderBy(_ => MyExtensions.Random(0f, 1f))
            .ToList();
        if (candidates.Count == 0) return result;

        var first = candidates[0];
        result.Add(first);
        foreach (var point in candidates.Skip(1))
        {
            if (result.Count >= neededPoints) break;
            if (Vector3.Distance(point.Position, first.Position) <= FollowerGroupRadius)
                result.Add(point);
        }

        // Same as vanilla: not enough points for the whole group -> followers share the leader's point
        while (result.Count < neededPoints)
            result.Add(first);

        return result;
    }

    private static bool IsValid(ISpawnPoint spawnPoint, IReadOnlyCollection<Player> players, float distance)
    {
        if (spawnPoint?.Collider == null) return false;

        foreach (var player in players)
        {
            Vector3 playerPosition;
            try
            {
                playerPosition = player.Position;
            }
            catch
            {
                continue;
            }

            if (spawnPoint.Collider.Contains(playerPosition)) return false;
            if (Vector3.Distance(spawnPoint.Position, playerPosition) < distance) return false;
        }

        return true;
    }

    private static float NearestPlayerDistance(ISpawnPoint spawnPoint, IReadOnlyCollection<Player> players)
    {
        return players
            .Select(p => Vector3.Distance(spawnPoint.Position, p.Position))
            .DefaultIfEmpty(float.MaxValue)
            .Min();
    }

    private static float GetDistanceForMap(string mapName)
    {
        var isSmallMap = mapName.Contains("factory") || mapName.Contains("laboratory") || mapName.Contains("labyrinth");
        return isSmallMap ? Plugin.BossSpawnDistanceSmallMaps : Plugin.BossSpawnDistance;
    }
}