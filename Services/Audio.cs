using System.Runtime.InteropServices;
using System.Text;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace VoiceGrid.Services;

public sealed record DeviceChoice(string Name, string Display);

public static class AudioDevices
{
    private static bool _init;
    private static readonly object Lock = new();

    public static void EnsureInit()
    {
        lock (Lock) { if (!_init) { PortAudio.Initialize(); _init = true; } }
    }

    public static void Shutdown()
    {
        lock (Lock) { if (_init) { PortAudio.Terminate(); _init = false; } }
    }

    /// <summary>First item (Name == "") means "system default".</summary>
    public static List<DeviceChoice> List(bool input)
    {
        EnsureInit();
        var res = new List<DeviceChoice> { new("", "Výchozí zařízení systému") };
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var d = PortAudio.GetDeviceInfo(i);
            int ch = input ? d.maxInputChannels : d.maxOutputChannels;
            if (ch > 0 && !res.Any(r => r.Name == d.name)) res.Add(new DeviceChoice(d.name, d.name));
        }
        return res;
    }

    /// <summary>Index of the named device; falls back to the default if it is empty or unplugged.</summary>
    public static int Find(string? name, bool input)
    {
        EnsureInit();
        if (!string.IsNullOrEmpty(name))
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                var d = PortAudio.GetDeviceInfo(i);
                if (d.name == name && (input ? d.maxInputChannels : d.maxOutputChannels) > 0) return i;
            }
        return input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
    }
}

/// <summary>16 kHz mono float capture via PortAudio (cross-platform, no NAudio).</summary>
public sealed class MicRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private readonly object _lock = new();
    private readonly List<float> _buffer = new();
    private PaStream? _stream;
    private PaStream.Callback? _callback; // keep delegate alive

    public void Start(string? deviceName)
    {
        AudioDevices.EnsureInit();
        lock (_lock) _buffer.Clear();

        int dev = AudioDevices.Find(deviceName, input: true);
        if (dev < 0) throw new InvalidOperationException("Mikrofon nenalezen.");
        var info = PortAudio.GetDeviceInfo(dev);

        var p = new StreamParameters
        {
            device = dev,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _callback = OnAudio;
        _stream = new PaStream(p, null, SampleRate, 0, StreamFlags.ClipOff, _callback, IntPtr.Zero);
        _stream.Start();
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        var tmp = new float[frameCount];
        Marshal.Copy(input, tmp, 0, (int)frameCount);
        lock (_lock) _buffer.AddRange(tmp);
        return StreamCallbackResult.Continue;
    }

    public float[] Stop()
    {
        try { _stream?.Stop(); _stream?.Dispose(); } catch { }
        _stream = null;
        lock (_lock) return _buffer.ToArray();
    }

    public void Dispose()
    {
        Stop();
        AudioDevices.Shutdown();
    }
}

/// <summary>Plays a 16-bit PCM WAV on a chosen output device through PortAudio.</summary>
public static class WavPlayer
{
    private sealed class State { public int Pos; public volatile bool Abort; }

    public static (float[] Mono, int Rate) Read(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 12, ch = 1, rate = 22050, bits = 16, start = -1, len = 0;
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                ch = BitConverter.ToInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToInt16(b, body + 14);
            }
            else if (id == "data")
            {
                start = body;
                len = size < 0 ? b.Length - body : Math.Min(size, b.Length - body);
                break;
            }
            pos = body + size + (size & 1);
        }
        if (start < 0 || bits != 16 || ch < 1) throw new InvalidDataException("Nepodporovaný WAV.");

        int frames = len / (2 * ch);
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++) sum += BitConverter.ToInt16(b, start + (i * ch + c) * 2) / 32768f;
            mono[i] = sum / ch;
        }
        return (mono, rate);
    }

    public static async Task PlayAsync(string wavPath, string? deviceName, CancellationToken ct)
    {
        var (mono, rate) = Read(wavPath);
        AudioDevices.EnsureInit();
        int dev = AudioDevices.Find(deviceName, input: false);
        if (dev < 0) throw new InvalidOperationException("Výstupní zařízení nenalezeno.");
        var info = PortAudio.GetDeviceInfo(dev);

        var p = new StreamParameters
        {
            device = dev,
            channelCount = 2,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        var st = new State();
        var done = new TaskCompletionSource();
        PaStream.Callback cb = (IntPtr input, IntPtr output, uint frames, ref StreamCallbackTimeInfo t,
            StreamCallbackFlags f, IntPtr u) =>
        {
            var buf = new float[frames * 2];
            int n = (int)Math.Min(frames, (uint)Math.Max(0, mono.Length - st.Pos));
            for (int i = 0; i < n; i++) { buf[2 * i] = mono[st.Pos + i]; buf[2 * i + 1] = mono[st.Pos + i]; }
            st.Pos += n;
            Marshal.Copy(buf, 0, output, buf.Length);
            if (st.Abort || st.Pos >= mono.Length) { done.TrySetResult(); return StreamCallbackResult.Complete; }
            return StreamCallbackResult.Continue;
        };

        using var reg = ct.Register(() => st.Abort = true);
        var stream = new PaStream(null, p, rate, 0, StreamFlags.ClipOff, cb, IntPtr.Zero);
        try
        {
            stream.Start();
            await done.Task;
            await Task.Delay((int)(info.defaultLowOutputLatency * 1000) + 150);   // let the buffer drain
        }
        finally
        {
            try { stream.Stop(); } catch { }
            stream.Dispose();
        }
    }
}

public static class WavHelper
{
    public static byte[] ToWav(float[] s, int rate = MicRecorder.SampleRate)
    {
        int dataLen = s.Length * 2;
        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var f in s) w.Write((short)Math.Clamp(f * 32767f, -32768f, 32767f));
        w.Flush();
        return ms.ToArray();
    }
}
