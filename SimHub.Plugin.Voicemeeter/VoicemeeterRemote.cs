using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SimHub.Plugin.Voicemeeter
{
    /// <summary>
    /// Thin P/Invoke wrapper around VoicemeeterRemote.dll (the official Voicemeeter Remote API).
    /// Handles login/logout lifecycle and float/string parameter get/set.
    /// </summary>
    /// <remarks>
    /// SimHub's host process is 32-bit only, so this must load the 32-bit "VoicemeeterRemote.dll"
    /// (not the 64-bit "VoicemeeterRemote64.dll"). That file lives under Voicemeeter's install
    /// directory, which is not on PATH, so it's preloaded from its resolved full path before any
    /// P/Invoke call - the OS module loader then matches plain DllImport("VoicemeeterRemote.dll")
    /// calls against the already-loaded module by file name.
    /// </remarks>
    public sealed class VoicemeeterRemote : IDisposable
    {
        private const string DllName = "VoicemeeterRemote.dll";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static bool _nativeLibraryPrepared;

        [DllImport(DllName)]
        private static extern int VBVMR_Login();

        [DllImport(DllName)]
        private static extern int VBVMR_Logout();

        [DllImport(DllName)]
        private static extern int VBVMR_RunVoicemeeter(int vType);

        [DllImport(DllName)]
        private static extern int VBVMR_GetVoicemeeterType(ref int type);

        [DllImport(DllName)]
        private static extern int VBVMR_GetVoicemeeterVersion(ref int version);

        [DllImport(DllName)]
        private static extern int VBVMR_IsParametersDirty();

        [DllImport(DllName, CharSet = CharSet.Ansi)]
        private static extern int VBVMR_GetParameterFloat(string paramName, ref float value);

        [DllImport(DllName, CharSet = CharSet.Ansi)]
        private static extern int VBVMR_SetParameterFloat(string paramName, float value);

        [DllImport(DllName, CharSet = CharSet.Ansi)]
        private static extern int VBVMR_SetParameters(string script);

        [DllImport(DllName, CharSet = CharSet.Ansi)]
        private static extern int VBVMR_GetParameterStringA(string paramName, [MarshalAs(UnmanagedType.LPArray)] byte[] value);

        /// <summary>
        /// Guards every call into VoicemeeterRemote.dll and all state below. SimHub invokes button/
        /// control actions and the per-tick DataUpdate telemetry callback from different threads, and
        /// this native client is not documented as thread-safe - concurrent Get/Set/IsParametersDirty
        /// calls from two threads at once produced inconsistent results in testing (a Set reported
        /// success but a same-process Get right after kept returning the pre-Set value indefinitely).
        /// Serializing all access here removes that race entirely.
        /// </summary>
        private readonly object _syncRoot = new object();

        private bool _loggedIn;

        /// <summary>
        /// Last value we ourselves commanded for each mute/gain parameter, keyed by parameter name.
        /// Voicemeeter's GetParameterFloat only reflects an up-to-date value once VBVMR_IsParametersDirty
        /// has been (re-)polled and the client's local mirror had a chance to resync - a raw Get called
        /// right after our own Set can otherwise return the pre-write value indefinitely. Mutate-in-place
        /// operations (toggle mute, gain up/down) and all display reads therefore go through this cache
        /// instead of trusting a fresh Get for anything we ourselves already know we set.
        /// </summary>
        private readonly Dictionary<string, float> _lastCommandedValue = new Dictionary<string, float>();

        public bool IsConnected { get; private set; }

        public VoicemeeterType Type { get; private set; } = VoicemeeterType.Unknown;

        /// <summary>
        /// Number of input strips (hardware + virtual) exposed by the running Voicemeeter edition.
        /// </summary>
        public int StripCount { get; private set; }

        /// <summary>
        /// Number of output buses (hardware + virtual) exposed by the running Voicemeeter edition.
        /// </summary>
        public int BusCount { get; private set; }

        /// <summary>
        /// Attempts to (re)connect to a running Voicemeeter instance. Safe to call repeatedly, e.g. every
        /// DataUpdate tick, it is a no-op once already connected and cheap when Voicemeeter isn't running.
        /// </summary>
        public bool TryConnect()
        {
            lock (_syncRoot)
            {
                if (IsConnected)
                {
                    return true;
                }

                try
                {
                    EnsureNativeLibraryLoaded();

                    if (!_loggedIn)
                    {
                        int loginResult = VBVMR_Login();
                        // 0 = OK, 1 = OK but Voicemeeter isn't running yet.
                        if (loginResult != 0 && loginResult != 1)
                        {
                            return false;
                        }

                        _loggedIn = true;

                        // Required by Voicemeeter's own API contract: without an initial call to
                        // VBVMR_IsParametersDirty right after login, VBVMR_GetParameterFloat can return
                        // stale/undefined values rather than the real current state.
                        VBVMR_IsParametersDirty();
                    }

                    int type = 0;
                    int typeResult = VBVMR_GetVoicemeeterType(ref type);
                    if (typeResult != 0)
                    {
                        // Voicemeeter process not running / not ready yet.
                        return false;
                    }

                    Type = (VoicemeeterType)type;
                    (StripCount, BusCount) = GetChannelCounts(Type);
                    IsConnected = true;
                    return true;
                }
                catch (DllNotFoundException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[Voicemeeter] Connection attempt failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Marks the connection as lost so the next TryConnect performs a fresh handshake.
        /// </summary>
        public void MarkDisconnected()
        {
            lock (_syncRoot)
            {
                IsConnected = false;
                Type = VoicemeeterType.Unknown;
                StripCount = 0;
                BusCount = 0;
                _lastCommandedValue.Clear();
            }
        }

        /// <summary>
        /// Polls Voicemeeter for external changes (its own UI, or another Remote API client) and, if
        /// any occurred, refreshes <see cref="_lastCommandedValue"/> from the live device so those
        /// changes show up wherever the plugin displays state.
        /// </summary>
        /// <remarks>
        /// Per Voicemeeter's own docs, "dirty" only fires for changes made elsewhere - not for our
        /// own Set calls on this same login session - so this is a separate, additive sync path to
        /// the immediate cache write in <see cref="SetFloat"/>, not a replacement for it.
        /// </remarks>
        public bool ParametersDirty()
        {
            lock (_syncRoot)
            {
                try
                {
                    // Bit 0: parameters changed, bit 1: levels changed, bit 6: macrobutton changed, negative on error
                    // (e.g. Voicemeeter was closed) - treat that as a lost connection.
                    int result = VBVMR_IsParametersDirty();
                    if (result < 0)
                    {
                        MarkDisconnectedLocked();
                        return false;
                    }

                    if (result > 0)
                    {
                        RefreshCacheFromDevice();
                        return true;
                    }

                    return false;
                }
                catch (Exception)
                {
                    MarkDisconnectedLocked();
                    return false;
                }
            }
        }

        private void RefreshCacheFromDevice()
        {
            for (int i = 0; i < StripCount && IsConnected; i++)
            {
                _lastCommandedValue[$"Strip[{i}].Gain"] = GetFloat($"Strip[{i}].Gain");
                if (!IsConnected)
                {
                    return;
                }

                _lastCommandedValue[$"Strip[{i}].Mute"] = GetFloat($"Strip[{i}].Mute");
            }

            for (int i = 0; i < BusCount && IsConnected; i++)
            {
                _lastCommandedValue[$"Bus[{i}].Gain"] = GetFloat($"Bus[{i}].Gain");
                if (!IsConnected)
                {
                    return;
                }

                _lastCommandedValue[$"Bus[{i}].Mute"] = GetFloat($"Bus[{i}].Mute");
            }
        }

        /// <summary>
        /// Voicemeeter clamps strip/bus gain to this range internally; a Set outside it is silently
        /// clamped by Voicemeeter itself without telling us, so callers here must clamp first -
        /// otherwise <see cref="_lastCommandedValue"/> would drift past what the device actually holds.
        /// </summary>
        public const float MinGainDb = -60f;
        public const float MaxGainDb = 12f;

        private static float ClampGain(float dbValue) => Math.Max(MinGainDb, Math.Min(MaxGainDb, dbValue));

        public float GetStripGain(int index)
        {
            lock (_syncRoot)
            {
                return GetLastCommandedOrFetch($"Strip[{index}].Gain");
            }
        }

        public void SetStripGain(int index, float dbValue)
        {
            lock (_syncRoot)
            {
                SetFloat($"Strip[{index}].Gain", ClampGain(dbValue));
            }
        }

        public bool GetStripMute(int index)
        {
            lock (_syncRoot)
            {
                return GetLastCommandedOrFetch($"Strip[{index}].Mute") >= 0.5f;
            }
        }

        public void SetStripMute(int index, bool mute)
        {
            lock (_syncRoot)
            {
                SetFloat($"Strip[{index}].Mute", mute ? 1f : 0f);
            }
        }

        public float GetBusGain(int index)
        {
            lock (_syncRoot)
            {
                return GetLastCommandedOrFetch($"Bus[{index}].Gain");
            }
        }

        public void SetBusGain(int index, float dbValue)
        {
            lock (_syncRoot)
            {
                SetFloat($"Bus[{index}].Gain", ClampGain(dbValue));
            }
        }

        public bool GetBusMute(int index)
        {
            lock (_syncRoot)
            {
                return GetLastCommandedOrFetch($"Bus[{index}].Mute") >= 0.5f;
            }
        }

        public void SetBusMute(int index, bool mute)
        {
            lock (_syncRoot)
            {
                SetFloat($"Bus[{index}].Mute", mute ? 1f : 0f);
            }
        }

        // Always leads with the same "Strip{index}"/"Bus{index}" naming used for the SimHub action and
        // property names (e.g. "GainUpBus0"), so a row shown here can always be matched back to the
        // target you'd pick in SimHub's Controls and events mapping screen - Voicemeeter's own custom
        // label (if the user set one) is appended rather than substituted, so renaming a bus/strip
        // inside Voicemeeter can never break that correlation.
        public string GetStripLabel(int index)
        {
            lock (_syncRoot)
            {
                return CombineWithTechnicalName($"Strip{index}", GetStringOrDefault($"Strip[{index}].Label", null));
            }
        }

        public string GetBusLabel(int index)
        {
            lock (_syncRoot)
            {
                return CombineWithTechnicalName($"Bus{index}", GetStringOrDefault($"Bus[{index}].Label", null));
            }
        }

        private static string CombineWithTechnicalName(string technicalName, string customLabel)
        {
            return string.IsNullOrEmpty(customLabel) ? technicalName : $"{technicalName} ({customLabel})";
        }

        /// <summary>
        /// Flips mute relative to the last value *we* commanded (falling back to a live read only
        /// the first time, before anything has been commanded yet). See the remarks on
        /// <see cref="_lastCommandedValue"/> for why this must not re-read the live value on every call.
        /// </summary>
        public void ToggleStripMute(int index)
        {
            lock (_syncRoot)
            {
                ToggleMute($"Strip[{index}].Mute");
            }
        }

        public void ToggleBusMute(int index)
        {
            lock (_syncRoot)
            {
                ToggleMute($"Bus[{index}].Mute");
            }
        }

        /// <summary>
        /// Applies a relative gain step on top of the last value *we* commanded, for the same reason
        /// as <see cref="ToggleStripMute"/>.
        /// </summary>
        public void AdjustStripGain(int index, float deltaDb)
        {
            lock (_syncRoot)
            {
                AdjustGain($"Strip[{index}].Gain", deltaDb);
            }
        }

        public void AdjustBusGain(int index, float deltaDb)
        {
            lock (_syncRoot)
            {
                AdjustGain($"Bus[{index}].Gain", deltaDb);
            }
        }

        // Everything below this point must only ever be called while already holding _syncRoot.

        private void ToggleMute(string paramName)
        {
            bool current = GetLastCommandedOrFetch(paramName) >= 0.5f;
            SetFloat(paramName, current ? 0f : 1f);
        }

        private void AdjustGain(string paramName, float deltaDb)
        {
            float current = GetLastCommandedOrFetch(paramName);
            SetFloat(paramName, ClampGain(current + deltaDb));
        }

        private float GetLastCommandedOrFetch(string paramName)
        {
            if (_lastCommandedValue.TryGetValue(paramName, out float cached))
            {
                return cached;
            }

            // Force Voicemeeter's client-side mirror to resync before this first-ever read of this
            // parameter - without a fresh IsParametersDirty poll immediately before it, a cold Get can
            // return stale/undefined data (this is a documented quirk of the Remote API, confirmed by
            // testing against the real DLL). Once seeded, later reads come from the cache instead.
            try
            {
                VBVMR_IsParametersDirty();
            }
            catch (Exception)
            {
                // Ignored here - the GetFloat call right below will detect and report any real failure.
            }

            float fetched = GetFloat(paramName);
            if (IsConnected)
            {
                // GetFloat marks us disconnected (and clears the cache) on failure, in which case
                // caching this fallback 0 would just re-poison the cache we were just cleared of.
                _lastCommandedValue[paramName] = fetched;
            }

            return fetched;
        }

        private float GetFloat(string paramName)
        {
            try
            {
                float value = 0f;
                int result = VBVMR_GetParameterFloat(paramName, ref value);
                if (result != 0)
                {
                    MarkDisconnectedLocked();
                    return 0f;
                }

                return value;
            }
            catch (Exception)
            {
                MarkDisconnectedLocked();
                return 0f;
            }
        }

        private void SetFloat(string paramName, float value)
        {
            try
            {
                int result = VBVMR_SetParameterFloat(paramName, value);
                if (result != 0)
                {
                    MarkDisconnectedLocked();
                    return;
                }

                _lastCommandedValue[paramName] = value;
            }
            catch (Exception)
            {
                MarkDisconnectedLocked();
            }
        }

        private string GetStringOrDefault(string paramName, string fallback)
        {
            try
            {
                byte[] buffer = new byte[512];
                int result = VBVMR_GetParameterStringA(paramName, buffer);
                if (result != 0)
                {
                    return fallback;
                }

                string value = Encoding.ASCII.GetString(buffer).TrimEnd('\0').Trim();
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>
        /// Same as <see cref="MarkDisconnected"/> but for use by code that already holds
        /// <see cref="_syncRoot"/> (Monitor/lock is reentrant on the same thread, so this could just
        /// call MarkDisconnected directly, but naming it separately makes the locking contract at each
        /// call site explicit).
        /// </summary>
        private void MarkDisconnectedLocked()
        {
            IsConnected = false;
            Type = VoicemeeterType.Unknown;
            StripCount = 0;
            BusCount = 0;
            _lastCommandedValue.Clear();
        }

        /// <summary>
        /// Loads VoicemeeterRemote.dll from its resolved install directory so the plain
        /// DllImport("VoicemeeterRemote.dll") calls above can find it even though that
        /// directory is not on PATH.
        /// </summary>
        private static void EnsureNativeLibraryLoaded()
        {
            if (_nativeLibraryPrepared)
            {
                return;
            }

            _nativeLibraryPrepared = true;

            string installDir = FindVoicemeeterInstallDir();
            if (installDir == null)
            {
                return;
            }

            string fullPath = Path.Combine(installDir, DllName);
            if (File.Exists(fullPath))
            {
                LoadLibrary(fullPath);
            }
        }

        private static string FindVoicemeeterInstallDir()
        {
            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "VB", "Voicemeeter");
            if (Directory.Exists(defaultDir))
            {
                return defaultDir;
            }

            const string uninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(uninstallKey))
            {
                if (root == null)
                {
                    return null;
                }

                foreach (string subKeyName in root.GetSubKeyNames())
                {
                    using (RegistryKey subKey = root.OpenSubKey(subKeyName))
                    {
                        string displayName = subKey?.GetValue("DisplayName") as string;
                        if (displayName == null || !displayName.Contains("Voicemeeter"))
                        {
                            continue;
                        }

                        string uninstallString = subKey.GetValue("UninstallString") as string;
                        string dir = string.IsNullOrEmpty(uninstallString) ? null : Path.GetDirectoryName(uninstallString.Trim('"'));
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            return dir;
                        }
                    }
                }
            }

            return null;
        }

        private static (int strips, int buses) GetChannelCounts(VoicemeeterType type)
        {
            switch (type)
            {
                case VoicemeeterType.Basic:
                    // 2 physical inputs + 1 virtual input; A1 (physical) + B1 (virtual) buses.
                    return (3, 2);
                case VoicemeeterType.Banana:
                    // 3 physical + 2 virtual inputs; A1-A3 + B1-B2 buses.
                    return (5, 5);
                case VoicemeeterType.Potato:
                case VoicemeeterType.PotatoX64:
                    // 5 physical + 3 virtual inputs; A1-A5 + B1-B3 buses.
                    return (8, 8);
                default:
                    return (0, 0);
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_loggedIn)
                {
                    try
                    {
                        VBVMR_Logout();
                    }
                    catch (Exception)
                    {
                        // Best effort, Voicemeeter may already be gone.
                    }

                    _loggedIn = false;
                }

                IsConnected = false;
            }
        }
    }
}
