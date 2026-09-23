using System.Globalization;

namespace SimHub.Plugin.Voicemeeter
{
    public enum ChannelKind
    {
        Strip,
        Bus
    }

    public enum PresetFunction
    {
        GainSet,
        GainStep,
        MuteOn,
        MuteOff,
        MuteToggle
    }

    /// <summary>
    /// A single user-defined preset: one channel, one function, one value. Each preset registers
    /// exactly one SimHub action, so the number of Controls-and-Events targets this plugin exposes
    /// is however many presets the user actually created, not every possible channel/function/value
    /// combination.
    /// </summary>
    public class ChannelPreset
    {
        /// <summary>
        /// Internal identity assigned once at creation and never reused. Not used in the action name
        /// (SimHub's Controls-and-Events target picker shows the action name verbatim, so it needs to
        /// stay human-readable - see <see cref="ActionName"/>); kept for any future feature that needs
        /// to refer to a specific preset independent of its current display text.
        /// </summary>
        public long Id { get; set; }

        public ChannelKind Channel { get; set; }

        public int ChannelIndex { get; set; }

        public PresetFunction Function { get; set; }

        /// <summary>
        /// Meaning depends on Function: absolute target dB for GainSet, signed dB delta for GainStep,
        /// unused for the Mute* functions.
        /// </summary>
        public float Value { get; set; }

        /// <summary>
        /// Optional user override; when null/empty the settings screen shows <see cref="DisplayName"/> instead.
        /// </summary>
        public string CustomName { get; set; }

        /// <summary>
        /// SimHub's Controls-and-Events target picker displays this string verbatim, so it's the same
        /// as <see cref="DisplayName"/> rather than an opaque id - there's currently no way to edit an
        /// existing preset's channel/function/value (only add/remove), so this can't change out from
        /// under an existing physical-control binding for as long as that preset exists. If in-place
        /// editing is ever added, this needs revisiting so an edit doesn't silently orphan a binding.
        /// </summary>
        public string ActionName => DisplayName;

        public string ChannelLabel => $"{Channel}{ChannelIndex}";

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CustomName))
                {
                    return CustomName;
                }

                switch (Function)
                {
                    case PresetFunction.GainSet:
                        return $"{ChannelLabel} Gain {Value.ToString("0.0", CultureInfo.InvariantCulture)}dB";
                    case PresetFunction.GainStep:
                        string sign = Value >= 0 ? "+" : string.Empty;
                        return $"{ChannelLabel} Gain Step {sign}{Value.ToString("0.0", CultureInfo.InvariantCulture)}dB";
                    case PresetFunction.MuteOn:
                        return $"{ChannelLabel} Mute On";
                    case PresetFunction.MuteOff:
                        return $"{ChannelLabel} Mute Off";
                    case PresetFunction.MuteToggle:
                        return $"{ChannelLabel} Toggle Mute";
                    default:
                        return ChannelLabel;
                }
            }
        }

        /// <summary>
        /// Whether <see cref="Value"/> is meaningful for this preset's function - the settings screen
        /// disables the value input for the Mute functions, whose outcome is already fully implied by
        /// the function itself.
        /// </summary>
        public bool UsesValue => Function == PresetFunction.GainSet || Function == PresetFunction.GainStep;
    }
}
