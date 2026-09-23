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
        /// plugin's lifetime, so we register the maximum channel set a Potato edition can have.
        /// Editions with fewer channels simply leave the upper indexes at their default (0 dB /
        /// unmuted) with no effect. Actions are no longer tied to this maximum - see the user-defined
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

            for (int i = 0; i < MaxStrips; i++)
            {
                int index = i;
                this.AttachDelegate($"Strip{index}.Gain", () => Remote.IsConnected ? (double)Remote.GetStripGain(index) : 0d);
                this.AttachDelegate($"Strip{index}.Mute", () => Remote.IsConnected && Remote.GetStripMute(index));
                this.AttachDelegate($"Strip{index}.Label", () => Remote.IsConnected ? Remote.GetStripLabel(index) : string.Empty);
            }

            for (int i = 0; i < MaxBuses; i++)
            {
                int index = i;
                this.AttachDelegate($"Bus{index}.Gain", () => Remote.IsConnected ? (double)Remote.GetBusGain(index) : 0d);
                this.AttachDelegate($"Bus{index}.Mute", () => Remote.IsConnected && Remote.GetBusMute(index));
                this.AttachDelegate($"Bus{index}.Label", () => Remote.IsConnected ? Remote.GetBusLabel(index) : string.Empty);
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

        /// <summary>
        /// Applies a single preset's function to its channel. Called both by the registered SimHub
        /// action and by the settings screen's "Test fire" button, so both paths behave identically.
        /// </summary>
        internal void ApplyPreset(ChannelPreset preset)
        {
            switch (preset.Function)
            {
                case PresetFunction.GainSet:
                    if (preset.Channel == ChannelKind.Strip)
                    {
                        Remote.SetStripGain(preset.ChannelIndex, preset.Value);
                    }
                    else
                    {
                        Remote.SetBusGain(preset.ChannelIndex, preset.Value);
                    }

                    break;

                case PresetFunction.GainStep:
                    if (preset.Channel == ChannelKind.Strip)
                    {
                        Remote.AdjustStripGain(preset.ChannelIndex, preset.Value);
                    }
                    else
                    {
                        Remote.AdjustBusGain(preset.ChannelIndex, preset.Value);
                    }

                    break;

                case PresetFunction.MuteOn:
                    if (preset.Channel == ChannelKind.Strip)
                    {
                        Remote.SetStripMute(preset.ChannelIndex, true);
                    }
                    else
                    {
                        Remote.SetBusMute(preset.ChannelIndex, true);
                    }

                    break;

                case PresetFunction.MuteOff:
                    if (preset.Channel == ChannelKind.Strip)
                    {
                        Remote.SetStripMute(preset.ChannelIndex, false);
                    }
                    else
                    {
                        Remote.SetBusMute(preset.ChannelIndex, false);
                    }

                    break;

                case PresetFunction.MuteToggle:
                    if (preset.Channel == ChannelKind.Strip)
                    {
                        Remote.ToggleStripMute(preset.ChannelIndex);
                    }
                    else
                    {
                        Remote.ToggleBusMute(preset.ChannelIndex);
                    }

                    break;
            }
        }

        /// <summary>
        /// Adds a new preset and persists it immediately, so it survives even if the user restarts
        /// SimHub without pressing "Save settings" first - restarting is already required before the
        /// preset's action shows up in Controls and Events, so losing it to a forgotten save on top of
        /// that would be a bad surprise. Returns null if an identical preset (same channel, function
        /// and value) already exists - since the action name is now the human-readable display text
        /// rather than an opaque id, two identical presets would otherwise collide on the same name.
        /// </summary>
        internal ChannelPreset AddPreset(ChannelKind channel, int channelIndex, PresetFunction function, float value)
        {
            bool isDuplicate = Settings.Presets.Exists(p =>
                p.Channel == channel && p.ChannelIndex == channelIndex && p.Function == function && p.Value == value);
            if (isDuplicate)
            {
                return null;
            }

            var preset = new ChannelPreset
            {
                Id = Settings.NextPresetId++,
                Channel = channel,
                ChannelIndex = channelIndex,
                Function = function,
                Value = value
            };

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
