using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Windows.Media;

namespace SimHub.Plugin.Voicemeeter
{
    [PluginDescription("Control Voicemeeter Basic/Banana/Potato strip and bus gain/mute from SimHub")]
    [PluginAuthor("Claude.ai")]
    [PluginName("Voicemeeter Control")]
    public class VoicemeeterPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        /// <summary>
        /// Display-only property count (Gain/Mute/Label per channel, for dashboards) is fixed for the
        /// plugin's lifetime, so we register the maximum channel set a Potato edition can have. Each
        /// delegate guards on the *connected* edition's real channel count before touching the native
        /// API, so on smaller editions the upper indexes just report defaults instead of issuing
        /// unknown-parameter requests. Actions are not tied to this maximum - see the user-defined
        /// preset list in Settings, which registers exactly one action per preset the user created.
        /// </summary>
        public const int MaxStrips = 8;
        public const int MaxBuses = 8;

        public VoicemeeterPluginSettings Settings;

        internal readonly VoicemeeterRemote Remote = new VoicemeeterRemote();

        private int _lastConnectAttemptTicks;
        private const int ReconnectIntervalMs = 2000;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => MixerIcon.Value;

        public string LeftMenuTitle => "Voicemeeter";

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[Voicemeeter] Starting plugin");

            Settings = this.ReadCommonSettings<VoicemeeterPluginSettings>("GeneralSettings", () => new VoicemeeterPluginSettings());

            this.AttachDelegate("Connected", () => Remote.IsConnected);
            this.AttachDelegate("Type", () => Remote.Type.ToString());

            foreach (ChannelKind kind in new[] { ChannelKind.Strip, ChannelKind.Bus })
            {
                int max = kind == ChannelKind.Strip ? MaxStrips : MaxBuses;
                for (int i = 0; i < max; i++)
                {
                    int index = i;
                    this.AttachDelegate($"{kind}{index}.Gain", () => IsChannelAvailable(kind, index) ? (double)Remote.GetGain(kind, index) : 0d);
                    this.AttachDelegate($"{kind}{index}.Mute", () => IsChannelAvailable(kind, index) && Remote.GetMute(kind, index));
                    this.AttachDelegate($"{kind}{index}.Label", () => IsChannelAvailable(kind, index) ? Remote.GetLabel(kind, index) : string.Empty);
                }
            }

            // One action per user-defined preset - the count here is exactly what the user configured,
            // not every possible channel/function/value combination. Presets added or removed via the
            // settings screen only take effect here on the next plugin/SimHub restart.
            foreach (ChannelPreset preset in Settings.Presets)
            {
                ChannelPreset capturedPreset = preset;
                this.AddAction(capturedPreset.ActionName, (a, b) => ApplyPreset(capturedPreset));
            }

            TryReconnect();
        }

        private bool IsChannelAvailable(ChannelKind kind, int index)
        {
            return Remote.IsConnected && index < Remote.GetChannelCount(kind);
        }

        /// <summary>
        /// Applies a single preset's function to its channel. Called both by the registered SimHub
        /// action and by the settings screen's "Test fire" button, so both paths behave identically.
        /// </summary>
        internal void ApplyPreset(ChannelPreset preset)
        {
            if (preset.ChannelIndex >= Remote.GetChannelCount(preset.Channel))
            {
                // Also covers the disconnected state (counts are 0). A preset can legitimately target
                // a channel the current edition lacks if it was created while a bigger edition (e.g.
                // Potato) was running - ignore it rather than sending a request for a channel that
                // doesn't exist, which Voicemeeter rejects.
                SimHub.Logging.Current.Warn($"[Voicemeeter] Preset '{preset.DisplayName}' targets {preset.ChannelLabel}, which is not available on the connected Voicemeeter edition - ignored.");
                return;
            }

            switch (preset.Function)
            {
                case PresetFunction.GainSet:
                    Remote.SetGain(preset.Channel, preset.ChannelIndex, preset.Value);
                    break;
                case PresetFunction.GainStep:
                    Remote.AdjustGain(preset.Channel, preset.ChannelIndex, preset.Value);
                    break;
                case PresetFunction.MuteOn:
                    Remote.SetMute(preset.Channel, preset.ChannelIndex, true);
                    break;
                case PresetFunction.MuteOff:
                    Remote.SetMute(preset.Channel, preset.ChannelIndex, false);
                    break;
                case PresetFunction.MuteToggle:
                    Remote.ToggleMute(preset.Channel, preset.ChannelIndex);
                    break;
            }
        }

        /// <summary>
        /// Adds a new preset and persists it immediately, so it survives even if the user restarts
        /// SimHub without an explicit save - restarting is already required before the preset's action
        /// shows up in Controls and Events, so losing it on top of that would be a bad surprise.
        /// Returns null if a preset with the same action name already exists: SimHub actions are keyed
        /// by name, and the name rounds gain to one decimal, so e.g. values 5.01 and 5.04 are distinct
        /// floats that would both register as "Strip0 Gain 5.0dB" - deduping on the generated name
        /// (rather than raw component equality) is what actually prevents the collision.
        /// </summary>
        internal ChannelPreset AddPreset(ChannelKind channel, int channelIndex, PresetFunction function, float value)
        {
            var preset = new ChannelPreset
            {
                Channel = channel,
                ChannelIndex = channelIndex,
                Function = function,
                Value = value
            };

            if (Settings.Presets.Exists(p => p.ActionName == preset.ActionName))
            {
                return null;
            }

            preset.Id = Settings.NextPresetId++;
            Settings.Presets.Add(preset);
            this.SaveCommonSettings("GeneralSettings", Settings);
            return preset;
        }

        internal void RemovePreset(ChannelPreset preset)
        {
            Settings.Presets.Remove(preset);
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!Remote.IsConnected)
            {
                int now = Environment.TickCount;
                if (unchecked(now - _lastConnectAttemptTicks) >= ReconnectIntervalMs)
                {
                    TryReconnect();
                }

                return;
            }

            // Voicemeeter's own docs call this a required side effect, not just an optimization:
            // without polling it regularly, VBVMR_GetParameterFloat can return stale/undefined
            // values. This also detects a Voicemeeter shutdown so we fall back to reconnect-retry.
            Remote.ParametersDirty();
        }

        private void TryReconnect()
        {
            _lastConnectAttemptTicks = Environment.TickCount;
            if (Remote.TryConnect())
            {
                SimHub.Logging.Current.Info($"[Voicemeeter] Connected, edition: {Remote.Type}, strips: {Remote.StripCount}, buses: {Remote.BusCount}");
            }
        }

        public void End(PluginManager pluginManager)
        {
            this.SaveCommonSettings("GeneralSettings", Settings);
            Remote.Dispose();
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }
}
