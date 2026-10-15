using System;
using System.Runtime.InteropServices;

namespace LightIsland;

/// <summary>
/// Reads how loud the sound going to the speakers is right now (0 to 1), using the Windows
/// audio meter. It does not record anything, needs no extra library and uses almost no CPU.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private IAudioMeterInformation? _meter;
    private long _retryAt;
    private long _openedAt;

    // The meter belongs to ONE audio device. When headphones are plugged in or out, Windows switches the default
    // device, but the old meter stays "valid" and just reports silence forever (so the equalizer froze until the
    // app was restarted). So the meter is re-opened on the current default device every 2 seconds.
    private const long ReopenMs = 2000;

    /// <summary>Current output loudness, 0 (silence) to 1 (full scale).</summary>
    public float GetPeak()
    {
        try
        {
            long now = Environment.TickCount64;
            if (_meter == null || now - _openedAt >= ReopenMs)
            {
                if (_meter == null && now < _retryAt) return 0;
                IAudioMeterInformation? fresh = Open();
                if (fresh != null)
                {
                    Release();
                    _meter = fresh;
                    _openedAt = now;
                }
                else if (_meter == null)
                {
                    _retryAt = now + 1000; // no output device right now: look again soon
                    return 0;
                }
                else
                {
                    Release(); // the old device is gone and there is no new one yet
                    _retryAt = now + 1000;
                    return 0;
                }
            }

            if (_meter!.GetPeakValue(out float peak) != 0)
            {
                Release();
                _retryAt = 0;
                return 0;
            }
            return Math.Clamp(peak, 0f, 1f);
        }
        catch
        {
            Release();
            return 0;
        }
    }

    private static IAudioMeterInformation? Open()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            // 0 = render (speakers), 1 = multimedia role
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device) != 0) return null;
            try
            {
                Guid iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out object meter) != 0) return null;
                return (IAudioMeterInformation)meter;
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

    private void Release()
    {
        if (_meter != null)
        {
            try { Marshal.ReleaseComObject(_meter); } catch { }
            _meter = null;
        }
    }

    public void Dispose() => Release();

    // ---------- Windows Core Audio COM interfaces (only the methods we use) ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
