using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VoiceGrid.Models;

public enum RecognitionMode { Auto, Offline, Online }
public enum AdvanceDirection { Down, Right }

public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private Key _pushToTalkKey = Key.RightCtrl;
    [ObservableProperty] private bool _autoAdvance = true;
    [ObservableProperty] private AdvanceDirection _direction = AdvanceDirection.Down;
    [ObservableProperty] private bool _speakBack = true;

    // "" = system default device; otherwise the exact PortAudio device name.
    [ObservableProperty] private string _inputDevice = "";
    [ObservableProperty] private string _outputDevice = "";

    [ObservableProperty] private RecognitionMode _mode = RecognitionMode.Auto;
    [ObservableProperty] private string _whisperModel = "ggml-small-q5_1.bin";

    // Any OpenAI-compatible /audio/transcriptions endpoint. Empty key -> env var OPENAI_API_KEY.
    [ObservableProperty] private string _onlineBaseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _onlineModel = "gpt-4o-transcribe";
    [ObservableProperty] private string _onlineApiKey = "";
}
