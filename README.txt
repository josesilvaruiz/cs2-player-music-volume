================================================================================
                         MUSIC CONTROL  —  v3.3.0
                    Per-Player Map Music Volume Control
                         Plugin for Counter-Strike 2
================================================================================

  Author  : Torment
  Version : 3.3.0
  Game    : Counter-Strike 2 (tested on CS2 build 14001 / June 2026)
  API     : CounterStrikeSharp (latest stable recommended)

--------------------------------------------------------------------------------
DESCRIPTION
--------------------------------------------------------------------------------

Music Control is a lightweight CounterStrikeSharp plugin that lets every player
on the server independently control the volume of map background music — without
affecting anyone else. Preferences are persisted across rounds and reconnects
via a local JSON file; no database is required.

--------------------------------------------------------------------------------
REQUIREMENTS
--------------------------------------------------------------------------------

  • Counter-Strike 2 dedicated server
  • MetaMod:Source (dev branch)  —  https://www.metamodsource.net/downloads.php?branch=dev
  • CounterStrikeSharp (latest)  —  https://github.com/roflmuffin/CounterStrikeSharp

--------------------------------------------------------------------------------
INSTALLATION
--------------------------------------------------------------------------------

  1. Build the project in Release mode (Visual Studio / dotnet CLI):

       dotnet publish -c Release

  2. Copy the compiled MusicControl.dll (and its deps file) into:

       <server>/game/csgo/addons/counterstrikesharp/plugins/MusicControl/

  3. (Re)start the server or run:

       css_plugins reload MusicControl

  No extra configuration files are needed. The plugin creates
  music_prefs.json automatically on first use inside its plugin directory.

--------------------------------------------------------------------------------
USAGE  —  PLAYER COMMANDS
--------------------------------------------------------------------------------

  Command      : !music  (also available as css_music in console)
  Available to : All connected players

  ┌─────────────────────────────────────────────────────────────────────────┐
  │  !music            Toggle music ON (100%) / OFF (0%)                   │
  │  !music 0          Mute map music completely                            │
  │  !music 50         Set map music to 50% volume                         │
  │  !music 100        Restore map music to full volume                     │
  │  !music <0-100>    Set any percentage between 0 and 100                │
  └─────────────────────────────────────────────────────────────────────────┘

  The change takes effect immediately. The setting is saved automatically and
  restored the next time the player connects to the server.

  EXAMPLES
  --------

  Player joins a map with loud background music and wants to mute it:
    > !music 0
    [Music] Música desactivada ✘

  Player wants half volume while still hearing the atmosphere:
    > !music 50
    [Music] Volumen: 50%

  Player toggles music back on to full:
    > !music
    [Music] Música activada (100%) ✔

  Player sets a precise level from console:
    > css_music 75
    [Music] Volumen: 75%

--------------------------------------------------------------------------------
HOW IT WORKS  (TECHNICAL OVERVIEW)
--------------------------------------------------------------------------------

  The plugin hooks UserMessage 208 (SosStartSoundEvent) to intercept every
  background sound that starts on the map. Eligible entities are:

    • ambient_generic
    • point_soundevent
    • snd_event_point
    • world entity (source index 0)

  When a player's stored volume is below 100%, the plugin immediately sends a
  UserMessage 210 (SosSetSoundEventParams) back to that specific player with a
  packed_params payload containing the desired float volume value. This adjusts
  the volume client-side without touching any game convars or other players.

  Late joiners and hot-reloads are handled via SOS replay: the plugin caches
  active sound events and re-sends them to any player who adjusts their volume
  after the sound started.

--------------------------------------------------------------------------------
PERSISTENCE
--------------------------------------------------------------------------------

  Player preferences are stored in:

    <plugin_directory>/music_prefs.json

  Format example:

    {
      "76561198012345678": 0.5,
      "76561197998765432": 0.0
    }

  The file is written on every round end and on player disconnect. Values are
  clamped to the [0.0, 1.0] range on load; corrupted files are silently skipped
  and a fresh file is created on the next save.

--------------------------------------------------------------------------------
COMPATIBILITY NOTES
--------------------------------------------------------------------------------

  • Tested on CS2 build 14001 (June 2026 patch cycle).
  • Only affects map/background music entities. Round-start stingers, weapon
    sounds, and UI audio are untouched.
  • Compatible alongside SoundControlPlugin (2.0.x) — both plugins hook the
    same user messages but operate on independent recipient filters.

--------------------------------------------------------------------------------
UNINSTALLATION
--------------------------------------------------------------------------------

  1. Remove the MusicControl folder from the plugins directory.
  2. Optionally delete music_prefs.json if stored preferences are no longer
     needed.

--------------------------------------------------------------------------------
LICENSE
--------------------------------------------------------------------------------

  Distributed under the MIT License. See source repository for full text.

================================================================================
