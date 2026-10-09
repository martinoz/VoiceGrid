using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VoiceGrid.Models;
using Whisper.net;

namespace VoiceGrid.Services;

public static class Prompts
{
    // Digit-style examples nudge Whisper-family models to emit numerals instead of words.
    public const string Numbers = "Naměřené hodnoty: 12,5; 140; 0,75; 3,2; 1024; 87; 250,4; mínus 5.";
}

/// <summary>Offline: whisper.cpp through Whisper.net. Model is downloaded once, then works without internet.</summary>
public sealed class WhisperRecognizer : IDisposable
{
    private readonly string _file;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string ModelDir => Path.Combine(SettingsStore.Dir, "models");
    public WhisperRecognizer(string modelFile) => _file = modelFile;

    public async Task EnsureLoadedAsync(IProgress<string>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory != null) return;
            var path = Path.Combine(ModelDir, _file);
            if (!File.Exists(path)) await DownloadAsync(path, progress, ct);
            progress?.Report("Načítám model…");
            _factory = await Task.Run(() => WhisperFactory.FromPath(path), ct);
        }
        finally { _gate.Release(); }
    }

    private static async Task DownloadAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{Path.GetFileName(path)}";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = path + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0; int n; var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(300))
                {
                    last = DateTime.UtcNow;
                    progress?.Report(total > 0 ? $"Stahuji model… {done * 100 / total} %" : $"Stahuji model… {done / 1_000_000} MB");
                }
            }
        }
        File.Move(tmp, path, true);
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        await EnsureLoadedAsync(null, ct);
        return await Task.Run(async () =>
        {
            using var processor = _factory!.CreateBuilder()
                .WithLanguage("cs")
                .WithPrompt(Prompts.Numbers)
                .WithThreads(Math.Clamp(Environment.ProcessorCount, 2, 8))
                .WithSingleSegment()
                .Build();
            using var wav = new MemoryStream(WavHelper.ToWav(samples));
            var sb = new StringBuilder();
            await foreach (var seg in processor.ProcessAsync(wav, ct)) sb.Append(seg.Text);
            return sb.ToString().Trim();
        }, ct);
    }

    public void Dispose() => _factory?.Dispose();
}

/// <summary>Online: any OpenAI-compatible /audio/transcriptions endpoint (OpenAI, Groq, self-hosted…).</summary>
public sealed class OpenAiRecognizer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _baseUrl, _model, _key;

    public OpenAiRecognizer(string baseUrl, string model, string key)
        => (_baseUrl, _model, _key) = (baseUrl.TrimEnd('/'), model, key);

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(WavHelper.ToWav(samples));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent("cs"), "language");
        form.Add(new StringContent(Prompts.Numbers), "prompt");

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }
}

/// <summary>Routes between online and offline. Auto = try online (8 s timeout), fall back to offline.</summary>
public sealed class SpeechService : IDisposable
{
    private WhisperRecognizer? _whisper;
    private string? _whisperFile;

    private WhisperRecognizer GetWhisper(AppSettings s)
    {
        if (_whisper == null || _whisperFile != s.WhisperModel)
        {
            _whisper?.Dispose();
            _whisper = new WhisperRecognizer(s.WhisperModel);
            _whisperFile = s.WhisperModel;
        }
        return _whisper;
    }

    public Task PreloadOfflineAsync(AppSettings s, IProgress<string> progress)
        => GetWhisper(s).EnsureLoadedAsync(progress, CancellationToken.None);

    /// <summary>First inference is much slower than the rest (lazy init); pay that cost at startup.</summary>
    public async Task WarmUpAsync(AppSettings s)
    {
        try { await GetWhisper(s).TranscribeAsync(new float[MicRecorder.SampleRate], CancellationToken.None); }
        catch { }
    }

    public async Task<(string Text, string Engine)> TranscribeAsync(float[] samples, AppSettings s, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(s.OnlineApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : s.OnlineApiKey;
        bool canOnline = !string.IsNullOrWhiteSpace(key);

        if (s.Mode == RecognitionMode.Online && !canOnline)
            throw new InvalidOperationException("Chybí API klíč pro online rozpoznávání.");

        if (s.Mode != RecognitionMode.Offline && canOnline)
        {
            try
            {
                var t = await new OpenAiRecognizer(s.OnlineBaseUrl, s.OnlineModel, key!).TranscribeAsync(samples, ct);
                return (t, "online");
            }
            catch (Exception) when (!ct.IsCancellationRequested && s.Mode == RecognitionMode.Auto)
            {
                // no internet / timeout / HTTP error -> fall through to offline
            }
        }

        return (await GetWhisper(s).TranscribeAsync(samples, ct), "offline");
    }

    public void Dispose() => _whisper?.Dispose();
}
