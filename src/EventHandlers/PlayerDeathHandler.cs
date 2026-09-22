using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using System;

namespace QuakeSounds;

public partial class QuakeSounds
{
    private bool ShouldSkipKillByConfig(IPlayer attacker, IPlayer victim)
    {
        if (victim is { IsValid: true } && victim.PlayerID == attacker.PlayerID && !_config.CountSelfKills)
        {
            return true;
        }

        if (victim is { IsValid: true } && victim.PlayerID != attacker.PlayerID && !_config.CountTeamKills)
        {
            try
            {
                if (victim.Controller?.TeamNum == attacker.Controller?.TeamNum)
                {
                    return true;
                }
            }
            catch
            {
                // If team lookup fails for any reason, continue as a normal kill.
            }
        }

        return false;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (!IsPluginEnabled() || IsWarmupBlockedByConfig())
        {
            return HookResult.Continue;
        }

        var victim = @event.Accessor.GetPlayer("userid");
        var attacker = @event.Accessor.GetPlayer("attacker");

        var victimSteamId = victim is { IsValid: true } ? victim.SteamID : 0;
        var attackerSteamId = attacker is { IsValid: true } ? attacker.SteamID : 0;

        if (attacker is not { IsValid: true } || attacker.IsFakeClient)
        {
            return HookResult.Continue;
        }

        if (attacker.PlayerID <= 0 && attackerSteamId != 0)
        {
            foreach (var p in Core.PlayerManager.GetAllPlayers())
            {
                if (p is { IsValid: true } && !p.IsFakeClient && p.SteamID == attackerSteamId && p.PlayerID > 0)
                {
                    attacker = p;
                    break;
                }
            }
        }

        if (attacker.PlayerID <= 0)
        {
            return HookResult.Continue;
        }

        if (attackerSteamId != 0 && !_gameStateService.ShouldProcessDeathEvent(attackerSteamId, victimSteamId, @event.Headshot, @event.NoScope, @event.Weapon, 250))
        {
            return HookResult.Continue;
        }

        if (victim is { IsValid: true } && _config.ResetKillsOnDeath)
        {
            _gameStateService.ResetKillCount(victim.PlayerID);
        }

        if (ShouldSkipKillByConfig(attacker, victim))
        {
            return HookResult.Continue;
        }

        var killCount = _gameStateService.IncrementKillCount(attacker.PlayerID);
        var (multiKillCount, isMultiKill) = _gameStateService.UpdateMultiKill(attacker.PlayerID, _config.MultiKillWindowSeconds);

        if (_config.PrioritizeSpecialKills)
        {
            if (@event.Weapon.Contains("taser", StringComparison.OrdinalIgnoreCase) && TryPlay(attacker, "taser_kill"))
            {
                return HookResult.Continue;
            }

            if (@event.Weapon.Contains("knife", StringComparison.OrdinalIgnoreCase))
            {
                if (TryPlay(attacker, "knife_kill") || TryPlay(attacker, "humiliation"))
                {
                    return HookResult.Continue;
                }
            }

            if (@event.Headshot && TryPlay(attacker, "headshot")) return HookResult.Continue;
            if (@event.NoScope && TryPlay(attacker, "noscope")) return HookResult.Continue;
        }

        if (!_gameStateService.FirstBloodDone && victim is { IsValid: true } && victim.PlayerID != attacker.PlayerID)
        {
            _gameStateService.FirstBloodDone = true;
            if (TryPlay(attacker, "firstblood"))
            {
                return HookResult.Continue;
            }
        }

        if (!_config.PrioritizeSpecialKills)
        {
            if (@event.Weapon.Contains("taser", StringComparison.OrdinalIgnoreCase) && TryPlay(attacker, "taser_kill"))
            {
                return HookResult.Continue;
            }

            if (@event.Weapon.Contains("knife", StringComparison.OrdinalIgnoreCase))
            {
                if (TryPlay(attacker, "knife_kill") || TryPlay(attacker, "humiliation"))
                {
                    return HookResult.Continue;
                }
            }
        }

        if (killCount >= 6)
        {
            if (TryPlayKillStreak(attacker, killCount)) return HookResult.Continue;
        }
        else if (isMultiKill && multiKillCount >= 2 && multiKillCount <= 5)
        {
            if (TryPlayKillStreak(attacker, multiKillCount)) return HookResult.Continue;
        }
        else if (killCount >= 2)
        {
            if (TryPlayKillStreak(attacker, killCount)) return HookResult.Continue;
        }

        if (!_config.PrioritizeSpecialKills)
        {
            if (@event.Headshot && TryPlay(attacker, "headshot")) return HookResult.Continue;
            if (@event.NoScope && TryPlay(attacker, "noscope")) return HookResult.Continue;
        }

        if (@event.Weapon.Contains("hegrenade", StringComparison.OrdinalIgnoreCase) && TryPlay(attacker, "perfect"))
        {
            return HookResult.Continue;
        }

        if (victim is { IsValid: true } && victim.PlayerID == attacker.PlayerID && TryPlay(attacker, "perfect"))
        {
            return HookResult.Continue;
        }

        var weaponKey = NormalizeWeaponKey(@event.Weapon);
        if (TryPlay(attacker, weaponKey))
        {
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    private bool TryPlay(IPlayer attacker, string soundKey)
    {
        if (!IsPluginEnabled() || IsWarmupBlockedByConfig()) return false;

        var played = _soundService?.TryPlay(
            attacker,
            soundKey,
            _config,
            id => _gameStateService.IsPlayerEnabled(id),
            id => _gameStateService.GetPlayerVolume(id)
        ) ?? false;

        if (_config.Sounds.ContainsKey(soundKey) && (_config.EnableChatMessage || _config.EnableCenterMessage))
        {
            if (_config.PlayToAll)
            {
                _messageService.PrintMessageToAll(Core.PlayerManager.GetAllPlayers(), attacker, soundKey, _config);
            }
            else if (_gameStateService.IsPlayerEnabled(attacker.SteamID))
            {
                _messageService.PrintMessage(attacker, attacker, soundKey, _config);
            }
        }

        return played;
    }

    private bool TryPlayKillStreak(IPlayer attacker, int killCount)
    {
        if (_config.KillStreakAnnounces.TryGetValue(killCount, out var soundKey) && _config.Sounds.ContainsKey(soundKey))
        {
            return TryPlay(attacker, soundKey);
        }
        return false;
    }

    private static string NormalizeWeaponKey(string weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon)) return "weapon_unknown";

        return weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? weapon.ToLowerInvariant()
            : $"weapon_{weapon.ToLowerInvariant()}";
    }
}
