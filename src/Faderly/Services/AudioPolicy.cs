using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Faderly.Services
{
    /// <summary>
    /// Per-app output device ("App volume and device preferences" in Windows Settings).
    /// Windows doesn't document this API; it's the same internal WinRT factory that Windows Settings,
    /// EarTrumpet and SoundSwitch use. Everything here fails soft: if the call isn't available,
    /// the feature just hides itself.
    /// </summary>
    internal static unsafe class AudioPolicy
    {
        private const string ClassId = "Windows.Media.Internal.AudioPolicyConfig";

        // Windows 11 / Windows Server 2022 (build 21390+) use a new interface id; the layout is the same.
        private static readonly Guid IidModern = new("ab3d4648-e242-459f-b02f-541c70306324");
        private static readonly Guid IidLegacy = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

        // vtable: IUnknown (3) + IInspectable (3) + 19 undocumented methods, then:
        private const int SlotSetPersisted = 25;
        private const int SlotGetPersisted = 26;

        private const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
        private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        private const int FlowRender = 0;
        private const int RoleConsole = 0, RoleMultimedia = 1;

        private static IntPtr _factory;
        private static bool _initialized;

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

        /// <summary>True on Windows 10 1803+ where the per-app device API exists.</summary>
        public static bool IsSupported => Factory != IntPtr.Zero;

        private static IntPtr Factory
        {
            get
            {
                if (_initialized) return _factory;
                _initialized = true;
                try
                {
                    if (Environment.OSVersion.Version.Build < 17134) return _factory; // before 1803

                    var iid = Environment.OSVersion.Version.Build >= 21390 ? IidModern : IidLegacy;
                    int hr = WindowsCreateString(ClassId, (uint)ClassId.Length, out IntPtr classId);
                    if (hr != 0) return _factory;
                    try
                    {
                        // Fails cleanly (no crash) if this Windows build doesn't offer the interface.
                        hr = RoGetActivationFactory(classId, ref iid, out IntPtr factory);
                        if (hr == 0) _factory = factory;
                        else Log.Info($"Per-app devices unavailable (0x{hr:X8}).");
                    }
                    finally
                    {
                        WindowsDeleteString(classId);
                    }
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("Per-app device API", ex);
                }
                return _factory;
            }
        }

        /// <summary>The device id an app is pinned to, or null when it follows the Windows default.</summary>
        public static string? GetAppDevice(uint processId)
        {
            IntPtr factory = Factory;
            if (factory == IntPtr.Zero || processId == 0) return null;
            try
            {
                var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)
                    (*(IntPtr**)factory)[SlotGetPersisted];
                IntPtr hstring = IntPtr.Zero;
                int hr = get(factory, processId, FlowRender, RoleMultimedia, &hstring);
                if (hr != 0 || hstring == IntPtr.Zero) return null; // E_INVALIDARG when the app has no audio
                try
                {
                    IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
                    string? raw = buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer, (int)length);
                    return Unpack(raw);
                }
                finally
                {
                    WindowsDeleteString(hstring);
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("Reading app device", ex);
                return null;
            }
        }

        /// <summary>Pin an app's output to a device, or pass null to follow the Windows default again.</summary>
        public static bool SetAppDevice(IEnumerable<uint> processIds, string? deviceId)
        {
            IntPtr factory = Factory;
            if (factory == IntPtr.Zero) return false;

            IntPtr hstring = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    string full = DevicePrefix + deviceId + RenderSuffix;
                    if (WindowsCreateString(full, (uint)full.Length, out hstring) != 0) return false;
                }

                var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)
                    (*(IntPtr**)factory)[SlotSetPersisted];

                bool any = false;
                var done = new HashSet<uint>();
                foreach (uint pid in processIds)
                {
                    if (pid == 0 || !done.Add(pid)) continue;
                    int a = set(factory, pid, FlowRender, RoleMultimedia, hstring);
                    int b = set(factory, pid, FlowRender, RoleConsole, hstring);
                    if (a == 0 && b == 0) any = true;
                    else Log.Info($"Set app device for pid {pid}: 0x{a:X8} / 0x{b:X8}");
                }
                return any;
            }
            catch (Exception ex)
            {
                Log.Error("Setting app device", ex);
                return false;
            }
            finally
            {
                if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            }
        }

        private static string? Unpack(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase)) id = id.Substring(DevicePrefix.Length);
            int hash = id.IndexOf('#');
            if (hash >= 0) id = id.Substring(0, hash);
            return id.Length == 0 ? null : id;
        }
    }
}
