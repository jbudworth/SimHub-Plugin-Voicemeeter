using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SimHub.Plugin.Voicemeeter
{
    public partial class SettingsControl : UserControl
    {
        private class ChannelOption
        {
            public string Label { get; set; }
            public ChannelKind Kind { get; set; }
            public int Index { get; set; }
        }

        private class FunctionOption
        {
            public string Label { get; set; }
            public PresetFunction Function { get; set; }
            public bool UsesValue { get; set; }
        }

        private readonly VoicemeeterPlugin _plugin;
        private readonly ObservableCollection<ChannelRowViewModel> _strips = new ObservableCollection<ChannelRowViewModel>();
        private readonly ObservableCollection<ChannelRowViewModel> _buses = new ObservableCollection<ChannelRowViewModel>();
        private readonly ObservableCollection<ChannelPreset> _presets = new ObservableCollection<ChannelPreset>();
        private readonly DispatcherTimer _refreshTimer;

        private int _lastStripCount = -1;
        private int _lastBusCount = -1;

        public SettingsControl(VoicemeeterPlugin plugin)
        {
            InitializeComponent();

            _plugin = plugin;

            StripsGrid.ItemsSource = _strips;
            BusesGrid.ItemsSource = _buses;

            PresetsGrid.ItemsSource = _presets;
            foreach (ChannelPreset preset in _plugin.Settings.Presets)
            {
                _presets.Add(preset);
            }

            InitializeNewPresetControls();
            RebuildRows();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();

            // SimHub appears to reuse this same UserControl instance across settings-page navigation
            // rather than recreating it, so Unloaded/Loaded can fire more than once over its lifetime -
            // without restarting here, navigating away (Unloaded stops the timer) and back would leave
            // the display frozen at whatever it showed just before you left.
            Unloaded += (s, e) => _refreshTimer.Stop();
            Loaded += (s, e) =>
            {
                RefreshTimer_Tick(this, EventArgs.Empty);
                _refreshTimer.Start();
            };
        }

        private void InitializeNewPresetControls()
        {
            var channelOptions = new List<ChannelOption>();
            for (int i = 0; i < VoicemeeterPlugin.MaxStrips; i++)
            {
                channelOptions.Add(new ChannelOption { Label = $"Strip{i}", Kind = ChannelKind.Strip, Index = i });
            }

            for (int i = 0; i < VoicemeeterPlugin.MaxBuses; i++)
            {
                channelOptions.Add(new ChannelOption { Label = $"Bus{i}", Kind = ChannelKind.Bus, Index = i });
            }

            NewPresetChannelCombo.ItemsSource = channelOptions;
            NewPresetChannelCombo.DisplayMemberPath = "Label";
            NewPresetChannelCombo.SelectedIndex = 0;

            var functionOptions = new List<FunctionOption>
            {
                new FunctionOption { Label = "Set gain", Function = PresetFunction.GainSet, UsesValue = true },
                new FunctionOption { Label = "Step gain", Function = PresetFunction.GainStep, UsesValue = true },
                new FunctionOption { Label = "Mute on", Function = PresetFunction.MuteOn, UsesValue = false },
                new FunctionOption { Label = "Mute off", Function = PresetFunction.MuteOff, UsesValue = false },
                new FunctionOption { Label = "Toggle mute", Function = PresetFunction.MuteToggle, UsesValue = false },
            };

            NewPresetFunctionCombo.ItemsSource = functionOptions;
            NewPresetFunctionCombo.DisplayMemberPath = "Label";
            NewPresetFunctionCombo.SelectedIndex = 0;
        }

        private void NewPresetFunctionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = NewPresetFunctionCombo.SelectedItem as FunctionOption;
            bool usesValue = selected?.UsesValue ?? false;
            NewPresetValueTextBox.IsEnabled = usesValue;
            if (!usesValue)
            {
                NewPresetValueTextBox.Text = string.Empty;
            }
        }

        private void AddPreset_Click(object sender, RoutedEventArgs e)
        {
            var channel = NewPresetChannelCombo.SelectedItem as ChannelOption;
            var function = NewPresetFunctionCombo.SelectedItem as FunctionOption;
            if (channel == null || function == null)
            {
                return;
            }

            float value = 0f;
            if (function.UsesValue)
            {
                if (!float.TryParse(NewPresetValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    MessageBox.Show("Enter a numeric value for this preset.", "Voicemeeter Control", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            ChannelPreset preset = _plugin.AddPreset(channel.Kind, channel.Index, function.Function, value);
            if (preset == null)
            {
                MessageBox.Show("An identical preset (same channel, function and value) already exists.", "Voicemeeter Control", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _presets.Add(preset);
        }

        private void RemovePreset_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).DataContext is ChannelPreset preset))
            {
                return;
            }

            _plugin.RemovePreset(preset);
            _presets.Remove(preset);
        }

        private void TestFirePreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ChannelPreset preset)
            {
                _plugin.ApplyPreset(preset);
            }
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            bool connected = _plugin.Remote.IsConnected;

            StatusText.Text = connected
                ? $"Connected to Voicemeeter {_plugin.Remote.Type} ({_plugin.Remote.StripCount} strips, {_plugin.Remote.BusCount} buses)"
                : "Not connected - waiting for Voicemeeter to be running";

            int stripCount = connected ? _plugin.Remote.StripCount : 0;
            int busCount = connected ? _plugin.Remote.BusCount : 0;

            if (stripCount != _lastStripCount || busCount != _lastBusCount)
            {
                RebuildRows();
            }

            foreach (var row in _strips)
            {
                row.RaiseRefresh();
            }

            foreach (var row in _buses)
            {
                row.RaiseRefresh();
            }
        }

        private void RebuildRows()
        {
            _lastStripCount = RebuildRows(_strips, ChannelKind.Strip);
            _lastBusCount = RebuildRows(_buses, ChannelKind.Bus);
        }

        private int RebuildRows(ObservableCollection<ChannelRowViewModel> rows, ChannelKind kind)
        {
            var remote = _plugin.Remote;
            int count = remote.IsConnected ? remote.GetChannelCount(kind) : 0;

            rows.Clear();
            for (int i = 0; i < count; i++)
            {
                rows.Add(new ChannelRowViewModel(remote, kind, i));
            }

            return count;
        }
    }
}
