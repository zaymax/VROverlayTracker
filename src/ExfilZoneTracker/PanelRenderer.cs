#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ExfilZoneTracker;

/// <summary>
/// Renders the checklist panel into an RGBA8 pixel buffer for IVROverlay.SetOverlayRaw,
/// and owns the pixel layout used for laser hit-testing: RowAtPixel must mirror the
/// rectangles drawn in RenderChecklist. GDI+ is Windows-only, which matches the app.
/// </summary>
public static class PanelRenderer
{
    private const int Margin = 16;
    private const int HeaderHeight = 60;
    private const int FooterHeight = 34;
    private const int RowHeight = 44;
    private const int CheckboxSize = 24;

    private static readonly Color Background = Color.FromArgb(235, 16, 20, 28);
    private static readonly Color Accent = Color.FromArgb(255, 90, 200, 250);
    private static readonly Color HoverFill = Color.FromArgb(60, 90, 200, 250);

    public static int VisibleRowCapacity(AppConfig config) =>
        Math.Max(0, (config.PanelPixelHeight - HeaderHeight - FooterHeight) / RowHeight);

    /// <summary>Maps a panel pixel to a checklist row index, or -1 outside any row.</summary>
    public static int RowAtPixel(AppConfig config, int entryCount, int x, int y)
    {
        if (x < Margin || x > config.PanelPixelWidth - Margin || y < HeaderHeight)
            return -1;
        var row = (y - HeaderHeight) / RowHeight;
        if (row >= Math.Min(entryCount, VisibleRowCapacity(config)))
            return -1;
        return row;
    }

    public static byte[] RenderChecklist(AppConfig config, ChecklistData checklist, int hoverIndex, out int width, out int height)
    {
        width = config.PanelPixelWidth;
        height = config.PanelPixelHeight;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.Clear(Background);
            using var borderPen = new Pen(Accent, 3);
            g.DrawRectangle(borderPen, 1, 1, width - 3, height - 3);

            using var titleFont = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
            using var rowFont = new Font("Segoe UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
            using var foundFont = new Font("Segoe UI", 22, FontStyle.Strikeout, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
            using var accentBrush = new SolidBrush(Accent);
            using var hoverBrush = new SolidBrush(HoverFill);
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far };
            using var singleLine = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter,
            };

            // Header: title left, found/total progress right.
            var (found, total) = checklist.Progress;
            g.DrawString("ExfilZone Tracker", titleFont, Brushes.White, Margin, 16);
            g.DrawString($"{found}/{total}", titleFont, accentBrush, new RectangleF(0, 16, width - Margin, 34), rightAlign);
            using var dividerPen = new Pen(Color.FromArgb(120, 90, 200, 250), 1);
            g.DrawLine(dividerPen, Margin, HeaderHeight - 4, width - Margin, HeaderHeight - 4);

            var entries = checklist.Entries;
            var capacity = VisibleRowCapacity(config);

            if (entries.Count == 0)
            {
                g.DrawString("Checklist is empty.", rowFont, Brushes.LightGray, Margin, HeaderHeight + 20);
                g.DrawString("Add items to checklist.json next to the exe.", smallFont, Brushes.Gray, Margin, HeaderHeight + 56);
            }

            for (var i = 0; i < entries.Count && i < capacity; i++)
            {
                var rowTop = HeaderHeight + i * RowHeight;
                if (i == hoverIndex)
                    g.FillRectangle(hoverBrush, Margin, rowTop + 2, width - 2 * Margin, RowHeight - 4);

                var entry = entries[i];
                var boxY = rowTop + (RowHeight - CheckboxSize) / 2;
                using var boxPen = new Pen(entry.Found ? Accent : Color.LightGray, 2);
                g.DrawRectangle(boxPen, Margin + 6, boxY, CheckboxSize, CheckboxSize);
                if (entry.Found)
                {
                    using var checkPen = new Pen(Accent, 3);
                    g.DrawLines(checkPen, new[]
                    {
                        new Point(Margin + 11, boxY + 12),
                        new Point(Margin + 16, boxY + 18),
                        new Point(Margin + 25, boxY + 6),
                    });
                }

                g.DrawString(
                    checklist.DisplayName(entry),
                    entry.Found ? foundFont : rowFont,
                    entry.Found ? Brushes.Gray : Brushes.White,
                    new RectangleF(Margin + CheckboxSize + 18, rowTop + 8, width - Margin * 2 - CheckboxSize - 22, RowHeight - 10),
                    singleLine);
            }

            var footerText = entries.Count > capacity
                ? $"+{entries.Count - capacity} more, edit checklist.json"
                : "point with the free hand, trigger to check";
            g.DrawString(footerText, smallFont, Brushes.Gray, Margin, height - FooterHeight + 6);
        }

        return ToRgba(bitmap);
    }

    /// <summary>GDI+ stores Format32bppArgb as BGRA in memory; OpenVR raw overlays expect RGBA.</summary>
    private static byte[] ToRgba(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bgra = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);

            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var src = y * stride + x * 4;
                    var dst = (y * bitmap.Width + x) * 4;
                    rgba[dst] = bgra[src + 2];     // R
                    rgba[dst + 1] = bgra[src + 1]; // G
                    rgba[dst + 2] = bgra[src];     // B
                    rgba[dst + 3] = bgra[src + 3]; // A
                }
            }
            return rgba;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
