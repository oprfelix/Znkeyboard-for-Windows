# ZnKeyboard for Windows

System tray companion to ZnKeyboard for Android and macOS: polish or translate selected text in any
Windows app, using the same prompts and 14 target languages. Providers: Google Gemini (Flash-Lite
models only) or any OpenAI-compatible API, with a DeepSeek preset (address built in, `deepseek-flash` by
default) or a custom address, each with its own key in Windows Credential Manager.

- Select text in a text box (drag, double-click, Ctrl+A) and 润色 | AI 翻译为 [EN ▾] appears beside it.
- **Ctrl+Alt+R** polish the selected text · **Ctrl+Alt+T** translate it (both changeable in Settings)
- **Ctrl+Alt+E** screenshot translate: drag over part of the screen; its text is recognised on this PC
  (Windows OCR) and translated faithfully into your reading language (Simplified Chinese by default;
  Chinese text goes to English). The image never leaves the PC.
- A card near the pointer shows the result: **Enter** replaces the selection, **Esc** cancels, **Ctrl+C** copies. When translating, its header switches the target language and translates again.
- Tray icon menu: polish, translate, screenshot translate, target language, Settings, Check for Updates,
  About, Quit. Double-click opens Settings.
- Settings follow the Windows 11 style (Fluent controls, Mica, light/dark) through WPF-UI (MIT).

Windows 10/11 x64 with the .NET 10 Desktop Runtime. Settings live in `%APPDATA%\ZnKeyboard\settings.json`.
Windows running as administrator ignore input from a normal app, so text there can't be replaced.

Build (works on macOS too; it only runs on Windows): `scripts/build.sh` → `build/ZnKeyboard-Windows-<version>.zip`.

Licensed under GPL-3.0 (see LICENSE). Prompts come from ZnKeyboard for Android.
