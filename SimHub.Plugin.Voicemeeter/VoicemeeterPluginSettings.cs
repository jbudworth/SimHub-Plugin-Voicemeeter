using System.Collections.Generic;

namespace SimHub.Plugin.Voicemeeter
{
    /// <summary>
    /// Settings class, must be JSON.net serializable.
    /// </summary>
    public class VoicemeeterPluginSettings
    {
        public List<ChannelPreset> Presets = new List<ChannelPreset>();

        /// <summary>
        /// Monotonically increasing counter used to assign each new preset a stable <see cref="ChannelPreset.Id"/>.
        /// Never reused, even after presets are removed.
        /// </summary>
        public long NextPresetId = 1;
    }
}
