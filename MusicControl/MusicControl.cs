using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace MusicControl;

public class MusicControl : BasePlugin
{
    public override string ModuleName        => "Music Control";
    public override string ModuleVersion     => "3.3.0";
    public override string ModuleAuthor      => "Torment";
    public override string ModuleDescription => "Per-player map music volume control";

    private readonly Dictionary<ulong, float> _playerVolume = [];

    private string DataFilePath =>
        Path.Combine(ModuleDirectory, "music_prefs.json");

    private static readonly byte[] VolumeParamHeader =
        [0xE9, 0x54, 0x60, 0xBD, 0x08, 0x04, 0x00];

    // Cache guid + source index to be able to replay the SOS to late players
    private readonly record struct SoundEvent(uint Guid, int SourceIndex);
    private readonly List<SoundEvent> _activeEvents = [];

    public override void Load(bool hotReload)
    {
        LoadFromJson();

        AddCommand("css_music", "Toggle or set music volume (usage: !music [0-100])", OnMusicCommand);

        HookUserMessage(208, OnSosStartSoundEvent);

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    public override void Unload(bool hotReload)
    {
        SaveToJson();
    }

    // ── Command ───────────────────────────────────────────────────────────────

    private void OnMusicCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        float current = _playerVolume.GetValueOrDefault(player.SteamID, 1.0f);

        float target;
        if (info.ArgCount >= 2 && int.TryParse(info.ArgByIndex(1), out int pct))
        {
            pct = Math.Clamp(pct, 0, 100);
            target = pct / 100f;
        }
        else
        {
            target = current > 0f ? 0f : 1.0f;
        }

        _playerVolume[player.SteamID] = target;

        if (target > 0f)
        {
            // Re-send SOS so the client starts the sound if it missed it,
            // then immediately set the desired volume
            ReplaySoundEvents(player);
            foreach (var evt in _activeEvents)
                SendVolume(player, evt.Guid, target);
        }
        else
        {
            foreach (var evt in _activeEvents)
                SendVolume(player, evt.Guid, 0f);
        }

        PrintVolumeMessage(player, target);
    }

    // Replay cached SOS messages to a specific player so they hear the sound
    // even if they missed the original event (late join / plugin reload)
    private void ReplaySoundEvents(CCSPlayerController player)
    {
        foreach (var evt in _activeEvents)
        {
            try
            {
                var sos = UserMessage.FromId(208);
                sos.SetUInt("soundevent_guid", evt.Guid);
                sos.SetInt("source_entity_index", evt.SourceIndex);

                var filter = new RecipientFilter();
                filter.Add(player);
                sos.Recipients = filter;
                sos.Send();
            }
            catch { }
        }
    }

    private static void PrintVolumeMessage(CCSPlayerController player, float volume)
    {
        int pct = (int)(volume * 100);
        if (pct == 0)
            player.PrintToChat(" \x01[Music] \x02Música desactivada ✘");
        else if (pct == 100)
            player.PrintToChat(" \x01[Music] \x04Música activada (100%) ✔");
        else
            player.PrintToChat($" \x01[Music] \x05Volumen: {pct}%");
    }

    // ── SOS hook ──────────────────────────────────────────────────────────────

    private HookResult OnSosStartSoundEvent(UserMessage msg)
    {
        int sourceIndex = msg.ReadInt("source_entity_index");
        var entity = Utilities.GetEntityFromIndex<CBaseEntity>(sourceIndex);

        if (!IsBackgroundSoundEntity(entity, sourceIndex))
            return HookResult.Continue;

        uint guid = msg.ReadUInt("soundevent_guid");

        if (!_activeEvents.Any(e => e.Guid == guid))
            _activeEvents.Add(new SoundEvent(guid, sourceIndex));

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid) continue;
            float vol = _playerVolume.GetValueOrDefault(player.SteamID, 1.0f);
            if (vol >= 1.0f) continue;

            SendVolume(player, guid, vol);
        }

        return HookResult.Continue;
    }

    private static bool IsBackgroundSoundEntity(CBaseEntity? entity, int sourceIndex)
    {
        if (entity == null) return false;
        if (sourceIndex == 0) return true;

        return entity.DesignerName is
            "ambient_generic" or "point_soundevent" or "snd_event_point";
    }

    // ── Volume message ────────────────────────────────────────────────────────

    private void SendVolume(CCSPlayerController player, uint guid, float volume)
    {
        try
        {
            var vmsg = UserMessage.FromId(210);
            vmsg.SetUInt("soundevent_guid", guid);
            vmsg.SetBytes("packed_params", BuildVolumeParams(volume));

            var filter = new RecipientFilter();
            filter.Add(player);
            vmsg.Recipients = filter;
            vmsg.Send();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicControl] SendVolume error: {ex.Message}");
        }
    }

    private static byte[] BuildVolumeParams(float volume)
    {
        byte[] vol = BitConverter.GetBytes(volume);
        byte[] result = new byte[VolumeParamHeader.Length + vol.Length];
        VolumeParamHeader.CopyTo(result, 0);
        vol.CopyTo(result, VolumeParamHeader.Length);
        return result;
    }

    // ── Persistence (JSON) ───────────────────────────────────────────────────

    private void LoadFromJson()
    {
        try
        {
            if (!File.Exists(DataFilePath)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, float>>(
                File.ReadAllText(DataFilePath));
            if (raw == null) return;
            foreach (var kv in raw)
                if (ulong.TryParse(kv.Key, out ulong id))
                    _playerVolume[id] = Math.Clamp(kv.Value, 0f, 1f);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicControl] LoadFromJson error: {ex.Message}");
        }
    }

    private void SaveToJson()
    {
        try
        {
            var serializable = _playerVolume.ToDictionary(
                kv => kv.Key.ToString(), kv => kv.Value);
            File.WriteAllText(DataFilePath,
                JsonSerializer.Serialize(serializable,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicControl] SaveToJson error: {ex.Message}");
        }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo _)
    {
        _activeEvents.Clear();
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo _)
    {
        SaveToJson();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo _)
    {
        if (@event.Userid is { IsValid: true } p)
        {
            SaveToJson();
            _playerVolume.Remove(p.SteamID);
        }
        return HookResult.Continue;
    }
}
