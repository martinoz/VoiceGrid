using System.Diagnostics;

namespace VoiceGrid.Services;

/// <summary>
/// Offline read-back with the OS Czech voice. The voice is rendered to a WAV file and played through
/// PortAudio so the output device can be chosen. If that fails, it falls back to speaking directly
/// on the system default device.
/// </summary>
public sealed class SystemSpeaker
{
    private Process? _proc;

    public async Task SpeakAsync(string text, string? outputDevice, CancellationToken ct)
    {
        try
        {
            Stop();
            var wav = Path.Combine(Path.GetTempPath(), $"voicegrid_{Guid.NewGuid():N}.wav");
            try
            {
                if (await SynthesizeAsync(text, wav, ct) && !ct.IsCancellationRequested)
                {
                    await WavPlayer.PlayAsync(wav, outputDevice, ct);
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch { /* fall back to direct speech */ }
            finally { try { File.Delete(wav); } catch { } }

            if (!ct.IsCancellationRequested) await SpeakDirectAsync(text, ct);
        }
        catch { /* speech must never crash the app */ }
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
    }

    private async Task<bool> SynthesizeAsync(string text, string wav, CancellationToken ct)
    {
        foreach (var psi in FileCandidates(text, wav))
        {
            int code = await RunAsync(psi, ct);
            if (ct.IsCancellationRequested) return false;
            if (code == 0 && File.Exists(wav) && new FileInfo(wav).Length > 64) return true;
        }
        return false;
    }

    private async Task SpeakDirectAsync(string text, CancellationToken ct)
    {
        foreach (var psi in DirectCandidates(text))
        {
            int code = await RunAsync(psi, ct);
            if (code == 0 || ct.IsCancellationRequested) return;
        }
    }

    private async Task<int> RunAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        Process? p;
        try { p = Process.Start(psi); } catch { return -1; }       // executable missing
        if (p == null) return -1;
        _proc = p;
        using var reg = ct.Register(Stop);
        await p.WaitForExitAsync(CancellationToken.None);
        int code = p.ExitCode;
        p.Dispose();
        return code;
    }

    private static string Clean(string t) => t.Replace("'", "").Replace("\"", "");

    private static IEnumerable<ProcessStartInfo> FileCandidates(string text, string wav)
    {
        var t = Clean(text);
        if (OperatingSystem.IsMacOS())
        {
            yield return Psi("say", "-v", "Zuzana", "-o", wav, "--data-format=LEI16@22050", t);
            yield return Psi("say", "-o", wav, "--data-format=LEI16@22050", t);
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Psi("powershell", "-NoProfile", "-Command",
                "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                "try { $s.SelectVoiceByHints('NotSet','NotSet',0,[cultureinfo]'cs-CZ') } catch {}; " +
                $"$s.SetOutputToWaveFile('{wav}'); $s.Speak('{t}'); $s.Dispose()");
        }
        else
        {
            yield return Psi("espeak-ng", "-v", "cs", "-w", wav, t);
        }
    }

    private static IEnumerable<ProcessStartInfo> DirectCandidates(string text)
    {
        var t = Clean(text);
        if (OperatingSystem.IsMacOS())
        {
            yield return Psi("say", "-v", "Zuzana", t);
            yield return Psi("say", t);
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Psi("powershell", "-NoProfile", "-Command",
                "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                "try { $s.SelectVoiceByHints('NotSet','NotSet',0,[cultureinfo]'cs-CZ') } catch {}; " +
                $"$s.Speak('{t}')");
        }
        else
        {
            yield return Psi("espeak-ng", "-v", "cs", t);
            yield return Psi("spd-say", "-l", "cs", "-w", t);
        }
    }

    private static ProcessStartInfo Psi(string file, params string[] args)
    {
        var p = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) p.ArgumentList.Add(a);
        return p;
    }
}
