# ZnKeyboard for Windows

System tray companion to ZnKeyboard for Android and macOS: polish or translate selected text in any
Windows app, using the same prompts and 14 target languages. Providers: Google Gemini (Flash-Lite
models only) or any OpenAI-compatible API, with a DeepSeek preset (address built in, `deepseek-flash` by
default) or a custom address, each with its own key in Windows Credential Manager.

- Select text in a text box (drag, double-click, Ctrl+A) and 润色 | AI 翻译为 [EN ▾] appears beside it.
- Reading translation (off by default, Settings → Usage): select text on a web page or in a document with the
  mouse and 翻译为 [ZH ▾] appears; it translates faithfully into the reading language screenshots use, and
  the card shows the original above the translation, each to copy.
- **Ctrl+Shift+1** translate the selection to read · **Ctrl+Shift+E** polish it · **Ctrl+Shift+R** translate it
  (all changeable in Settings)
- **Ctrl+Shift+2** screenshot translate: drag over part of the screen; its text is recognised on this PC
  (Windows OCR) and translated faithfully into your reading language (Simplified Chinese by default;
  Chinese text goes to English). The image never leaves the PC.
- A card near the pointer shows the result: **Enter** replaces the selection, **Esc** cancels, **Ctrl+C** copies. When translating, its header switches the target language and translates again.
- Clicking elsewhere closes the card. Translations to read (reading and screenshot) have a pin in the header that
  keeps them open, remembered for next time.
- Tray icon menu: polish, translate, screenshot translate, target language, Settings, Check for Updates,
  About, Quit. Double-click opens Settings.
- Settings follow the Windows 11 style (Fluent controls, Mica, light/dark) through WPF-UI (MIT).

Website: [znkeyboard.2amz.com](https://znkeyboard.2amz.com/) (mirror of oprfelix.github.io/znkeyboard). Updates come
from znkeyboard.2amz.com, or from GitHub Releases when that fails. Once a day ZnKeyboard sends an anonymous ping
(a random ID, platform, version and how many times it polished or translated; never any text) for the usage
numbers on the website; Settings → General turns it off.

Windows 10/11 x64 with the .NET 10 Desktop Runtime. Settings live in `%APPDATA%\ZnKeyboard\settings.json`.
Windows running as administrator ignore input from a normal app, so text there can't be replaced.

Build (works on macOS too; it only runs on Windows): `scripts/build.sh` → `build/ZnKeyboard-Windows-<version>.zip`.

Licensed under GPL-3.0 (see LICENSE). Prompts come from ZnKeyboard for Android.
