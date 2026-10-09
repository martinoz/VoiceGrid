using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceGrid.Models;
using VoiceGrid.Services;

namespace VoiceGrid.ViewModels;

public enum VoiceState { Idle, Recording, Transcribing, Confirming }

public partial class MainViewModel : ObservableObject
{
    public const int ValueColumns = 3;

    private readonly SettingsStore _store = new();
    private readonly MicRecorder _mic = new();
    private readonly SystemSpeaker _speaker = new();
    private readonly SpeechService _speech = new();
    private CancellationTokenSource? _speakCts;
    private double? _pending;

    public AppSettings Settings { get; }
    public ObservableCollection<MeasurementRow> Rows { get; } = new();

    public IReadOnlyList<RecognitionMode> ModeValues { get; } = Enum.GetValues<RecognitionMode>();
    public IReadOnlyList<AdvanceDirection> DirectionValues { get; } = Enum.GetValues<AdvanceDirection>();
    public IReadOnlyList<string> ModelChoices { get; } = new[]
    {
        "ggml-large-v3-turbo-q5_0.bin", "ggml-medium-q5_0.bin", "ggml-small-q5_1.bin", "ggml-base-q5_1.bin"
    };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HintText))] private VoiceState _state;
    [ObservableProperty] private IReadOnlyList<DeviceChoice> _inputDevices = Array.Empty<DeviceChoice>();
    [ObservableProperty] private IReadOnlyList<DeviceChoice> _outputDevices = Array.Empty<DeviceChoice>();
    [ObservableProperty] private string _status = "Připraveno.";
    [ObservableProperty] private string _heard = "";
    [ObservableProperty] private string _pendingDisplay = "";
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyLabel))] private bool _isCapturingKey;

    public int CurrentRow { get; private set; }
    public int CurrentCol { get; private set; }   // 0..ValueColumns-1 (grid column index - 1)
    public event Action? CursorChanged;           // view moves the grid selection

    public string CursorText => Rows.Count == 0 ? "" : $"Aktivní buňka: {Rows[CurrentRow].Name} · Hodnota {CurrentCol + 1}";
    public string KeyLabel => IsCapturingKey ? "Stiskněte novou klávesu… (Esc = zrušit)" : Settings.PushToTalkKey.ToString();
    public string HintText => State switch
    {
        VoiceState.Recording => "● Nahrávám… pusťte klávesu po domluvení",
        VoiceState.Transcribing => "Zpracovávám…",
        VoiceState.Confirming => "Enter / „ano“ = potvrdit · klávesa + číslo = přepsat · Esc / „ne“ = znovu",
        _ => $"Držte [{Settings.PushToTalkKey}] a řekněte číslo"
    };

    public MainViewModel()
    {
        Settings = _store.Load();
        Settings.PropertyChanged += (_, e) =>
        {
            _store.Save(Settings);
            OnPropertyChanged(nameof(KeyLabel));
            OnPropertyChanged(nameof(HintText));
            if (e.PropertyName == nameof(AppSettings.WhisperModel)) _ = InitializeAsync();   // reload model, no restart
        };
        for (int i = 1; i <= 10; i++) Rows.Add(new MeasurementRow { Name = $"Vzorek {i}" });
        RefreshDevices();
    }

    // ------------------------------------------------------------ startup

    public async Task InitializeAsync()
    {
        if (Settings.Mode == RecognitionMode.Online) return;
        try
        {
            var progress = new Progress<string>(s => Status = s);   // captured on UI thread
            await _speech.PreloadOfflineAsync(Settings, progress);
            Status = "Zahřívám model…";
            var sw = Stopwatch.StartNew();
            await _speech.WarmUpAsync(Settings);
            Status = $"Offline model je připraven (zahřátí {sw.Elapsed.TotalSeconds:0.0} s).";
        }
        catch (Exception ex)
        {
            Status = $"Offline model nelze načíst ({ex.Message}). Online režim stále funguje, pokud je zadán klíč.";
        }
    }

    // ------------------------------------------------------------ push-to-talk

    public Task OnPttDownAsync()
    {
        if (State is VoiceState.Recording or VoiceState.Transcribing) return Task.CompletedTask;
        _speakCts?.Cancel();
        _speaker.Stop();                       // never record our own read-back
        try
        {
            _mic.Start(Settings.InputDevice);
            State = VoiceState.Recording;
            Status = "Poslouchám…";
        }
        catch (Exception ex) { Status = "Mikrofon: " + ex.Message; }
        return Task.CompletedTask;
    }

    public async Task OnPttUpAsync()
    {
        if (State != VoiceState.Recording) return;
        State = VoiceState.Transcribing;
        Status = "Zpracovávám…";
        try
        {
            await Task.Delay(250);             // keep recording a moment so the last word is not clipped
            var samples = _mic.Stop();
            if (samples.Length < MicRecorder.SampleRate / 3 || Rms(samples) < 0.003f)
            {
                Fail("Nic jsem neslyšel – zkuste to znovu.");
                return;
            }
            Status = $"Zpracovávám… (záznam {samples.Length / (double)MicRecorder.SampleRate:0.0} s)";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var sw = Stopwatch.StartNew();
            var (text, engine) = await _speech.TranscribeAsync(samples, Settings, cts.Token);
            sw.Stop();
            HandleText(text, $"{engine}, {sw.Elapsed.TotalSeconds:0.0} s");
        }
        catch (OperationCanceledException)
        {
            _mic.Stop();
            Fail("Rozpoznávání trvalo přes 60 s – v Nastavení zvolte menší model.");
        }
        catch (Exception ex)
        {
            _mic.Stop();
            Fail("Chyba rozpoznávání: " + ex.Message);
        }
    }

    private void HandleText(string text, string engine)
    {
        Heard = $"„{text}“  ({engine})";

        if (CzechNumberParser.TryParse(text, out var v))
        {
            _pending = v;
            HasPending = true;
            PendingDisplay = v.ToString("0.####", CultureInfo.CurrentCulture);
            State = VoiceState.Confirming;
            Status = "Potvrďte hodnotu.";
            Speak(CzechNumberWords.ToSpoken(v));       // read it back
            return;
        }

        var intent = CzechNumberParser.GetIntent(text);
        if (_pending.HasValue && intent == Intent.Yes) { Confirm(); return; }
        if (_pending.HasValue && intent == Intent.No) { RetryInternal(speak: true); return; }

        Speak("Nerozuměl jsem.");
        Fail($"Nerozuměl jsem: „{text}“. Zkuste to znovu.");
    }

    // ------------------------------------------------------------ confirm / retry

    [RelayCommand]
    private void Confirm()
    {
        if (_pending is not { } v) return;
        Rows[CurrentRow].Set(CurrentCol, v);
        ClearPending();
        Status = $"Uloženo: {v.ToString("0.####", CultureInfo.CurrentCulture)}";
        if (Settings.AutoAdvance) Advance();
        else CursorChanged?.Invoke();
    }

    [RelayCommand]
    private void Retry() => RetryInternal(speak: false);

    private void RetryInternal(bool speak)
    {
        ClearPending();
        Status = "Zrušeno – držte klávesu a řekněte číslo znovu.";
        if (speak) Speak("Zopakujte hodnotu.");
    }

    private void ClearPending()
    {
        _pending = null;
        HasPending = false;
        PendingDisplay = "";
        State = VoiceState.Idle;
    }

    private void Fail(string message)
    {
        Status = message;
        State = _pending.HasValue ? VoiceState.Confirming : VoiceState.Idle;
    }

    // ------------------------------------------------------------ cursor

    private void Advance()
    {
        int r = CurrentRow, c = CurrentCol;
        if (Settings.Direction == AdvanceDirection.Down)
        {
            r++;
            if (r >= Rows.Count) { r = 0; c++; }
        }
        else
        {
            c++;
            if (c >= ValueColumns) { c = 0; r++; }
        }

        if (r >= Rows.Count || c >= ValueColumns)
        {
            Status += " · Tabulka je vyplněná.";
            CursorChanged?.Invoke();
            return;
        }
        MoveTo(r, c);
    }

    private void MoveTo(int row, int col)
    {
        CurrentRow = row;
        CurrentCol = col;
        OnPropertyChanged(nameof(CursorText));
        CursorChanged?.Invoke();
    }

    /// <summary>Called by the view when the user clicks / arrows to another cell.</summary>
    public void SetCursorFromGrid(int row, int gridColumn)
    {
        if (row < 0 || row >= Rows.Count) return;
        int col = Math.Clamp(gridColumn - 1, 0, ValueColumns - 1);
        if (row == CurrentRow && col == CurrentCol) return;
        CurrentRow = row;
        CurrentCol = col;
        OnPropertyChanged(nameof(CursorText));
        if (_pending.HasValue) RetryInternal(speak: false);   // pending value belonged to the old cell
    }

    // ------------------------------------------------------------ settings: key capture

    [RelayCommand]
    private void StartKeyCapture() => IsCapturingKey = true;

    public void AssignKey(Key key)
    {
        Settings.PushToTalkKey = key;
        IsCapturingKey = false;
    }

    public void CancelKeyCapture() => IsCapturingKey = false;

    // ------------------------------------------------------------ helpers

    [RelayCommand]
    private void RefreshDevices()
    {
        try
        {
            InputDevices = AudioDevices.List(input: true);
            OutputDevices = AudioDevices.List(input: false);
        }
        catch (Exception ex) { Status = "Zvuková zařízení: " + ex.Message; }
    }

    [RelayCommand]
    private void TestOutput() => Speak("Jedna dva tři", force: true);

    private void Speak(string text, bool force = false)
    {
        if (!Settings.SpeakBack && !force) return;
        _speakCts?.Cancel();
        _speakCts = new CancellationTokenSource();
        _ = _speaker.SpeakAsync(text, Settings.OutputDevice, _speakCts.Token);
    }

    private static float Rms(float[] s)
    {
        double sum = 0;
        foreach (var f in s) sum += f * f;
        return (float)Math.Sqrt(sum / Math.Max(1, s.Length));
    }
}
