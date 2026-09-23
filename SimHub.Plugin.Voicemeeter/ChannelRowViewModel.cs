using System;
using System.ComponentModel;

namespace SimHub.Plugin.Voicemeeter
{
    /// <summary>
    /// Live view of a single strip or bus channel, used to bind the settings-screen DataGrids
    /// directly against the Voicemeeter Remote API without duplicating strip/bus logic.
    /// </summary>
    public class ChannelRowViewModel : INotifyPropertyChanged
    {
        private readonly Func<float> _getGain;
        private readonly Action<float> _setGain;
        private readonly Func<bool> _getMute;
        private readonly Action<bool> _setMute;
        private readonly Func<string> _getLabel;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChannelRowViewModel(
            int index,
            Func<float> getGain,
            Action<float> setGain,
            Func<bool> getMute,
            Action<bool> setMute,
            Func<string> getLabel)
        {
            Index = index;
            _getGain = getGain;
            _setGain = setGain;
            _getMute = getMute;
            _setMute = setMute;
            _getLabel = getLabel;
        }

        public int Index { get; }

        public string Label => _getLabel();

        public double Gain
        {
            get => _getGain();
            set => _setGain((float)value);
        }

        public bool IsMuted
        {
            get => _getMute();
            set => _setMute(value);
        }

        /// <summary>
        /// Called on a timer by the settings control to keep bound controls in sync with
        /// external changes (e.g. the user moving a fader inside Voicemeeter itself).
        /// </summary>
        public void RaiseRefresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gain)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
        }
    }
}
