using System;
using System.Collections.Generic;
using System.IO;
using OpenCvSharp;

namespace Stupipandi.Game;

/// <summary>
/// Representa un barril que cae desde la parte superior de la pantalla.
/// </summary>
public sealed class FallingBarrel
{
    public float X { get; set; }
    public float Y { get; set; }
    public float SpeedY { get; set; }
    public float Radius { get; set; }
    public bool IsDestroyed { get; set; }
    public float ExplosionProgress { get; set; } // 0.0 a 1.0 tras impacto
    public int PointsAwarded { get; set; }

    public Rect BoundingRect => new((int)(X - Radius), (int)(Y - Radius), (int)(Radius * 2), (int)(Radius * 2));
}

/// <summary>
/// Partícula de impacto tras destruir un barril.
/// </summary>
public sealed class SparkParticle
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Vx { get; set; }
    public float Vy { get; set; }
    public float Life { get; set; } // 1.0 a 0.0
    public Scalar Color { get; set; }
}

/// <summary>
/// Motor del minijuego interactivo "High Noon" con física de barriles cayendo,
/// detección de colisión con la punta del dedo índice (Finger-Gun), sistema de puntuación,
/// combos y partículas de impacto.
/// </summary>
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

        // Si falló la carga o no tiene canal alfa, generar proceduralmente
        if (_barrelSprite is null || _barrelSprite.Empty() || _barrelSprite.Channels() != 4)
        {
            _barrelSprite?.Dispose();
            _barrelSprite = Infrastructure.MediaInitializer.GenerateProceduralBarrel(70, 85);
        }
    }

    /// <summary>
    /// Actualiza la simulación física del juego, evalúa colisiones con la punta del dedo y dibuja el HUD.
    /// </summary>
    /// <param name="frame">Lienzo de renderizado actual.</param>
    /// <param name="aimPoint">Coordenada 2D de la punta del dedo índice (crosshair), o null si no apunta.</param>
    public void UpdateAndRender(Mat frame, Point? aimPoint)
    {
        if (frame is null || frame.Empty()) return;

        int frameW = frame.Cols;
        int frameH = frame.Rows;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_lastUpdateTime == 0) _lastUpdateTime = now;
        float dt = Math.Clamp((now - _lastUpdateTime) / 1000.0f, 0.001f, 0.1f);
        _lastUpdateTime = now;

        // ── 1. Spawning de nuevos barriles (cada 1.2 a 1.8 segundos) ──────────
        if (now - _lastSpawnTime > _random.Next(1100, 1700))
        {
            _lastSpawnTime = now;
            float radius = _random.Next(30, 42);
            float x = _random.Next((int)(radius + 60), (int)(frameW - radius - 60));
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

        // ── 2. Detección de impacto con la punta del dedo (AimPoint) ──────────
        if (aimPoint.HasValue)
        {
            Point target = aimPoint.Value;

            foreach (var barrel in _barrels)
            {
                if (barrel.IsDestroyed) continue;

                double dx = target.X - barrel.X;
                double dy = target.Y - barrel.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                // Si la punta del dedo colisiona con el área del barril
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

        // ── 3. Actualización física de barriles ────────────────────────────────
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

                // Si cae fuera de la pantalla sin ser destruido
                if (b.Y > frameH + b.Radius)
                {
                    Streak = 0; // Se pierde la racha
                    _barrels.RemoveAt(i);
                    continue;
                }
            }

            // Renderizado del barril o explosión
            RenderBarrel(frame, b);
        }

        // ── 4. Actualización y renderizado de partículas ──────────────────────
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vy += 250f * dt; // Gravedad
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

        // ── 5. Retícula táctica de apuntado (Crosshair) ───────────────────────
        if (aimPoint.HasValue)
        {
            DrawCrosshair(frame, aimPoint.Value);
        }

        // ── 6. Banner y Marcador "HIGH NOON" ──────────────────────────────────
        RenderHighNoonBanner(frame, frameW, frameH);
    }

    private void RenderBarrel(Mat frame, FallingBarrel b)
    {
        if (b.IsDestroyed)
        {
            // Efecto de desintegración y explosión
            int expRadius = (int)(b.Radius * (1.0f + b.ExplosionProgress * 1.6f));
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), expRadius, new Scalar(0, 165, 255), 3, LineTypes.AntiAlias);
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)(expRadius * 0.6), new Scalar(0, 240, 255), -1, LineTypes.AntiAlias);

            // Texto flotante de puntos
            string pts = $"+{b.PointsAwarded}";
            int textY = (int)(b.Y - expRadius - 10);
            Cv2.PutText(frame, pts, new Point((int)b.X - 25, Math.Max(20, textY)), HersheyFonts.HersheyComplex, 0.75, new Scalar(0, 255, 255), 2);
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
            // Fallback vectorial
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)b.Radius, new Scalar(25, 75, 140), -1, LineTypes.AntiAlias);
            Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)b.Radius, new Scalar(15, 45, 95), 3, LineTypes.AntiAlias);
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

    private void RenderHighNoonBanner(Mat frame, int frameW, int frameH)
    {
        // 1. Título "HIGH NOON" grande en amarillo con borde negro
        string title = "HIGH NOON";
        var tSize = Cv2.GetTextSize(title, HersheyFonts.HersheyComplex, 1.4, 4, out int baseline);
        int tx = (frameW - tSize.Width) / 2;
        int ty = 85;

        // Borde negro grueso
        for (int ox = -3; ox <= 3; ox++)
        {
            for (int oy = -3; oy <= 3; oy++)
            {
                if (ox == 0 && oy == 0) continue;
                Cv2.PutText(frame, title, new Point(tx + ox, ty + oy), HersheyFonts.HersheyComplex, 1.4, Scalar.Black, 5, LineTypes.AntiAlias);
            }
        }
        // Relleno amarillo oro brillante
        Cv2.PutText(frame, title, new Point(tx, ty), HersheyFonts.HersheyComplex, 1.4, new Scalar(0, 225, 255), 4, LineTypes.AntiAlias);

        // 2. Marcador de puntuación lateral
        var scoreBox = new Rect(frameW - 220, 75, 205, 75);
        Cv2.Rectangle(frame, scoreBox, new Scalar(20, 20, 20), -1);
        Cv2.Rectangle(frame, scoreBox, new Scalar(0, 215, 255), 2);

        Cv2.PutText(frame, $"SCORE: {Score}", new Point(scoreBox.X + 12, scoreBox.Y + 28), HersheyFonts.HersheySimplex, 0.70, new Scalar(255, 255, 255), 2);
        Cv2.PutText(frame, $"RACHA: x{Streak}  |  HIT: {BarrelsDestroyed}", new Point(scoreBox.X + 12, scoreBox.Y + 58), HersheyFonts.HersheySimplex, 0.50, new Scalar(0, 225, 255), 1);
    }

    private static void OverlayBgraSafe(Mat bgMat, Mat overlayBgra, Rect destRect)
    {
        int x = Math.Max(0, destRect.X);
        int y = Math.Max(0, destRect.Y);
        int w = Math.Min(destRect.Width, bgMat.Cols - x);
        int h = Math.Min(destRect.Height, bgMat.Rows - y);

        if (w <= 0 || h <= 0) return;

        using var resized = new Mat();
        Cv2.Resize(overlayBgra, resized, new Size(destRect.Width, destRect.Height), 0, 0, InterpolationFlags.Area);

        int ox = x - destRect.X;
        int oy = y - destRect.Y;
        using var cropOverlay = resized[new Rect(ox, oy, w, h)];
        using var cropBg = bgMat[new Rect(x, y, w, h)];

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
