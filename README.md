# VoiceGrid – Czech spoken numbers into an Avalonia DataGrid

Hold the push-to-talk key (default **Right Ctrl**, changeable in Nastavení), say a number in Czech,
release. The app writes down what it heard, reads it back (OS Czech voice) and waits:

| Action | Result |
|---|---|
| Enter, or say "ano" | value saved; with "auto-advance" on, cursor moves to the next cell |
| Esc, or say "ne" / "znovu" | discard, say it again |
| Hold key and say another number | replaces the pending value |

## Run
    dotnet run
First start downloads the offline Whisper model (~574 MB for the default) to
`%APPDATA%/VoiceGrid/models` (macOS/Linux: `~/.config/VoiceGrid/models`). After that no internet is needed.

* macOS: allow microphone access for Rider / Terminal (a packaged .app needs `NSMicrophoneUsageDescription`).
  Czech voice "Zuzana": System Settings → Accessibility → Spoken Content → System Voice → Manage Voices.
* Online: set `OPENAI_API_KEY` or fill the key in Nastavení. Mode **Auto** tries online (8 s timeout)
  and silently falls back to offline Whisper.
* Settings are saved to `settings.json` next to the models folder.

## Accepted input
Digits ("12,5", "1 024", "-5") and words: "dvanáct celá pět", "pětadvacet", "sto dvacet tři",
"dvě stě", "tisíc pět set", "mínus tři", "čárka pět", digit-by-digit "dva pět tři" = 253.
