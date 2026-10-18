using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Satsuma.Core;
using Satsuma.Ops;

namespace Satsuma.UI;

/// <summary>Draws the radial menu. All geometry is in DIPs around (Cx, Cy).</summary>
public sealed class WheelControl : FrameworkElement
{
    public const double Size = 440;
    public const double Cx = Size / 2, Cy = 204;
    public const double OuterR = 178, InnerR = 72;
    const double GapPx = 3;

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface BodyFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface IconFace = new(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    List<WheelItem> _items = new();
    double[] _hover = Array.Empty<double>();
    double _centerHover;
    double _appear;           // 0 = invisible (but still hit-testable), 1 = fully shown
    double _appearTarget;
    double _spin;             // small rotation used when switching modes
    int _hot = -1;
    bool _centerHot;
    bool _animating;

    public WheelMode Mode { get; private set; }
    public string Caption { get; set; } = "";
    public string? SubCaption { get; set; }
    public string HubGlyph { get; set; } = "";
    public string HubLabel { get; set; } = "";
    public string? EmptyText { get; set; }
    public bool KeyboardMode { get; set; }

    public IReadOnlyList<WheelItem> Items => _items;
    public int HotIndex => _hot;
    public bool CenterHot => _centerHot;
    public bool IsShown => _appearTarget > 0;

    public event Action? Hidden;

    public WheelControl()
    {
        Width = Size;
        Height = Size + 76;
        SnapsToDevicePixels = true;
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public void SetItems(WheelMode mode, List<WheelItem> items, bool animate)
    {
        bool changed = mode != Mode && _items.Count > 0;
        Mode = mode;
        _items = items;
        _hover = new double[items.Count];
        _hot = KeyboardMode && items.Count > 0 ? 0 : -1;
        if (animate && changed) _spin = -18;
        Kick();
    }

    public void Show() { _appearTarget = 1; Kick(); }
    public void Hide() { _appearTarget = 0; _hot = -1; Kick(); }
    public void HideInstant() { _appear = _appearTarget = 0; _hot = -1; InvalidateVisual(); }

    public void SetPointer(Point p)
    {
        var (index, center) = HitTestWheel(p);
        if (index != _hot || center != _centerHot)
        {
            _hot = index;
            _centerHot = center;
            Kick();
        }
    }

    public void ClearPointer()
    {
        if (_hot == -1 && !_centerHot) return;
        if (!KeyboardMode) _hot = -1;
        _centerHot = false;
        Kick();
    }

    public void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        _hot = _hot < 0 ? (delta > 0 ? 0 : _items.Count - 1) : ((_hot + delta) % _items.Count + _items.Count) % _items.Count;
        _centerHot = false;
        Kick();
    }

    public void Select(int index)
    {
        if (index >= 0 && index < _items.Count) { _hot = index; Kick(); }
    }

    /// <summary>Returns the slice under a point (or -1) and whether the point is in the center hub.</summary>
    public (int index, bool center) HitTestWheel(Point p)
    {
        double dx = p.X - Cx, dy = p.Y - Cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < InnerR) return (-1, true);
        if (dist > OuterR + 26 || _items.Count == 0) return (-1, false);
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90; // 0° = top, clockwise
        double step = 360.0 / _items.Count;
        angle = ((angle + step / 2) % 360 + 360) % 360;
        return ((int)(angle / step) % _items.Count, false);
    }

    void Kick()
    {
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
        InvalidateVisual();
    }

    TimeSpan _last;

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.05);
        _last = now;
        bool anim = SystemTheme.AnimationsEnabled;

        bool busy = false;
        double Approach(double v, double target, double speed)
        {
            if (!anim) return target;
            double nv = v + (target - v) * (1 - Math.Exp(-speed * dt));
            if (Math.Abs(nv - target) < 0.004) nv = target;
            if (nv != target) busy = true;
            return nv;
        }

        _appear = Approach(_appear, _appearTarget, _appearTarget > _appear ? 22 : 30);
        _spin = Approach(_spin, 0, 18);
        for (int i = 0; i < _hover.Length; i++) _hover[i] = Approach(_hover[i], i == _hot ? 1 : 0, 26);
        _centerHover = Approach(_centerHover, _centerHot ? 1 : 0, 26);

        InvalidateVisual();
        if (!busy)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            _last = TimeSpan.Zero;
            if (_appear == 0 && _appearTarget == 0) Hidden?.Invoke();
        }
    }

    // ---------------- Palette ----------------

    static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ---------------- Rendering ----------------

    protected override void OnRender(DrawingContext dc)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Always-present, nearly invisible disc: keeps the layered window hit-testable so OLE drags can find us
        // before the wheel has faded in.
        dc.DrawEllipse(B(Color.FromArgb(2, 0, 0, 0)), null, new Point(Cx, Cy), OuterR + 24, OuterR + 24);

        if (_appear <= 0.001) return;

        bool dark = SystemTheme.IsDark;
        var accent = SystemTheme.Accent;
        var onAccent = SystemTheme.OnAccent;

        var surface = dark ? Color.FromArgb(246, 39, 39, 39) : Color.FromArgb(250, 251, 251, 251);
        var hub = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var stroke = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
        var divider = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
        var text = dark ? Color.FromRgb(242, 242, 242) : Color.FromRgb(26, 26, 26);
        var muted = dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0);

        // Ease-out-back style pop: scale from 0.88 → 1.
        double t = _appear;
        double scale = 0.88 + 0.12 * (1 - Math.Pow(1 - t, 3));
        dc.PushOpacity(Math.Min(1, t * 1.15));
        dc.PushTransform(new ScaleTransform(scale, scale, Cx, Cy));

        // Shadow (two soft rings — cheaper and crisper than a bitmap effect).
        for (int i = 0; i < 6; i++)
        {
            double grow = 3 + i * 3.2;
            dc.DrawEllipse(B(Color.FromArgb((byte)(dark ? 16 - i * 2 : 12 - i * 2), 0, 0, 0)), null, new Point(Cx, Cy + 6), OuterR + grow, OuterR + grow);
        }

        // Ring surface
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(Cx, Cy), OuterR, OuterR),
            new EllipseGeometry(new Point(Cx, Cy), InnerR - 6, InnerR - 6));
        dc.DrawGeometry(B(surface), new Pen(B(stroke), 1), ring);

        int n = _items.Count;
        if (n > 0)
        {
            double step = 360.0 / n;
            dc.PushTransform(new RotateTransform(_spin, Cx, Cy));
            for (int i = 0; i < n; i++)
            {
                double mid = i * step;
                double h = _hover[i];
                double pop = 5 * h;
                if (h > 0.001)
                {
                    var seg = Sector(mid - step / 2, mid + step / 2, InnerR + 2, OuterR - 4 + pop);
                    dc.DrawGeometry(B(A(accent, 0.95 * h)), null, seg);
                }
                else if (n > 1)
                {
                    // Hairline divider between slices
                    double a = (mid - step / 2 - 90) * Math.PI / 180;
                    dc.DrawLine(new Pen(B(divider), 1),
                        new Point(Cx + Math.Cos(a) * (InnerR + 8), Cy + Math.Sin(a) * (InnerR + 8)),
                        new Point(Cx + Math.Cos(a) * (OuterR - 10), Cy + Math.Sin(a) * (OuterR - 10)));
                }

                double rad = (mid - 90) * Math.PI / 180;
                double r = (InnerR + OuterR) / 2 + pop * 0.5;
                var center = new Point(Cx + Math.Cos(rad) * r, Cy + Math.Sin(rad) * r);
                var fg = Mix(text, onAccent, h);

                var icon = new FormattedText(_items[i].Glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, IconFace, 20, B(A(fg, 0.92)), dpi);
                var label = new FormattedText(_items[i].Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, n > 8 ? 12 : 13, B(fg), dpi)
                {
                    MaxTextWidth = Math.Max(40, 2 * Math.PI * r / n - 10),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                };
                double total = icon.Height + 2 + label.Height;
                dc.DrawText(icon, new Point(center.X - icon.Width / 2, center.Y - total / 2));
                dc.DrawText(label, new Point(center.X - label.MaxTextWidth / 2, center.Y - total / 2 + icon.Height + 2));
            }
            dc.Pop();
        }

        // Hub
        var hubColor = Mix(hub, dark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(228, 228, 228), _centerHover);
        dc.DrawEllipse(B(hubColor), new Pen(B(stroke), 1), new Point(Cx, Cy), InnerR - 6, InnerR - 6);

        if (_centerHover > 0.5)
        {
            var x = new FormattedText("", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, IconFace, 22, B(muted), dpi);
            dc.DrawText(x, new Point(Cx - x.Width / 2, Cy - x.Height / 2 - 8));
            var cancel = new FormattedText("Cancel", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BodyFace, 12, B(muted), dpi);
            dc.DrawText(cancel, new Point(Cx - cancel.Width / 2, Cy + 10));
        }
        else
        {
            // Calm, consistent hub: file-type glyph in the accent color, the format, and the current wheel.
            var glyph = new FormattedText(HubGlyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, IconFace, 26, B(accent), dpi);
            var fmt = new FormattedText(HubLabel, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 12.5, B(text), dpi)
            {
                MaxTextWidth = (InnerR - 14) * 2, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            };
            var modeText = new FormattedText(ModeName(Mode), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BodyFace, 11, B(muted), dpi);
            double total = glyph.Height + fmt.Height + modeText.Height;
            double y0 = Cy - total / 2;
            dc.DrawText(glyph, new Point(Cx - glyph.Width / 2, y0));
            dc.DrawText(fmt, new Point(Cx - fmt.MaxTextWidth / 2, y0 + glyph.Height));
            dc.DrawText(modeText, new Point(Cx - modeText.Width / 2, y0 + glyph.Height + fmt.Height));
        }

        if (n == 0 && EmptyText != null)
        {
            var empty = new FormattedText(EmptyText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BodyFace, 13, B(muted), dpi)
            {
                MaxTextWidth = OuterR - InnerR - 10,
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(empty, new Point(Cx - empty.MaxTextWidth / 2, Cy - OuterR + (OuterR - InnerR) / 2 - empty.Height / 2));
        }

        dc.Pop(); // scale
        DrawFooter(dc, dpi, dark, text, muted, surface, stroke, accent, onAccent);
        dc.Pop(); // opacity
    }

    void DrawFooter(DrawingContext dc, double dpi, bool dark, Color text, Color muted, Color surface, Color stroke, Color accent, Color onAccent)
    {
        // File name pill
        double y = Cy + OuterR + 18;
        var name = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 12.5, B(text), dpi)
        {
            MaxTextWidth = 300, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        FormattedText? sub = SubCaption == null ? null
            : new FormattedText("  ·  " + SubCaption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BodyFace, 12.5, B(muted), dpi);
        double w = name.WidthIncludingTrailingWhitespace + (sub?.WidthIncludingTrailingWhitespace ?? 0) + 28;
        var pill = new Rect(Cx - w / 2, y, w, 28);
        dc.DrawRoundedRectangle(B(surface), new Pen(B(stroke), 1), pill, 14, 14);
        dc.DrawText(name, new Point(pill.X + 14, y + 14 - name.Height / 2));
        if (sub != null) dc.DrawText(sub, new Point(pill.X + 14 + name.WidthIncludingTrailingWhitespace, y + 14 - sub.Height / 2));

        // Mode switcher: shows which key opens which wheel.
        var s = Core.Settings.Current;
        var modes = new[] { WheelMode.Convert, WheelMode.Tools, WheelMode.Actions };
        double chipY = y + 36;
        var chips = new List<(FormattedText key, FormattedText label, bool active, double width)>();
        double totalW = 0;
        foreach (var m in modes)
        {
            var k = s.KeyFor(m);
            if (k == TriggerKey.None) continue;
            bool active = m == Mode;
            var fg = active ? onAccent : muted;
            var kt = new FormattedText(KeyName(k), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 10.5, B(A(fg, active ? 0.85 : 0.9)), dpi);
            var lt = new FormattedText(ModeName(m), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11.5, B(fg), dpi);
            double cw = kt.Width + lt.Width + 26;
            chips.Add((kt, lt, active, cw));
            totalW += cw + 6;
        }
        double x = Cx - (totalW - 6) / 2;
        foreach (var (kt, lt, active, cw) in chips)
        {
            var r = new Rect(x, chipY, cw, 24);
            dc.DrawRoundedRectangle(B(active ? accent : surface), active ? null : new Pen(B(stroke), 1), r, 12, 12);
            dc.DrawText(kt, new Point(x + 10, chipY + 12 - kt.Height / 2));
            dc.DrawText(lt, new Point(x + 16 + kt.Width, chipY + 12 - lt.Height / 2));
            x += cw + 6;
        }
    }

    static string KeyName(TriggerKey k) => k switch { TriggerKey.Shift => "⇧", TriggerKey.Ctrl => "Ctrl", TriggerKey.Alt => "Alt", _ => "" };

    public static string ModeName(WheelMode m) => m switch { WheelMode.Convert => "Convert", WheelMode.Tools => "Tools", _ => "Actions" };

    static Geometry Sector(double fromDeg, double toDeg, double r0, double r1)
    {
        // Constant pixel gap between slices on both radii.
        double gap0 = GapPx / r0 * 180 / Math.PI, gap1 = GapPx / r1 * 180 / Math.PI;
        if (toDeg - fromDeg >= 359.9) { gap0 = gap1 = 0; toDeg = fromDeg + 359.99; }
        Point P(double deg, double r)
        {
            double a = (deg - 90) * Math.PI / 180;
            return new Point(Cx + Math.Cos(a) * r, Cy + Math.Sin(a) * r);
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool large = toDeg - fromDeg > 180;
            ctx.BeginFigure(P(fromDeg + gap1, r1), true, true);
            ctx.ArcTo(P(toDeg - gap1, r1), new Size(r1, r1), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(P(toDeg - gap0, r0), true, true);
            ctx.ArcTo(P(fromDeg + gap0, r0), new Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}
