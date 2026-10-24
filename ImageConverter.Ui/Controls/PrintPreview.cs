using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using ImageConverter.Core.Printing;

namespace ImageConverter.Controls;

/// <summary>
/// Preview of a print page: the sheet, the margin (dashed), the images at their positions in mm
/// (rotated images rotated) and optionally the cut marks. Drawn to scale.
/// </summary>
public sealed class PrintPreview : Control
{
    public static readonly StyledProperty<LayoutPage?> PageProperty =
        AvaloniaProperty.Register<PrintPreview, LayoutPage?>(nameof(Page));

    public static readonly StyledProperty<IReadOnlyDictionary<int, Bitmap>?> ThumbnailsProperty =
        AvaloniaProperty.Register<PrintPreview, IReadOnlyDictionary<int, Bitmap>?>(nameof(Thumbnails));

    public static readonly StyledProperty<double> MarginMmProperty =
        AvaloniaProperty.Register<PrintPreview, double>(nameof(MarginMm));

    public static readonly StyledProperty<double> GapMmProperty =
        AvaloniaProperty.Register<PrintPreview, double>(nameof(GapMm));

    public static readonly StyledProperty<bool> CutMarksProperty =
        AvaloniaProperty.Register<PrintPreview, bool>(nameof(CutMarks));

    public LayoutPage? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public IReadOnlyDictionary<int, Bitmap>? Thumbnails { get => GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public double MarginMm { get => GetValue(MarginMmProperty); set => SetValue(MarginMmProperty, value); }
    public double GapMm { get => GetValue(GapMmProperty); set => SetValue(GapMmProperty, value); }
    public bool CutMarks { get => GetValue(CutMarksProperty); set => SetValue(CutMarksProperty, value); }

    private static readonly IBrush PaperBrush = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush ShadowBrush = new ImmutableSolidColorBrush(Color.FromArgb(40, 0, 0, 0));
    private static readonly IBrush MissingBrush = new ImmutableSolidColorBrush(Color.FromRgb(225, 228, 233));
    private static readonly IPen MarginPen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromArgb(150, 47, 111, 235)), 1, new ImmutableDashStyle([4, 3], 0));
    private static readonly IPen ImageEdgePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 0.5);
    private static readonly IPen CutPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(90, 90, 90)), 0.8);

    static PrintPreview()
    {
        AffectsRender<PrintPreview>(PageProperty, ThumbnailsProperty, MarginMmProperty, GapMmProperty, CutMarksProperty);
    }

    public override void Render(DrawingContext context)
    {
        if (Page is not { } page || Bounds.Width < 20 || Bounds.Height < 20) return;

        // Fit the sheet centred, with some room for the shadow.
        const double pad = 24;
        var scale = Math.Min((Bounds.Width - 2 * pad) / page.WidthMm, (Bounds.Height - 2 * pad) / page.HeightMm);
        if (scale <= 0) return;
        var pw = page.WidthMm * scale;
        var ph = page.HeightMm * scale;
        var paper = new Rect((Bounds.Width - pw) / 2, (Bounds.Height - ph) / 2, pw, ph);

        Rect Mm(double x, double y, double w, double h) =>
            new(paper.X + x * scale, paper.Y + y * scale, w * scale, h * scale);

        context.FillRectangle(ShadowBrush, paper.Translate(new Vector(3, 4)));
        context.FillRectangle(PaperBrush, paper);

        if (MarginMm > 0)
            context.DrawRectangle(null, MarginPen,
                Mm(MarginMm, MarginMm, page.WidthMm - 2 * MarginMm, page.HeightMm - 2 * MarginMm));

        foreach (var p in page.Placements)
        {
            var r = Mm(p.X, p.Y, p.Width, p.Height);
            if (Thumbnails?.TryGetValue(p.ItemId, out var bmp) == true)
            {
                if (p.Rotated)
                {
                    // Draw in a rotated coordinate system: 90° around the centre of the slot.
                    var c = r.Center;
                    using (context.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y)
                                                 * Matrix.CreateRotation(Math.PI / 2)
                                                 * Matrix.CreateTranslation(c.X, c.Y)))
                    {
                        var turned = new Rect(c.X - r.Height / 2, c.Y - r.Width / 2, r.Height, r.Width);
                        context.DrawImage(bmp, new Rect(bmp.Size), turned);
                    }
                }
                else
                {
                    context.DrawImage(bmp, new Rect(bmp.Size), r);
                }
            }
            else
            {
                context.FillRectangle(MissingBrush, r);
            }
            context.DrawRectangle(null, ImageEdgePen, r);
        }

        if (CutMarks)
            DrawCutMarks(context, page, Mm);
    }

    // Same as in the PDF (PrintDocument.DrawCutMarks): corner marks outside the image that never reach the neighbouring image.
    private void DrawCutMarks(DrawingContext context, LayoutPage page, Func<double, double, double, double, Rect> mm)
    {
        const double offset = 0.6;
        var length = Math.Min(4.0, GapMm / 2 - offset);
        if (length < 0.8) return;

        foreach (var p in page.Placements)
        {
            foreach (var (x, y, dx, dy) in new[]
                     {
                         (p.X, p.Y, -1, -1), (p.X + p.Width, p.Y, 1, -1),
                         (p.X, p.Y + p.Height, -1, 1), (p.X + p.Width, p.Y + p.Height, 1, 1),
                     })
            {
                var h1 = mm(x + dx * offset, y, 0, 0).TopLeft;
                var h2 = mm(x + dx * (offset + length), y, 0, 0).TopLeft;
                var v1 = mm(x, y + dy * offset, 0, 0).TopLeft;
                var v2 = mm(x, y + dy * (offset + length), 0, 0).TopLeft;
                context.DrawLine(CutPen, h1, h2);
                context.DrawLine(CutPen, v1, v2);
            }
        }
    }
}
