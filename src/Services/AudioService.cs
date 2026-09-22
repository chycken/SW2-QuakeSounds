using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace QuakeSounds.Services;

public class AudioService : ISoundService
{
    private readonly ISwiftlyCore _core;
    private readonly dynamic _audioApi;
    private readonly ConcurrentDictionary<string, object> _decodedSources = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _setSourceMethodCache = new();
    private int _channelCounter = 0;

    private AudioService(ISwiftlyCore core, dynamic audioApi)
    {
        _core = core;
        _audioApi = audioApi;
    }

    public static ISoundService Create(ISwiftlyCore core, object audioApi)
    {
        return new AudioService(core, audioApi);
    }

    public void ClearCache()
    {
        _decodedSources.Clear();
        _channelCounter = 0;
    }

    public void RemovePlayerChannel(int playerId) { }

    public bool TryPlay(IPlayer attacker, string soundKey, QuakeSounds.QuakeSoundsConfig config, Func<ulong, bool> isPlayerEnabled, Func<ulong, float> getPlayerVolume)
    {
        if (_audioApi == null) return false;

        if (!config.Sounds.TryGetValue(soundKey, out var configuredPath) || string.IsNullOrWhiteSpace(configuredPath))
            return false;

        var resolvedPath = ResolvePath(configuredPath);
        if (!File.Exists(resolvedPath)) return false;

        object source;
        try
        {
            source = _decodedSources.GetOrAdd(resolvedPath, path => _audioApi.DecodeFromFile(path));
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, "[QuakeSounds] Failed to decode sound file: {Path}", resolvedPath);
            return false;
        }

        var channelId = $"quakesounds.{System.Threading.Interlocked.Increment(ref _channelCounter)}";
        dynamic channel = _audioApi.UseChannel(channelId);

        try
        {
            var channelType = ((object)channel).GetType();
            
            // Reflexiós Metódus gyorsítótárazása (Cache) -> megszünteti a mikro-akadásokat
            var setSource = _setSourceMethodCache.GetOrAdd(channelType, t => t.GetMethod("SetSource"));

            if (setSource == null)
            {
                _core.Logger.LogWarning("[QuakeSounds] Audio channel does not have SetSource method.");
                return false;
            }

            setSource.Invoke(channel, new[] { source });
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, "[QuakeSounds] Failed to SetSource on audio channel.");
            return false;
        }

        if (config.PlayToAll)
        {
            var anyPlayed = false;
            foreach (var player in _core.PlayerManager.GetAllPlayers())
            {
                if (player?.IsValid != true || player.IsFakeClient || player.PlayerID <= 0)
                    continue;

                if (!isPlayerEnabled(player.SteamID))
                    continue;

                var volume = config.Volume;
                var overrideVolume = getPlayerVolume(player.SteamID);
                if (overrideVolume >= 0) volume = overrideVolume;
                volume = Math.Clamp(volume, 0f, 1f);

                try
                {
                    channel.SetVolume(player.PlayerID, volume);
                    channel.Play(player.PlayerID);
                    anyPlayed = true;
                }
                catch (Exception ex)
                {
                    _core.Logger.LogError(ex, "[QuakeSounds] Audio failed for player. PlayerID={PlayerID}", player.PlayerID);
                }
            }
            return anyPlayed;
        }

        if (!isPlayerEnabled(attacker.SteamID)) return false;

        var attackerVolume = config.Volume;
        var overrideAttackerVol = getPlayerVolume(attacker.SteamID);
        if (overrideAttackerVol >= 0) attackerVolume = overrideAttackerVol;
        attackerVolume = Math.Clamp(attackerVolume, 0f, 1f);

        try
        {
            var attackerPlayerId = ResolvePlayerId(attacker);
            if (attackerPlayerId <= 0) return false;

            channel.SetVolume(attackerPlayerId, attackerVolume);
            channel.Play(attackerPlayerId);
            return true;
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, "[QuakeSounds] Audio failed for attacker. PlayerID={PlayerID}", attacker.PlayerID);
            return false;
        }
    }

    private string ResolvePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath)) return configuredPath;

        var dataPath = Path.Combine(_core.PluginDataDirectory, configuredPath);
        if (File.Exists(dataPath)) return dataPath;

        return Path.Combine(_core.PluginPath, configuredPath);
    }

    private int ResolvePlayerId(IPlayer player)
    {
        if (player is { IsValid: true } && player.PlayerID > 0)
        {
            return player.PlayerID;
        }

        if (player.SteamID == 0) return 0;

        foreach (var p in _core.PlayerManager.GetAllPlayers())
        {
            if (p is { IsValid: true } && !p.IsFakeClient && p.SteamID == player.SteamID && p.PlayerID > 0)
            {
                return p.PlayerID;
            }
        }

        return 0;
    }
}
