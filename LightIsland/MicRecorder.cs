using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LightIsland;

/// <summary>
/// Records the default microphone as 16 kHz mono 16-bit (what the speech model wants) into memory (about 32 KB per second,
/// 30 seconds at most). Plain Windows waveIn: no extra library.
/// </summary>
internal sealed class MicRecorder
{
    private const int Rate = 16000, BufCount = 4, BufBytes = 6400; // 0.2 s per buffer
    private const int MaxBytes = Rate * 2 * 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser;
        public uint dwFlags, dwLoops; public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

    private IntPtr _h;
    private readonly IntPtr[] _hdr = new IntPtr[BufCount];
    private readonly IntPtr[] _buf = new IntPtr[BufCount];
    private readonly MemoryStream _pcm = new();
    private AutoResetEvent? _ev;
    private Thread? _thread;
    private volatile bool _running;
    private static readonly int HdrSize = Marshal.SizeOf<WAVEHDR>();

    /// <summary>False when there is no microphone or Windows refused it.</summary>
    public bool Start()
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16,
            nBlockAlign = 2, nAvgBytesPerSec = Rate * 2, cbSize = 0
        };
        _ev = new AutoResetEvent(false);
        if (waveInOpen(out _h, 0xFFFFFFFF /* WAVE_MAPPER */, ref fmt, _ev.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000 /* CALLBACK_EVENT */) != 0)
        {
            _ev.Dispose();
            _ev = null;
            return false;
        }

        for (int i = 0; i < BufCount; i++)
        {
            _buf[i] = Marshal.AllocHGlobal(BufBytes);
            _hdr[i] = Marshal.AllocHGlobal(HdrSize);
            var h = new WAVEHDR { lpData = _buf[i], dwBufferLength = BufBytes };
            Marshal.StructureToPtr(h, _hdr[i], false);
            waveInPrepareHeader(_h, _hdr[i], HdrSize);
            waveInAddBuffer(_h, _hdr[i], HdrSize);
        }

        _running = true;
        _thread = new Thread(() =>
        {
            while (_running)
            {
                _ev!.WaitOne(80);
                Drain(true);
            }
        }) { IsBackground = true, Name = "LightIsland mic" };
        _thread.Start();
        waveInStart(_h);
        return true;
    }

    private void Drain(bool reAdd)
    {
        for (int i = 0; i < BufCount; i++)
        {
            if (_hdr[i] == IntPtr.Zero) continue;
            var h = Marshal.PtrToStructure<WAVEHDR>(_hdr[i]);
            if ((h.dwFlags & 1 /* WHDR_DONE */) == 0) continue;
            if (h.dwBytesRecorded > 0 && _pcm.Length < MaxBytes)
            {
                var data = new byte[h.dwBytesRecorded];
                Marshal.Copy(h.lpData, data, 0, data.Length);
                lock (_pcm) _pcm.Write(data, 0, data.Length);
            }
            if (reAdd && _running) waveInAddBuffer(_h, _hdr[i], HdrSize);
            else
            {
                // mark as taken so it is not copied twice
                h.dwFlags &= ~1u;
                h.dwBytesRecorded = 0;
                Marshal.StructureToPtr(h, _hdr[i], false);
            }
        }
    }

    /// <summary>Stops and returns the recording (16 kHz, mono, 16-bit samples without a header).</summary>
    public byte[] Stop()
    {
        if (_h == IntPtr.Zero) return Array.Empty<byte>();
        _running = false;
        try { _thread?.Join(500); } catch { }
        waveInStop(_h);
        waveInReset(_h);
        Drain(false);
        for (int i = 0; i < BufCount; i++)
        {
            waveInUnprepareHeader(_h, _hdr[i], HdrSize);
            Marshal.FreeHGlobal(_hdr[i]);
            Marshal.FreeHGlobal(_buf[i]);
            _hdr[i] = _buf[i] = IntPtr.Zero;
        }
        waveInClose(_h);
        _h = IntPtr.Zero;
        _ev?.Dispose();
        _ev = null;
        lock (_pcm) return _pcm.ToArray();
    }

    /// <summary>Loudest sample (0..32767): silent = no microphone permission or muted.</summary>
    public static int Peak(byte[] pcm)
    {
        int max = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int v = Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8)));
            if (v > max) max = v;
        }
        return max;
    }

    public static void WriteWav(string path, byte[] pcm)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
    }

    public static double Seconds(byte[] pcm) => pcm.Length / (double)(Rate * 2);
}
