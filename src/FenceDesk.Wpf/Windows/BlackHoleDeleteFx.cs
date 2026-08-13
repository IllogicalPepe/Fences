using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MediaPen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace FenceDesk.Windows;

/// <summary>
/// Overlay black-hole that forms around a tile icon, then swallows it.
/// Drawn per-frame (accretion disk, photon ring, event horizon, sparks).
/// </summary>
internal static class BlackHoleDeleteFx
{
    public static void Play(
        Canvas layer,
        FrameworkElement tile,
        ImageSource? icon,
        double iconPx,
        TimeSpan delay,
        Action onDone)
    {
        void Begin()
        {
            if (!layer.IsLoaded || !tile.IsLoaded)
            {
                onDone();
                return;
            }

            var img = FindIcon(tile);
            Point center;
            if (img is { ActualWidth: > 1, ActualHeight: > 1 })
                center = img.TranslatePoint(new Point(img.ActualWidth / 2, img.ActualHeight / 2), layer);
            else
                center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2), layer);

            var size = Math.Clamp(iconPx * 3.5 + 36, 148, 228);
            var burst = new BlackHoleBurst(icon, iconPx)
            {
                Width = size,
                Height = size,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(burst, center.X - size / 2);
            Canvas.SetTop(burst, center.Y - size / 2);

            tile.Opacity = 0;
            tile.IsHitTestVisible = false;
            layer.Children.Add(burst);
            burst.Completed += () =>
            {
                try { layer.Children.Remove(burst); } catch { /* closing */ }
                onDone();
            };
            burst.Start();
        }

        if (delay <= TimeSpan.Zero)
        {
            Begin();
            return;
        }

        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Begin();
        };
        timer.Start();
    }

    private static Image? FindIcon(FrameworkElement tile)
    {
        if (tile is Border { Child: StackPanel sp } &&
            sp.Children.Count > 0 &&
            sp.Children[0] is Image img)
            return img;
        return null;
    }
}

internal sealed class BlackHoleBurst : FrameworkElement
{
    private const double Duration = 0.92;

    private readonly ImageSource? _icon;
    private readonly double _iconPx;
    private readonly Stopwatch _clock = new();
    private readonly Spark[] _sparks;
    private readonly Star[] _stars;

    private EventHandler? _frame;
    private bool _finished;
    private double _t;

    public event Action? Completed;

    public BlackHoleBurst(ImageSource? icon, double iconPx)
    {
        _icon = icon;
        _iconPx = Math.Clamp(iconPx, 16, 128);
        _sparks = SeedSparks();
        _stars = SeedStars();
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
    }

    public void Start()
    {
        _clock.Restart();
        _frame = (_, _) =>
        {
            if (_finished) return;
            _t = Math.Clamp(_clock.Elapsed.TotalSeconds / Duration, 0, 1);
            InvalidateVisual();
            if (_t < 1) return;
            Finish();
        };
        CompositionTarget.Rendering += _frame;
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_frame is not null)
            CompositionTarget.Rendering -= _frame;
        Dispatcher.BeginInvoke(() => Completed?.Invoke(), DispatcherPriority.Background);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;

        var cx = w / 2;
        var cy = h / 2;
        var t = _t;

        var form = Smooth(0, 0.16, t);
        var suck = Smooth(0.14, 0.68, t);
        var collapse = Smooth(0.70, 1.0, t);
        var pulse = 1 + 0.035 * Math.Sin(t * Math.PI * 11);
        var holeScale = form * (1 - collapse) * pulse;
        var holeR = Math.Min(w, h) * 0.13 * holeScale;

        DrawGlow(dc, cx, cy, w, form, collapse);
        DrawJets(dc, cx, cy, w, h, form, collapse);
        DrawStars(dc, cx, cy, form, collapse);

        DrawDisk(dc, cx, cy, w, t, form, collapse, front: false);
        DrawHorizon(dc, cx, cy, holeR, form, collapse);
        DrawIcon(dc, cx, cy, t, suck, form);
        DrawDisk(dc, cx, cy, w, t, form, collapse, front: true);
        DrawFlash(dc, cx, cy, w, collapse);
    }

    private void DrawGlow(DrawingContext dc, double cx, double cy, double w, double form, double collapse)
    {
        var strength = form * (1 - collapse * 0.85);
        if (strength < 0.01) return;

        var r = w * 0.48;
        dc.PushOpacity(0.72 * strength);
        dc.DrawEllipse(Palette.OuterGlow, null, new Point(cx, cy), r, r * 0.78);
        dc.Pop();

        dc.PushOpacity(0.55 * strength);
        dc.DrawEllipse(Palette.MidGlow, null, new Point(cx, cy), r * 0.62, r * 0.50);
        dc.Pop();
    }

    private void DrawJets(DrawingContext dc, double cx, double cy, double w, double h, double form, double collapse)
    {
        var a = form * (1 - collapse) * 0.28;
        if (a < 0.01) return;
        dc.PushOpacity(a);
        var jetH = h * 0.46;
        dc.DrawEllipse(Palette.Jet, null, new Point(cx, cy - jetH * 0.42), w * 0.045, jetH * 0.38);
        dc.DrawEllipse(Palette.Jet, null, new Point(cx, cy + jetH * 0.42), w * 0.045, jetH * 0.38);
        dc.Pop();
    }

    private void DrawStars(DrawingContext dc, double cx, double cy, double form, double collapse)
    {
        if (form < 0.05) return;
        var pull = 1 - 0.55 * form * (1 - collapse * 0.4);
        dc.PushOpacity(0.55 * form * (1 - collapse));
        foreach (var s in _stars)
        {
            var x = cx + s.Dx * pull;
            var y = cy + s.Dy * pull;
            dc.DrawEllipse(s.Fill, null, new Point(x, y), s.Size, s.Size);
        }
        dc.Pop();
    }

    private void DrawHorizon(DrawingContext dc, double cx, double cy, double holeR, double form, double collapse)
    {
        if (holeR < 0.4) return;

        var ringR = holeR * 1.55;
        dc.DrawEllipse(Palette.PhotonFill, null, new Point(cx, cy), ringR, ringR);
        dc.DrawEllipse(Palette.Void, null, new Point(cx, cy), holeR, holeR);

        if (form < 0.2 || collapse > 0.7) return;
        dc.PushOpacity(0.85 * form * (1 - collapse));
        dc.DrawEllipse(null, Palette.PhotonPen, new Point(cx, cy), holeR * 1.22, holeR * 1.22);
        dc.Pop();
    }

    private void DrawDisk(DrawingContext dc, double cx, double cy, double w, double t, double form, double collapse, bool front)
    {
        if (form < 0.02) return;

        var flatten = 0.40;
        var maxR = w * 0.46;
        var minR = w * 0.11;
        var spin = t * Math.PI * 7.2;
        var tighten = 1 - 0.38 * Smooth(0.45, 0.85, t);
        var fade = form * (1 - collapse);

        foreach (var s in _sparks)
        {
            var ang = s.Angle + spin * s.Omega;
            var behind = Math.Sin(ang) < 0;
            if (behind == front) continue;

            var life = Math.Max(0, 1 - collapse * 1.4);
            var r = (minR + s.Orbit * (maxR - minR) * tighten) * life;
            if (r < 1.2) continue;

            var x = cx + Math.Cos(ang) * r;
            var y = cy + Math.Sin(ang) * r * flatten;

            // Doppler: approaching side (right) runs hotter / brighter
            var doppler = 0.42 + 0.58 * (0.5 + 0.5 * Math.Cos(ang));
            var trailA = ang - 0.18 * s.Omega;
            var trailR = r + 2.4;
            var x0 = cx + Math.Cos(trailA) * trailR;
            var y0 = cy + Math.Sin(trailA) * trailR * flatten;

            dc.PushOpacity(fade * s.Alpha * doppler);
            dc.DrawLine(s.Streak, new Point(x0, y0), new Point(x, y));
            dc.DrawEllipse(s.Fill, null, new Point(x, y), s.Size, s.Size * 0.72);
            dc.Pop();
        }
    }

    private void DrawIcon(DrawingContext dc, double cx, double cy, double t, double suck, double form)
    {
        if (_icon is null || suck >= 0.995) return;

        var rumble = (1 - Smooth(0, 0.20, t)) * 2.4;
        var ox = Math.Cos(t * 38) * rumble + Math.Cos(t * 14) * (1 - suck) * 3.2;
        var oy = Math.Sin(t * 33) * rumble * 0.7 + Math.Sin(t * 14) * (1 - suck) * 2.0;

        var scale = 1.0 - EaseInCubic(suck);
        if (scale < 0.02) return;

        var size = _iconPx * scale;
        var squash = 1 + suck * 0.55;
        var spin = suck * 520;
        var opacity = (0.35 + 0.65 * form) * (1 - EaseInCubic(suck));

        dc.PushOpacity(opacity);
        dc.PushTransform(new RotateTransform(spin, cx + ox, cy + oy));
        dc.PushTransform(new ScaleTransform(1 / squash, squash, cx + ox, cy + oy));
        dc.DrawImage(_icon, new Rect(cx + ox - size / 2, cy + oy - size / 2, size, size));
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void DrawFlash(DrawingContext dc, double cx, double cy, double w, double collapse)
    {
        if (collapse is < 0.02 or > 0.92) return;
        var peak = Math.Sin(collapse * Math.PI);
        var r = w * (0.06 + 0.22 * collapse);
        dc.PushOpacity(0.85 * peak);
        dc.DrawEllipse(Palette.Flash, null, new Point(cx, cy), r, r);
        dc.Pop();
    }

    private static Spark[] SeedSparks()
    {
        var rng = new Random(0xB10C);
        var n = 72;
        var list = new Spark[n];
        for (var i = 0; i < n; i++)
        {
            var hot = rng.NextDouble() > 0.28;
            var cool = !hot && rng.NextDouble() > 0.45;
            var color = hot
                ? Color.FromRgb(
                    (byte)(230 + rng.Next(25)),
                    (byte)(90 + rng.Next(120)),
                    (byte)(20 + rng.Next(70)))
                : cool
                    ? Color.FromRgb(
                        (byte)(90 + rng.Next(70)),
                        (byte)(140 + rng.Next(70)),
                        (byte)(210 + rng.Next(45)))
                    : Color.FromRgb(255, (byte)(210 + rng.Next(40)), (byte)(170 + rng.Next(50)));

            var fill = new SolidColorBrush(color);
            fill.Freeze();
            var streak = new MediaPen(new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B)), 1.15)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            streak.Freeze();

            list[i] = new Spark
            {
                Angle = rng.NextDouble() * Math.PI * 2,
                Orbit = 0.12 + rng.NextDouble() * 0.88,
                Omega = 0.55 + rng.NextDouble() * 1.15,
                Size = 0.7 + rng.NextDouble() * 2.1,
                Alpha = 0.35 + rng.NextDouble() * 0.65,
                Fill = fill,
                Streak = streak
            };
        }
        return list;
    }

    private static Star[] SeedStars()
    {
        var rng = new Random(0x5A17);
        var n = 16;
        var list = new Star[n];
        for (var i = 0; i < n; i++)
        {
            var ang = rng.NextDouble() * Math.PI * 2;
            var rad = 28 + rng.NextDouble() * 62;
            var fill = new SolidColorBrush(Color.FromArgb(
                (byte)(90 + rng.Next(120)), 220, 230, 255));
            fill.Freeze();
            list[i] = new Star
            {
                Dx = Math.Cos(ang) * rad,
                Dy = Math.Sin(ang) * rad * 0.72,
                Size = 0.5 + rng.NextDouble() * 1.1,
                Fill = fill
            };
        }
        return list;
    }

    private static double Smooth(double a, double b, double t)
    {
        if (t <= a) return 0;
        if (t >= b) return 1;
        var x = (t - a) / (b - a);
        return x * x * (3 - 2 * x);
    }

    private static double EaseInCubic(double x) => x * x * x;

    private struct Spark
    {
        public double Angle, Orbit, Omega, Size, Alpha;
        public SolidColorBrush Fill;
        public MediaPen Streak;
    }

    private struct Star
    {
        public double Dx, Dy, Size;
        public SolidColorBrush Fill;
    }

    private static class Palette
    {
        public static readonly RadialGradientBrush OuterGlow = FreezeRadial(
            Color.FromArgb(0, 40, 10, 50),
            Color.FromArgb(90, 90, 30, 140),
            Color.FromArgb(0, 0, 0, 0));

        public static readonly RadialGradientBrush MidGlow = FreezeRadial(
            Color.FromArgb(160, 255, 120, 30),
            Color.FromArgb(50, 180, 60, 10),
            Color.FromArgb(0, 0, 0, 0));

        public static readonly RadialGradientBrush PhotonFill = FreezePhoton();

        public static readonly RadialGradientBrush Flash = FreezeRadial(
            Color.FromArgb(255, 255, 245, 220),
            Color.FromArgb(160, 255, 170, 70),
            Color.FromArgb(0, 255, 120, 20));

        public static readonly RadialGradientBrush Jet = FreezeRadial(
            Color.FromArgb(140, 160, 200, 255),
            Color.FromArgb(40, 80, 130, 220),
            Color.FromArgb(0, 20, 40, 80));

        public static readonly SolidColorBrush Void = FreezeSolid(Colors.Black);

        public static readonly MediaPen PhotonPen = FreezePen(
            Color.FromArgb(220, 255, 210, 140), 1.6);

        private static RadialGradientBrush FreezeRadial(Color inner, Color mid, Color outer)
        {
            var b = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(inner, 0),
                    new GradientStop(mid, 0.45),
                    new GradientStop(outer, 1)
                }
            };
            b.Freeze();
            return b;
        }

        private static RadialGradientBrush FreezePhoton()
        {
            var b = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.58),
                    new GradientStop(Color.FromArgb(255, 255, 190, 90), 0.74),
                    new GradientStop(Color.FromArgb(255, 255, 250, 230), 0.80),
                    new GradientStop(Color.FromArgb(200, 255, 140, 40), 0.88),
                    new GradientStop(Color.FromArgb(0, 80, 20, 0), 1)
                }
            };
            b.Freeze();
            return b;
        }

        private static SolidColorBrush FreezeSolid(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static MediaPen FreezePen(Color c, double thickness)
        {
            var p = new MediaPen(new SolidColorBrush(c), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            p.Freeze();
            return p;
        }
    }
}
