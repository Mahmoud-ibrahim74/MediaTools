using System.Runtime.InteropServices;

namespace MediaTools.Presentation.Helpers;

/// <summary>
/// Checks whether a capture (microphone) device is muted at the Windows OS level
/// using the Core Audio (MMDevice / IAudioEndpointVolume) COM API.
/// </summary>
public static class MicrophoneMuteHelper
{
    /// <summary>
    /// Returns <c>true</c> if the default capture device is muted in Windows Sound Settings.
    /// Returns <c>false</c> if the device is not muted or the check cannot be performed.
    /// </summary>
    public static bool IsDefaultMicrophoneMuted()
    {
        try
        {
            // Create the MMDeviceEnumerator COM object.
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType is null)
            {
                return false;
            }

            var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (enumerator is null)
            {
                return false;
            }

            try
            {
                // eCapture = 1 (microphone), eConsole = 0
                var hr = enumerator.GetDefaultAudioEndpoint(1, 0, out var device);
                if (hr != 0 || device is null)
                {
                    return false;
                }

                try
                {
                    var iidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                    hr = device.Activate(ref iidEndpointVolume, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var volumeObj);
                    if (hr != 0 || volumeObj is null)
                    {
                        return false;
                    }

                    try
                    {
                        var volume = (IAudioEndpointVolume)volumeObj;
                        volume.GetMute(out var isMuted);
                        return isMuted;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(volumeObj);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch
        {
            // Fail silently — if we can't determine mute state, assume not muted.
            return false;
        }
    }

    // ─── COM Interfaces (minimal declarations for mute check) ───

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        // We only need GetMute, but COM vtable order matters.
        // IAudioEndpointVolume vtable has 12 methods before GetMute:
        //  0: RegisterControlChangeNotify
        //  1: UnregisterControlChangeNotify
        //  2: GetChannelCount
        //  3: SetMasterVolumeLevel
        //  4: SetMasterVolumeLevelScalar
        //  5: GetMasterVolumeLevel
        //  6: GetMasterVolumeLevelScalar
        //  7: SetChannelVolumeLevel
        //  8: SetChannelVolumeLevelScalar
        //  9: GetChannelVolumeLevel
        // 10: GetChannelVolumeLevelScalar
        // 11: SetMute
        // 12: GetMute  <-- this is the one we need

        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig]
        int GetChannelCount(out uint count);
        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig]
        int GetMasterVolumeLevel(out float level);
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
