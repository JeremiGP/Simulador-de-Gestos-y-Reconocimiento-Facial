using System;
using System.Collections.Generic;
using System.IO;
using OpenCvSharp;

namespace Stupipandi.Game;

public sealed class FallingBarrel
{
    public float X { get; set; }
    public float Y { get; set; }
    public float SpeedY { get; set; }
    public float Radius { get; set; }
    public bool IsDestroyed { get; set; }
    public float ExplosionProgress { get; set; }
    public int PointsAwarded { get; set; }

    public Rect BoundingRect => new((int)(X - Radius), (int)(Y - Radius), (int)(Radius * 2), (int)(Radius * 2));
}

public sealed class SparkParticle
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Vx { get; set; }
    public float Vy { get; set; }
    public float Life { get; set; }
    public Scalar Color { get; set; }
}

public sealed class HighNoonGameEngine : IDisposable
{
    private readonly Random _random = new();
    private readonly List<FallingBarrel> _barrels = new();
    private readonly List<SparkParticle> _particles = new();

    private Mat? _barrelSprite;
    private long _lastSpawnTime;
    private long _lastUpdateTime;

    public int Score { get; private set; }
    public int Streak { get; private set; }
    public int BarrelsDestroyed { get; private set; }
    public bool IsActive { get; set; }

    private static readonly string BarrelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Media", "barrel.png");

    public HighNoonGameEngine()
    {
        LoadBarrelSprite();
    }

    private void LoadBarrelSprite()
    {
        try
        {
            if (File.Exists(BarrelPath))
            {
                _barrelSprite = Cv2.ImRead(BarrelPath, ImreadModes.Unchanged);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HighNoonGame] Advertencia al cargar sprite de barril: {ex.Message}");
        }

        if (_barrelSprite is null || _barrelSprite.Empty() || _barrelSprite.Channels() != 4)
        {
            _barrelSprite?.Dispose();
            _barrelSprite = Infrastructure.MediaInitializer.GenerateProceduralBarrel(70, 85);
        }
    }

    public void UpdateAndRender(Mat frame, Point? aimPoint)
    {
        if (frame is null || frame.Empty()) return;

        int frameW = frame.Cols;
        int frameH = frame.Rows;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_lastUpdateTime == 0) _lastUpdateTime = now;
        float dt = Math.Clamp((now - _lastUpdateTime) / 1000.0f, 0.001f, 0.1f);
        _lastUpdateTime = now;

        // 1. Spawning de nuevos barriles
        if (now - _lastSpawnTime > _random.Next(1100, 1700))
        {
            _lastSpawnTime = now;
            float radius = _random.Next(30, 42);
            float x = _random.Next((int)(radius + 60), Math.Max((int)(radius + 61), (int)(frameW - radius - 60)));
            float speed = _random.Next(140, 260) + (Math.Min(Score, 500) * 0.18f);

            _barrels.Add(new FallingBarrel
            {
                X = x,
                Y = -radius,
                SpeedY = speed,
                Radius = radius,
                IsDestroyed = false,
                ExplosionProgress = 0f,
                PointsAwarded = 100 + (int)(speed * 0.4f)
            });
        }

        // 2. Detección de impacto con la punta del dedo
        if (aimPoint.HasValue)
        {
            Point target = aimPoint.Value;

            foreach (var barrel in _barrels)
            {
                if (barrel.IsDestroyed) continue;

                double dx = target.X - barrel.X;
                double dy = target.Y - barrel.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist <= barrel.Radius + 16)
                {
                    barrel.IsDestroyed = true;
                    Score += barrel.PointsAwarded * Math.Max(1, Streak / 3 + 1);
                    Streak++;
                    BarrelsDestroyed++;

                    SpawnExplosionParticles(barrel.X, barrel.Y);
                }
            }
        }

        // 3. Actualización física de barriles
        for (int i = _barrels.Count - 1; i >= 0; i--)
        {
            var b = _barrels[i];

            if (b.IsDestroyed)
            {
                b.ExplosionProgress += dt * 3.5f;
                if (b.ExplosionProgress >= 1.0f)
                {
                    _barrels.RemoveAt(i);
                    continue;
                }
            }
            else
            {
                b.Y += b.SpeedY * dt;

                if (b.Y > frameH + b.Radius)
                {
                    Streak = 0;
                    _barrels.RemoveAt(i);
                    continue;
                }
            }

            RenderBarrel(frame, b);
        }

        // 4. Partículas
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vy += 250f * dt;
            p.Life -= dt * 2.2f;

            if (p.Life <= 0)
            {
                _particles.RemoveAt(i);
                continue;
            }

            if (p.X >= 0 && p.X < frameW && p.Y >= 0 && p.Y < frameH)
            {
                int r = Math.Max(1, (int)(p.Life * 5));
                Cv2.Circle(frame, new Point((int)p.X, (int)p.Y), r, p.Color, -1);
            }
        }

        // 5. Mira
        if (aimPoint.HasValue)
        {
            DrawCrosshair(frame, aimPoint.Value);
        }

        // 6. Banner y Marcador
        RenderHighNoonBanner(frame, frameW);
    }

    private void RenderBarrel(Mat frame, FallingBarrel b)
    {
        if (b.IsDestroyed)
        {
            int expRadius = (int)(b.Radius * (1.0f + b.ExplosionProgress * 1.6f));
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), expRadius, new Scalar(0, 165, 255), 3, LineTypes.AntiAlias);
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)(expRadius * 0.6), new Scalar(0, 240, 255), -1, LineTypes.AntiAlias);

            string pts = $"+{b.PointsAwarded}";
            int textY = (int)(b.Y - expRadius - 10);
            Cv2.PutText(frame, pts, new Point(Math.Max(10, (int)b.X - 25), Math.Max(25, textY)), HersheyFonts.HersheyComplex, 0.75, new Scalar(0, 255, 255), 2);
            return;
        }

        int size = (int)(b.Radius * 2);
        Rect dest = new((int)(b.X - b.Radius), (int)(b.Y - b.Radius), size, size);

        if (_barrelSprite is not null && !_barrelSprite.Empty())
        {
            OverlayBgraSafe(frame, _barrelSprite, dest);
        }
        else
        {
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)b.Radius, new Scalar(25, 75, 140), -1, LineTypes.AntiAlias);
        }
    }

    private void SpawnExplosionParticles(float x, float y)
    {
        for (int i = 0; i < 22; i++)
        {
            double angle = _random.NextDouble() * Math.PI * 2.0;
            float speed = _random.Next(80, 260);

            _particles.Add(new SparkParticle
            {
                X = x,
                Y = y,
                Vx = (float)(Math.Cos(angle) * speed),
                Vy = (float)(Math.Sin(angle) * speed),
                Life = 1.0f,
                Color = _random.Next(0, 2) == 0 ? new Scalar(0, 215, 255) : new Scalar(0, 120, 255)
            });
        }
    }

    private static void DrawCrosshair(Mat frame, Point target)
    {
        int r = 18;
        Cv2.Circle(frame, target, r, new Scalar(0, 255, 255), 2, LineTypes.AntiAlias);
        Cv2.Circle(frame, target, 3, new Scalar(0, 0, 255), -1, LineTypes.AntiAlias);
        Cv2.Line(frame, new Point(target.X - r - 6, target.Y), new Point(target.X + r + 6, target.Y), new Scalar(0, 255, 255), 2);
        Cv2.Line(frame, new Point(target.X, target.Y - r - 6), new Point(target.X, target.Y + r + 6), new Scalar(0, 255, 255), 2);
    }

    private void RenderHighNoonBanner(Mat frame, int frameW)
    {
        string title = "HIGH NOON";
        var tSize = Cv2.GetTextSize(title, HersheyFonts.HersheyComplex, 1.4, 4, out _);
        int tx = Math.Max(10, (frameW - tSize.Width) / 2);
        int ty = 85;

        for (int ox = -3; ox <= 3; ox++)
        {
            for (int oy = -3; oy <= 3; oy++)
            {
                if (ox == 0 && oy == 0) continue;
                Cv2.PutText(frame, title, new Point(tx + ox, ty + oy), HersheyFonts.HersheyComplex, 1.4, Scalar.Black, 5, LineTypes.AntiAlias);
            }
        }
        Cv2.PutText(frame, title, new Point(tx, ty), HersheyFonts.HersheyComplex, 1.4, new Scalar(0, 225, 255), 4, LineTypes.AntiAlias);

        var scoreBox = new Rect(Math.Max(0, frameW - 220), 75, Math.Min(205, frameW), 75);
        Cv2.Rectangle(frame, scoreBox, new Scalar(20, 20, 20), -1);
        Cv2.Rectangle(frame, scoreBox, new Scalar(0, 215, 255), 2);

        Cv2.PutText(frame, $"SCORE: {Score}", new Point(scoreBox.X + 12, scoreBox.Y + 28), HersheyFonts.HersheySimplex, 0.70, new Scalar(255, 255, 255), 2);
        Cv2.PutText(frame, $"RACHA: x{Streak}  |  HIT: {BarrelsDestroyed}", new Point(scoreBox.X + 12, scoreBox.Y + 58), HersheyFonts.HersheySimplex, 0.50, new Scalar(0, 225, 255), 1);
    }

    private static void OverlayBgraSafe(Mat bgMat, Mat overlayBgra, Rect destRect)
    {
        if (destRect.Width <= 0 || destRect.Height <= 0) return;

        // Intersección exacta dentro de los límites de bgMat
        int x0 = Math.Max(0, destRect.X);
        int y0 = Math.Max(0, destRect.Y);
        int x1 = Math.Min(bgMat.Cols, destRect.Right);
        int y1 = Math.Min(bgMat.Rows, destRect.Bottom);

        int w = x1 - x0;
        int h = y1 - y0;
        if (w <= 0 || h <= 0) return;

        using var resized = new Mat();
        Cv2.Resize(overlayBgra, resized, new Size(destRect.Width, destRect.Height), 0, 0, InterpolationFlags.Area);

        int ox = x0 - destRect.X;
        int oy = y0 - destRect.Y;

        if (ox < 0 || oy < 0 || ox + w > resized.Cols || oy + h > resized.Rows)
            return;

        using var cropOverlay = resized[new Rect(ox, oy, w, h)];
        using var cropBg = bgMat[new Rect(x0, y0, w, h)];

        Cv2.Split(cropOverlay, out Mat[] channels);
        using var b = channels[0];
        using var g = channels[1];
        using var r = channels[2];
        using var alpha = channels[3];

        using var overlayBgr = new Mat();
        Cv2.Merge(new Mat[] { b, g, r }, overlayBgr);

        using var alphaF = new Mat();
        using var invAlphaF = new Mat();
        alpha.ConvertTo(alphaF, MatType.CV_32FC1, 1.0 / 255.0);

        using var ones = new Mat(alphaF.Size(), MatType.CV_32FC1, Scalar.All(1.0));
        Cv2.Subtract(ones, alphaF, invAlphaF);

        using var alpha3 = new Mat();
        using var invAlpha3 = new Mat();
        Cv2.Merge(new Mat[] { alphaF, alphaF, alphaF }, alpha3);
        Cv2.Merge(new Mat[] { invAlphaF, invAlphaF, invAlphaF }, invAlpha3);

        using var bgF = new Mat();
        using var ovF = new Mat();
        cropBg.ConvertTo(bgF, MatType.CV_32FC3);
        overlayBgr.ConvertTo(ovF, MatType.CV_32FC3);

        using var blendedF = new Mat();
        Cv2.Multiply(ovF, alpha3, ovF);
        Cv2.Multiply(bgF, invAlpha3, bgF);
        Cv2.Add(ovF, bgF, blendedF);

        blendedF.ConvertTo(cropBg, MatType.CV_8UC3);

        foreach (var c in channels) c.Dispose();
    }

    public void Reset()
    {
        _barrels.Clear();
        _particles.Clear();
        Score = 0;
        Streak = 0;
        BarrelsDestroyed = 0;
        _lastSpawnTime = 0;
        _lastUpdateTime = 0;
    }

    public void Dispose()
    {
        _barrelSprite?.Dispose();
        _barrelSprite = null;
        _barrels.Clear();
        _particles.Clear();
    }
}