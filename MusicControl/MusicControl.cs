using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace MusicControl;

public class MusicControl : BasePlugin
{
    public override string ModuleName => "Music Control";
    public override string ModuleVersion => "3.5.0";
    public override string ModuleAuthor => "Torment";
    public override string ModuleDescription => "Per-player map music volume control + radio";

    private readonly Dictionary<ulong, float> _playerVolume = [];

    private string DataFilePath =>
        Path.Combine(ModuleDirectory, "music_prefs.json");

    private static readonly byte[] VolumeParamHeader =
        [0xE9, 0x54, 0x60, 0xBD, 0x08, 0x04, 0x00];

    private readonly record struct SoundEvent(uint Guid, int SourceIndex);
    private readonly List<SoundEvent> _activeEvents = [];

    // ── Radio ─────────────────────────────────────────────────────────────────

    private record RadioTrack(string Path, int DurationSeconds);

    private class RadioConfig
    {
        public float DuckVolume { get; set; } = 0.2f;
        public List<RadioTrackEntry> Tracks { get; set; } = [];
    }

    private class RadioTrackEntry
    {
        public string Path { get; set; } = "";
        public int Duration { get; set; } = 180;
    }

    private List<RadioTrack> _radioTracks = [];
    private int _currentTrackIndex = -1;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _trackTimer;
    private float _duckVolume = 0.2f;
    private readonly Dictionary<ulong, bool> _radioEnabled = [];

    private string RadioConfigPath =>
        Path.Combine(ModuleDirectory, "radio.json");

    // ── Load / Unload ─────────────────────────────────────────────────────────

    public override void Load(bool hotReload)
    {
        LoadFromJson();
        LoadRadioConfig();

        AddCommand("css_music", "Toggle or set music volume (usage: !music [0-100])", OnMusicCommand);
        AddCommand("css_radio", "Toggle radio on/off (usage: !radio [on|off])", OnRadioCommand);

        HookUserMessage(208, OnSosStartSoundEvent);

        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
    }

    public override void Unload(bool hotReload)
    {
        SaveToJson();
        _trackTimer?.Kill();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Effective map-music volume for a player, respecting radio duck.
    private float GetEffectiveMusicVolume(ulong steamId)
    {
        float vol = _playerVolume.GetValueOrDefault(steamId, 1.0f);
        if (_radioEnabled.GetValueOrDefault(steamId, false))
            return Math.Min(vol, _duckVolume);
        return vol;
    }

    private bool AnyRadioListener() =>
        _radioEnabled.Values.Any(v => v);

    // ── !music command ────────────────────────────────────────────────────────

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

        float effective = GetEffectiveMusicVolume(player.SteamID);

        if (effective > 0f)
        {
            ReplaySoundEvents(player);
            foreach (var evt in _activeEvents)
                SendVolume(player, evt.Guid, effective);
        }
        else
        {
            foreach (var evt in _activeEvents)
                SendVolume(player, evt.Guid, 0f);
        }

        PrintVolumeMessage(player, target);
    }

    // ── !radio command ────────────────────────────────────────────────────────

    private void OnRadioCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        if (_radioTracks.Count == 0)
        {
            player.PrintToChat(" \x01[Radio] \x02No hay pistas configuradas en radio.json.");
            return;
        }

        bool current = _radioEnabled.GetValueOrDefault(player.SteamID, false);
        bool enable = !current;

        if (info.ArgCount >= 2)
        {
            string arg = info.ArgByIndex(1);
            enable = arg.Equals("on", StringComparison.OrdinalIgnoreCase) || arg == "1";
        }

        _radioEnabled[player.SteamID] = enable;

        if (enable)
        {
            // Duck map music
            float duck = GetEffectiveMusicVolume(player.SteamID);
            foreach (var evt in _activeEvents)
                SendVolume(player, evt.Guid, duck);

            // Start or join the current track
            if (_currentTrackIndex < 0)
                PlayTrack(0, startTimer: true);
            else
                player.ExecuteClientCommand($"play \"{_radioTracks[_currentTrackIndex].Path}\"");

            int displayIdx = Math.Max(0, _currentTrackIndex);
            player.PrintToChat($" \x01[Radio] \x04Radio ON \x01— \x05{_radioTracks[displayIdx].Path}");
        }
        else
        {
            // Restore map music to player's preferred volume
            float vol = _playerVolume.GetValueOrDefault(player.SteamID, 1.0f);
            foreach (var evt in _activeEvents)
                SendVolume(player, evt.Guid, vol);

            player.PrintToChat(" \x01[Radio] \x02Radio OFF.");

            // Stop the server-side timer if no one is listening anymore
            if (!AnyRadioListener())
            {
                _trackTimer?.Kill();
                _trackTimer = null;
                _currentTrackIndex = -1;
            }
        }
    }

    // ── Radio playback ────────────────────────────────────────────────────────

    private void PlayTrack(int index, bool startTimer)
    {
        _currentTrackIndex = index % _radioTracks.Count;
        var track = _radioTracks[_currentTrackIndex];

        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid) continue;
            if (!_radioEnabled.GetValueOrDefault(p.SteamID, false)) continue;
            p.ExecuteClientCommand($"play \"{track.Path}\"");
        }

        if (startTimer)
        {
            _trackTimer?.Kill();
            _trackTimer = AddTimer(track.DurationSeconds, OnTrackEnd);
        }
    }

    private void OnTrackEnd()
    {
        if (!AnyRadioListener())
        {
            _currentTrackIndex = -1;
            _trackTimer = null;
            return;
        }
        PlayTrack((_currentTrackIndex + 1) % _radioTracks.Count, startTimer: true);
    }

    // ── Radio config ──────────────────────────────────────────────────────────

    private void LoadRadioConfig()
    {
        try
        {
            if (!File.Exists(RadioConfigPath))
            {
                var def = new RadioConfig
                {
                    DuckVolume = 0.2f,
                    Tracks = [new RadioTrackEntry { Path = "radio/example.mp3", Duration = 180 }]
                };
                File.WriteAllText(RadioConfigPath,
                    JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("[MusicControl] radio.json creado — edítalo para añadir tus pistas.");
                return;
            }

            var cfg = JsonSerializer.Deserialize<RadioConfig>(File.ReadAllText(RadioConfigPath));
            if (cfg == null) return;

            _duckVolume = Math.Clamp(cfg.DuckVolume, 0f, 1f);
            _radioTracks = cfg.Tracks
                .Where(t => !string.IsNullOrWhiteSpace(t.Path) && t.Duration > 0)
                .Select(t => new RadioTrack(t.Path, t.Duration))
                .ToList();

            Console.WriteLine($"[MusicControl] Radio: {_radioTracks.Count} pistas cargadas, duck={_duckVolume:P0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MusicControl] LoadRadioConfig error: {ex.Message}");
        }
    }

    // ── SOS hook ──────────────────────────────────────────────────────────────

    private void ReplaySoundEvents(CCSPlayerController player)
    {
        foreach (var evt in _activeEvents)
            ReplaySingleEvent(player, evt);
    }

    private static void ReplaySingleEvent(CCSPlayerController player, SoundEvent evt)
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
            float vol = GetEffectiveMusicVolume(player.SteamID);
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

    private static void PrintVolumeMessage(CCSPlayerController player, float volume)
    {
        int pct = (int)(volume * 100);
        if (pct == 0)
            player.PrintToChat(" \x01[Music v3.5.0] \x02Música desactivada ✘");
        else if (pct == 100)
            player.PrintToChat(" \x01[Music v3.5.0] \x04Música activada (100%) ✔");
        else
            player.PrintToChat($" \x01[Music v3.5.0] \x05Volumen: {pct}%");
    }

    // ── Persistence (JSON) ───────────────────────────────────────────────────

    private void LoadFromJson()
    {
        try
        {
            Directory.CreateDirectory(ModuleDirectory);
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
            _radioEnabled.Remove(p.SteamID);

            if (!AnyRadioListener())
            {
                _trackTimer?.Kill();
                _trackTimer = null;
                _currentTrackIndex = -1;
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo _)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;
        if (_activeEvents.Count == 0) return HookResult.Continue;

        float vol = GetEffectiveMusicVolume(player.SteamID);
        if (vol <= 0f) return HookResult.Continue;

        AddTimer(2.0f, () =>
        {
            if (!player.IsValid) return;

            ReplaySoundEvents(player);

            if (vol < 1.0f)
            {
                foreach (var evt in _activeEvents)
                    SendVolume(player, evt.Guid, vol);
            }

            // Resume radio for player if they had it active
            if (_radioEnabled.GetValueOrDefault(player.SteamID, false) && _currentTrackIndex >= 0)
                player.ExecuteClientCommand($"play \"{_radioTracks[_currentTrackIndex].Path}\"");
        });

        return HookResult.Continue;
    }
}
