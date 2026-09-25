using System.ComponentModel;

namespace SimHub.Plugin.Voicemeeter
{
    /// <summary>
    /// Live view of a single strip or bus channel, used to bind the settings-screen DataGrids
    /// directly against the Voicemeeter Remote API.
    /// </summary>
    public class ChannelRowViewModel : INotifyPropertyChanged
    {
        private readonly VoicemeeterRemote _remote;
        private readonly ChannelKind _kind;
        private readonly int _index;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChannelRowViewModel(VoicemeeterRemote remote, ChannelKind kind, int index)
        {
            _remote = remote;
            _kind = kind;
            _index = index;
        }

        public string Label => _remote.GetLabel(_kind, _index);

        public double Gain
        {
            get => _remote.GetGain(_kind, _index);
            set => _remote.SetGain(_kind, _index, (float)value);
        }

        public bool IsMuted
        {
            get => _remote.GetMute(_kind, _index);
            set => _remote.SetMute(_kind, _index, value);
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
