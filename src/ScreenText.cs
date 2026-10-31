using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Forms = System.Windows.Forms;

namespace ZnKeyboard;

/// <summary>
/// Screenshot translation's first half: the user drags over a frozen, dimmed copy of the screen,
/// and the text in that area is recognised on this PC with Windows' own OCR. The image never
/// leaves the PC; only the recognised text is sent for translation.
/// </summary>
public static class ScreenText
{
    /// <summary>Shows the dimmed overlay on every screen; the dragged area, or null when cancelled (Esc, right click).</summary>
    public static Task<Bitmap?> SelectRegionAsync()
    {
        var done = new TaskCompletionSource<Bitmap?>();
        var overlays = new List<SnipOverlay>();
        void Finish(Bitmap? result)
        {
            if (!done.TrySetResult(result)) return;
            foreach (var overlay in overlays) overlay.Close();
        }
        foreach (var screen in Forms.Screen.AllScreens)
        {
            // The process is per-monitor DPI aware, so screen bounds and the copy are in physical pixels.
            var shot = new Bitmap(screen.Bounds.Width, screen.Bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
            overlays.Add(new SnipOverlay(screen.Bounds, shot, Finish));
        }
        foreach (var overlay in overlays) overlay.Show();
        (overlays.FirstOrDefault(o => o.Bounds.Contains(Forms.Cursor.Position)) ?? overlays.FirstOrDefault())?.Activate();
        return done.Task;
    }

    /// <summary>The text in <paramref name="image"/>, one recognised line per line.</summary>
    public static async Task<string> RecognizeAsync(Bitmap image)
    {
        // The languages in Windows' settings (Chinese and English on a Chinese PC); English if none has OCR.
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new InvalidOperationException("No OCR language is installed.");

        // Small crops read better enlarged; Windows' OCR has a size limit.
        var scale = Math.Min(image.Width < 900 ? 2.0 : 1.0, OcrEngine.MaxImageDimension / (double)Math.Max(image.Width, image.Height));
        using var sized = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, 0, 0, sized.Width, sized.Height);
        }
        using var png = new MemoryStream();
        sized.Save(png, ImageFormat.Png);
        png.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(png.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(line => JoinWords(line.Words.Select(w => w.Text))));
    }

    /// <summary>Windows' OCR splits Chinese into one "word" per character; spaces go only between Latin words.</summary>
    private static string JoinWords(IEnumerable<string> words)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            if (word.Length == 0) continue;
            if (text.Length > 0 && !IsCjk(text[^1]) && !IsCjk(word[0])) text.Append(' ');
            text.Append(word);
        }
        return text.ToString();
    }

    private static bool IsCjk(char c) => c is >= '　' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯';

    /// <summary>Mostly Chinese characters: screenshot translation into Chinese then goes to English instead.</summary>
    public static bool IsMostlyChinese(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return false;
        return letters.Count(c => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') / (double)letters.Count > 0.5;
    }

    /// <summary>One screen's frozen copy, dimmed, with the dragged rectangle shown clear.</summary>
    private sealed class SnipOverlay : Forms.Form
    {
        private readonly Bitmap shot;
        private readonly Action<Bitmap?> finish;
        private Point? start;
        private Rectangle selection;
        private static readonly Brush Dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        private static readonly Pen Edge = new(Color.FromArgb(255, 0, 120, 212), 2);

        public SnipOverlay(Rectangle bounds, Bitmap shot, Action<Bitmap?> finish)
        {
            this.shot = shot;
            this.finish = finish;
            FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual;
            AutoScaleMode = Forms.AutoScaleMode.None;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Cursor = Forms.Cursors.Cross;
            KeyPreview = true;
        }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(shot, 0, 0);
            e.Graphics.FillRectangle(Dim, ClientRectangle);
            if (selection.Width > 0 && selection.Height > 0)
            {
                e.Graphics.DrawImage(shot, selection, selection, GraphicsUnit.Pixel);
                e.Graphics.DrawRectangle(Edge, selection);
            }
        }

        protected override void OnMouseDown(Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Right) { finish(null); return; }
            start = e.Location;
            selection = Rectangle.Empty;
        }

        protected override void OnMouseMove(Forms.MouseEventArgs e)
        {
            if (start is not { } from) return;
            selection = Rectangle.FromLTRB(Math.Min(from.X, e.X), Math.Min(from.Y, e.Y), Math.Max(from.X, e.X), Math.Max(from.Y, e.Y));
            Invalidate();
        }

        protected override void OnMouseUp(Forms.MouseEventArgs e)
        {
            if (start == null) return;
            start = null;
            // A click without a drag selects nothing; keep the overlay up.
            if (selection.Width < 6 || selection.Height < 6) { selection = Rectangle.Empty; Invalidate(); return; }
            finish(shot.Clone(selection, PixelFormat.Format32bppArgb));
        }

        protected override void OnKeyDown(Forms.KeyEventArgs e)
        {
            if (e.KeyCode == Forms.Keys.Escape) finish(null);
        }

        protected override void OnFormClosed(Forms.FormClosedEventArgs e)
        {
            shot.Dispose();
            base.OnFormClosed(e);
        }
    }
}
