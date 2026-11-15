using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using Stupipandi.Game;
using Stupipandi.Gestures;
using Stupipandi.State;

namespace Stupipandi.Biometrics;

public sealed class OverlayRenderer : IDisposable
{
    private static readonly Scalar ColorRecognized = new(50, 220, 50);
    private static readonly Scalar ColorUnknown = new(40, 40, 230);
    private static readonly Scalar ColorWarning = new(0, 215, 255);
    private static readonly Scalar ColorEye = new(255, 210, 40);
    private static readonly Scalar ColorGesture = new(20, 160, 255);
    private static readonly Scalar ColorWhite = new(255, 255, 255);
    private static readonly Scalar ColorMuted = new(190, 190, 190);

    private static readonly string MediaFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Media");

    private readonly Dictionary<string, Mat> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Mat> _gifFrames = new();
    private int _gifFrameIndex = 0;
    private long _lastGifFrameTicks = 0;

    private VideoCapture? _auxVideoCap;
    private Mat? _auxVideoFrame;
    private long _auxVideoStartTicks = 0;
    private double _auxVideoFps = 25.0;

    private Rect? _smoothedHatRect;
    private Rect? _smoothedKippahRect;
    private const double HatAlpha = 0.28;

    private bool _disposed;

    public void DrawFace(
        Mat frame,
        Rect faceBounds,
        FaceRecognitionResult result,
        ExpressionResult? expression = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(result);
        if (frame.Empty() || faceBounds.Width <= 0 || faceBounds.Height <= 0) return;

        Scalar mainColor = result.IsRecognized
            ? ColorRecognized
            : (!result.ModelIsReady ? ColorWarning : ColorUnknown);

        DrawStylizedBox(frame, faceBounds, mainColor, 2);

        string labelText = result.Label;
        if (result.IsRecognized && result.ConfidencePercent > 0)
        {
            labelText += $" [{result.ConfidencePercent}%]";
        }
        else if (double.IsFinite(result.Distance))
        {
            labelText += $" (d={result.Distance:0.0})";
        }

        int textY = faceBounds.Y > 28 ? faceBounds.Y - 8 : faceBounds.Y + 24;
        DrawPillText(frame, labelText, new Point(faceBounds.X, textY), mainColor, 0.55);

        if (expression is not null && expression.Expression != FacialExpression.Neutral)
        {
            Scalar expColor = expression.Expression switch
            {
                FacialExpression.Smiling => ColorWarning,
                FacialExpression.Surprised => ColorEye,
                FacialExpression.EyesClosed => ColorMuted,
                _ => ColorWhite
            };

            int expY = faceBounds.Bottom + 20;
            if (expY < frame.Rows - 35)
            {
                string exprLabel = expression.IsRoaring ? "RUGIDO / BOCA ABIERTA" : expression.Label;
                DrawPillText(frame, exprLabel, new Point(faceBounds.X, expY), expColor, 0.50);
            }
        }

        if (expression?.EyeBounds is not null)
        {
            foreach (Rect eye in expression.EyeBounds)
            {
                Cv2.Rectangle(frame, eye, ColorEye, 1, LineTypes.AntiAlias);
            }
        }
    }

    public void DrawGesture(Mat frame, Rect handBounds, GestureType gesture)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty() || gesture == GestureType.None || handBounds.Width <= 0 || handBounds.Height <= 0) return;

        DrawStylizedBox(frame, handBounds, ColorGesture, 2);

        string gestureName = FormatGestureName(gesture);
        int textY = Math.Max(20, handBounds.Y - 8);
        DrawPillText(frame, $"Gesto: {gestureName}", new Point(handBounds.X, textY), ColorGesture, 0.58);
    }

    public void DrawCowboyHat(Mat frame, Rect faceBounds)
    {
        Mat? hat = GetOrLoadTexture("cowboy_hat.png");
        if (hat is null || hat.Empty()) return;

        int hatWidth = (int)(faceBounds.Width * 1.55);
        int hatHeight = (int)(hatWidth * ((double)hat.Height / Math.Max(1, hat.Width)));
        int hatX = faceBounds.X + (faceBounds.Width - hatWidth) / 2;
        int hatY = faceBounds.Y - (int)(hatHeight * 0.75);

        Rect targetRect = new(hatX, hatY, hatWidth, hatHeight);

        if (_smoothedHatRect.HasValue)
        {
            double sx = HatAlpha * targetRect.X + (1 - HatAlpha) * _smoothedHatRect.Value.X;
            double sy = HatAlpha * targetRect.Y + (1 - HatAlpha) * _smoothedHatRect.Value.Y;
            double sw = HatAlpha * targetRect.Width + (1 - HatAlpha) * _smoothedHatRect.Value.Width;
            double sh = HatAlpha * targetRect.Height + (1 - HatAlpha) * _smoothedHatRect.Value.Height;
            _smoothedHatRect = new Rect((int)sx, (int)sy, (int)sw, (int)sh);
        }
        else
        {
            _smoothedHatRect = targetRect;
        }

        DrawAlphaOverlay(frame, hat, _smoothedHatRect.Value);
    }

    public void DrawKippah(Mat frame, Rect faceBounds)
    {
        Mat? kippah = GetOrLoadTexture("kippah.png");
        if (kippah is null || kippah.Empty()) return;

        int kippahWidth = (int)(faceBounds.Width * 0.70);
        int kippahHeight = (int)(kippahWidth * ((double)kippah.Height / Math.Max(1, kippah.Width)));
        int kippahX = faceBounds.X + (faceBounds.Width - kippahWidth) / 2;
        int kippahY = faceBounds.Y - (int)(kippahHeight * 0.45);

        Rect targetRect = new(kippahX, kippahY, kippahWidth, kippahHeight);

        if (_smoothedKippahRect.HasValue)
        {
            double sx = HatAlpha * targetRect.X + (1 - HatAlpha) * _smoothedKippahRect.Value.X;
            double sy = HatAlpha * targetRect.Y + (1 - HatAlpha) * _smoothedKippahRect.Value.Y;
            double sw = HatAlpha * targetRect.Width + (1 - HatAlpha) * _smoothedKippahRect.Value.Width;
            double sh = HatAlpha * targetRect.Height + (1 - HatAlpha) * _smoothedKippahRect.Value.Height;
            _smoothedKippahRect = new Rect((int)sx, (int)sy, (int)sw, (int)sh);
        }
        else
        {
            _smoothedKippahRect = targetRect;
        }

        DrawAlphaOverlay(frame, kippah, _smoothedKippahRect.Value);
    }

    public void DrawMemeOverlay(Mat frame, string filename = "image_84e5ca.jpg")
    {
        Mat? img = GetOrLoadTexture(filename);
        if (img is null || img.Empty()) return;

        int targetW = Math.Max(40, Math.Min((int)(frame.Cols * 0.45), img.Cols));
        int targetH = Math.Max(40, (int)(targetW * ((double)img.Rows / Math.Max(1, img.Cols))));

        int x = Math.Max(0, (frame.Cols - targetW) / 2);
        int y = Math.Max(0, frame.Rows - targetH - 40);

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        if (target.Width <= 2 || target.Height <= 2) return;

        using var resized = new Mat();
        Cv2.Resize(img, resized, new Size(target.Width, target.Height));
        EnsureBgr3(resized);

        Cv2.Rectangle(frame, ClampRect(new Rect(target.X - 3, target.Y - 3, target.Width + 6, target.Height + 6), frame.Cols, frame.Rows), ColorWarning, 2);
        resized.CopyTo(frame[target]);
    }

    public void DrawAnimatedGif(Mat frame, string filename = "guy-funny.gif")
    {
        EnsureGifLoaded(filename);
        if (_gifFrames.Count == 0) return;

        long nowTicks = Stopwatch.GetTimestamp();
        if (nowTicks - _lastGifFrameTicks > Stopwatch.Frequency / 15)
        {
            _gifFrameIndex = (_gifFrameIndex + 1) % _gifFrames.Count;
            _lastGifFrameTicks = nowTicks;
        }

        Mat currentFrame = _gifFrames[_gifFrameIndex];
        if (currentFrame.Empty()) return;

        int targetW = Math.Max(40, (int)(frame.Cols * 0.30));
        int targetH = Math.Max(40, (int)(targetW * ((double)currentFrame.Rows / Math.Max(1, currentFrame.Cols))));

        int x = Math.Max(0, frame.Cols - targetW - 20);
        int y = 75;

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        if (target.Width <= 2 || target.Height <= 2) return;

        using var resized = new Mat();
        Cv2.Resize(currentFrame, resized, new Size(target.Width, target.Height));
        EnsureBgr3(resized);

        Cv2.Rectangle(frame, ClampRect(new Rect(target.X - 2, target.Y - 2, target.Width + 4, target.Height + 4), frame.Cols, frame.Rows), ColorWarning, 2);
        resized.CopyTo(frame[target]);
    }

    public void ResetAuxVideo()
    {
        _auxVideoStartTicks = 0;
        if (_auxVideoCap is not null && _auxVideoCap.IsOpened())
        {
            _auxVideoCap.Set(VideoCaptureProperties.PosFrames, 0);
        }
    }

    public void DrawPictureInPictureVideo(Mat frame, string videoFilename = "video_1.mp4")
    {
        EnsureAuxVideoOpen(videoFilename);
        if (_auxVideoCap is null || !_auxVideoCap.IsOpened()) return;

        long nowTicks = Stopwatch.GetTimestamp();
        if (_auxVideoStartTicks == 0)
        {
            _auxVideoStartTicks = nowTicks;
            _auxVideoCap.Set(VideoCaptureProperties.PosFrames, 0);
        }

        // Sincronizar el fotograma con el tiempo real transcurrido para ir a la par que su audio
        double elapsedSec = (double)(nowTicks - _auxVideoStartTicks) / Stopwatch.Frequency;
        int expectedFrame = (int)(elapsedSec * _auxVideoFps);
        int currentPos = (int)_auxVideoCap.Get(VideoCaptureProperties.PosFrames);

        if (expectedFrame > currentPos + 2)
        {
            _auxVideoCap.Set(VideoCaptureProperties.PosFrames, expectedFrame);
        }

        _auxVideoFrame ??= new Mat();
        if (expectedFrame >= currentPos || _auxVideoFrame.Empty())
        {
            if (!_auxVideoCap.Read(_auxVideoFrame) || _auxVideoFrame.Empty())
            {
                _auxVideoStartTicks = nowTicks;
                _auxVideoCap.Set(VideoCaptureProperties.PosFrames, 0);
                if (!_auxVideoCap.Read(_auxVideoFrame) || _auxVideoFrame.Empty())
                    return;
            }
        }

        int targetW = Math.Max(40, (int)(frame.Cols * 0.28));
        int targetH = Math.Max(40, (int)(targetW * ((double)_auxVideoFrame.Rows / Math.Max(1, _auxVideoFrame.Cols))));
        int x = 20;
        int y = 75;

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        if (target.Width <= 2 || target.Height <= 2) return;

        using var resized = new Mat();
        Cv2.Resize(_auxVideoFrame, resized, new Size(target.Width, target.Height));
        EnsureBgr3(resized);

        Cv2.Rectangle(frame, ClampRect(new Rect(target.X - 2, target.Y - 2, target.Width + 4, target.Height + 4), frame.Cols, frame.Rows), new Scalar(0, 0, 220), 2);
        resized.CopyTo(frame[target]);
    }

    public void DrawDragoniteOverlay(Mat frame, string filename = "dragonite_espanol.jpeg")
    {
        Mat? img = GetOrLoadTexture(filename);
        if (img is null || img.Empty()) return;

        int targetW = Math.Max(40, (int)(frame.Cols * 0.26));
        int targetH = Math.Max(40, (int)(targetW * ((double)img.Rows / Math.Max(1, img.Cols))));

        int x = Math.Max(0, frame.Cols - targetW - 20);
        int y = Math.Max(0, frame.Rows - targetH - 35);

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        if (target.Width <= 2 || target.Height <= 2) return;

        using var resized = new Mat();
        Cv2.Resize(img, resized, new Size(target.Width, target.Height));
        EnsureBgr3(resized);

        Cv2.Rectangle(frame, ClampRect(new Rect(target.X - 2, target.Y - 2, target.Width + 4, target.Height + 4), frame.Cols, frame.Rows), ColorWarning, 2);
        resized.CopyTo(frame[target]);
    }

    public void ReplaceBackground(Mat frame, Mat skinMask, Rect? faceBounds, string bgFilename = "jesus_bg.jpg")
    {
        Mat? bg = GetOrLoadTexture(bgFilename);
        if (bg is null || bg.Empty() || frame.Empty()) return;

        using var resizedBg = new Mat();
        Cv2.Resize(bg, resizedBg, new Size(frame.Cols, frame.Rows));
        EnsureBgr3(resizedBg);

        using var humanMask = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(25, 25));
        Cv2.Dilate(skinMask, humanMask, kernel, iterations: 2);

        if (faceBounds.HasValue)
        {
            var face = faceBounds.Value;
            Cv2.Circle(humanMask, new Point(face.X + face.Width / 2, face.Y + face.Height / 2), (int)(face.Width * 0.65), Scalar.White, -1);

            int torsoX = Math.Max(0, face.X - (int)(face.Width * 0.40));
            int torsoY = Math.Clamp(face.Bottom, 0, frame.Rows - 1);
            int torsoW = Math.Clamp((int)(face.Width * 1.80), 1, frame.Cols - torsoX);
            int torsoH = Math.Clamp((int)(face.Height * 1.60), 1, frame.Rows - torsoY);
            if (torsoW > 1 && torsoH > 1)
            {
                Cv2.Rectangle(humanMask, new Rect(torsoX, torsoY, torsoW, torsoH), Scalar.White, -1);
            }
        }

        using var bgMask = new Mat();
        Cv2.BitwiseNot(humanMask, bgMask);
        resizedBg.CopyTo(frame, bgMask);
    }

    public void DrawHighNoonGame(Mat frame, HighNoonGameEngine gameEngine, Point? indexFingerTip, Rect? faceBounds)
    {
        gameEngine.UpdateAndRender(frame, indexFingerTip);

        if (faceBounds.HasValue)
        {
            DrawCowboyHat(frame, faceBounds.Value);
        }
    }

    public void DrawHud(
        Mat frame,
        ApplicationMode mode,
        double fps,
        string? currentUser = null,
        int userConfidence = 0,
        string? lastGesture = null,
        string? colorSpaceName = "HSV",
        string? activePrompt = null,
        ActiveTrigger activeTrigger = ActiveTrigger.None)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty()) return;

        int frameW = frame.Cols;
        int frameH = frame.Rows;

        var topBar = new Rect(0, 0, frameW, Math.Min(64, frameH));
        Cv2.Rectangle(frame, topBar, new Scalar(20, 20, 20), -1);
        Cv2.Line(frame, new Point(0, 64), new Point(frameW, 64), new Scalar(60, 60, 60), 1);

        (string modeText, Scalar modeColor) = mode switch
        {
            ApplicationMode.AwaitingFace =>
                ("Esperando rostro en camara...", ColorMuted),
            ApplicationMode.GestureInteraction =>
                ($"ACCESO AUTORIZADO: {currentUser ?? "Usuario"} ({userConfidence}% conf.) - Interaccion Activa", ColorRecognized),
            ApplicationMode.AccessDenied =>
                ("ACCESO DENEGADO: Rostro no registrado en el sistema", ColorUnknown),
            ApplicationMode.TrainingRequired =>
                ("MODELO SIN ENTRENAR: Presione 'T' para enrolar su rostro", ColorWarning),
            ApplicationMode.Exiting =>
                ("Cerrando aplicacion...", ColorMuted),
            _ => (mode.ToString(), ColorMuted)
        };

        Cv2.Circle(frame, new Point(20, 22), 7, modeColor, -1);
        Cv2.PutText(frame, modeText, new Point(36, 27),
            HersheyFonts.HersheySimplex, 0.55, modeColor, 2, LineTypes.AntiAlias);

        string subInfo = $"FPS: {fps:0.0}   |   Piel: {colorSpaceName}";
        if (!string.IsNullOrWhiteSpace(lastGesture) && mode == ApplicationMode.GestureInteraction)
        {
            subInfo += $"   |   Gesto: {lastGesture}";
        }
        if (activeTrigger != ActiveTrigger.None)
        {
            subInfo += $"   |   [TRIGGER ACTIVO: {activeTrigger}]";
        }

        Cv2.PutText(frame, subInfo, new Point(14, 50),
            HersheyFonts.HersheySimplex, 0.44, ColorWhite, 1, LineTypes.AntiAlias);

        var botBar = new Rect(0, Math.Max(0, frameH - 26), frameW, Math.Min(26, frameH));
        Cv2.Rectangle(frame, botBar, new Scalar(16, 16, 16), -1);
        Cv2.Line(frame, new Point(0, frameH - 26), new Point(frameW, frameH - 26), new Scalar(50, 50, 50), 1);

        string shortcuts = "ESC/Q: Salir  |  T: Enrolar rostro  |  S: Guardar modelo  |  R: Reiniciar  |  C: Color Piel  |  1-6: Triggers";
        Cv2.PutText(frame, shortcuts, new Point(12, Math.Max(15, frameH - 8)),
            HersheyFonts.HersheySimplex, 0.40, ColorMuted, 1, LineTypes.AntiAlias);

        if (!string.IsNullOrWhiteSpace(activePrompt))
        {
            var pSize = Cv2.GetTextSize(activePrompt, HersheyFonts.HersheySimplex, 0.65, 2, out int pBaseline);
            var centerRect = ClampRect(new Rect(
                (frameW - pSize.Width) / 2 - 16,
                (frameH - pSize.Height) / 2 - 16,
                pSize.Width + 32,
                pSize.Height + pBaseline + 32), frameW, frameH);

            Cv2.Rectangle(frame, centerRect, new Scalar(12, 12, 70), -1);
            Cv2.Rectangle(frame, centerRect, ColorWarning, 2);
            Cv2.PutText(
                frame,
                activePrompt,
                new Point(centerRect.X + 16, centerRect.Y + pSize.Height + 12),
                HersheyFonts.HersheySimplex,
                0.65,
                ColorWhite,
                2,
                LineTypes.AntiAlias);
        }
    }

    private static void DrawAlphaOverlay(Mat frame, Mat overlayBgra, Rect destRect)
    {
        if (overlayBgra.Empty() || destRect.Width <= 0 || destRect.Height <= 0) return;

        int x0 = Math.Max(0, destRect.X);
        int y0 = Math.Max(0, destRect.Y);
        int x1 = Math.Min(frame.Cols, destRect.Right);
        int y1 = Math.Min(frame.Rows, destRect.Bottom);

        int renderW = x1 - x0;
        int renderH = y1 - y0;
        if (renderW <= 0 || renderH <= 0) return;

        using var resizedOverlay = new Mat();
        Cv2.Resize(overlayBgra, resizedOverlay, new Size(destRect.Width, destRect.Height));

        int srcX = x0 - destRect.X;
        int srcY = y0 - destRect.Y;
        if (srcX < 0 || srcY < 0 || srcX + renderW > resizedOverlay.Cols || srcY + renderH > resizedOverlay.Rows)
            return;

        using var srcSub = resizedOverlay[new Rect(srcX, srcY, renderW, renderH)];
        using var dstSub = frame[new Rect(x0, y0, renderW, renderH)];

        if (srcSub.Channels() == 4)
        {
            Cv2.Split(srcSub, out Mat[] ch);
            using var b = ch[0];
            using var g = ch[1];
            using var r = ch[2];
            using var alpha = ch[3];

            using var overlayBgr = new Mat();
            Cv2.Merge(new Mat[] { b, g, r }, overlayBgr);
            overlayBgr.CopyTo(dstSub, alpha);

            foreach (var c in ch) c.Dispose();
        }
        else
        {
            EnsureBgr3(srcSub);
            srcSub.CopyTo(dstSub);
        }
    }

    private static void EnsureBgr3(Mat mat)
    {
        if (mat.Channels() == 4)
        {
            Cv2.CvtColor(mat, mat, ColorConversionCodes.BGRA2BGR);
        }
        else if (mat.Channels() == 1)
        {
            Cv2.CvtColor(mat, mat, ColorConversionCodes.GRAY2BGR);
        }
    }

    private Mat? GetOrLoadTexture(string filename)
    {
        if (_textureCache.TryGetValue(filename, out Mat? cached) && cached is not null && !cached.IsDisposed)
        {
            return cached;
        }

        string fullPath = Path.Combine(MediaFolder, filename);
        if (!File.Exists(fullPath)) return null;

        try
        {
            var mode = filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? ImreadModes.Unchanged
                : ImreadModes.Color;

            Mat loaded = Cv2.ImRead(fullPath, mode);
            if (!loaded.Empty())
            {
                _textureCache[filename] = loaded;
                return loaded;
            }
            loaded.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OverlayRenderer] Error al cargar textura '{filename}': {ex.Message}");
        }

        return null;
    }

    private void EnsureGifLoaded(string filename)
    {
        if (_gifFrames.Count > 0) return;

        string path = Path.Combine(MediaFolder, filename);
        if (!File.Exists(path)) return;

        try
        {
            using var cap = new VideoCapture(path);
            if (cap.IsOpened())
            {
                while (true)
                {
                    var m = new Mat();
                    if (!cap.Read(m) || m.Empty())
                    {
                        m.Dispose();
                        break;
                    }
                    _gifFrames.Add(m);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OverlayRenderer] Error al cargar GIF '{filename}': {ex.Message}");
        }
    }

    private void EnsureAuxVideoOpen(string videoFilename)
    {
        if (_auxVideoCap is not null && _auxVideoCap.IsOpened()) return;

        string path = Path.Combine(MediaFolder, videoFilename);
        if (!File.Exists(path)) return;

        try
        {
            _auxVideoCap = new VideoCapture(path);
            double fps = _auxVideoCap.Get(VideoCaptureProperties.Fps);
            _auxVideoFps = fps is > 5 and < 120 ? fps : 25.0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OverlayRenderer] Error al abrir video '{videoFilename}': {ex.Message}");
        }
    }

    public static string FormatGestureName(GestureType gesture) => gesture switch
    {
        GestureType.Fist => "Puno cerrado",
        GestureType.ThumbsUp => "Pulgar arriba",
        GestureType.OneFinger => "1 Dedo (Indice)",
        GestureType.TwoFingers => "2 Dedos (Paz / Victoria)",
        GestureType.ThreeFingers => "3 Dedos",
        GestureType.FourFingers => "4 Dedos",
        GestureType.OpenHand => "Mano abierta (5 dedos)",
        GestureType.Rock => "Signo del Rock / Cuernos",
        GestureType.FingerGun => "Pistola con dedos (High Noon)",
        GestureType.PrayingPalms => "Palmas en Oracion",
        GestureType.SteepledFingertips => "Manos en Ojiva (Merkel)",
        GestureType.MilitarySalute => "Saludo Militar",
        GestureType.HandsOnHead => "Manos en la Cabeza",
        _ => "Ninguno"
    };

    private static void DrawPillText(Mat frame, string text, Point origin, Scalar color, double fontScale)
    {
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, fontScale, 1, out int baseline);
        var bgRect = ClampRect(new Rect(origin.X - 2, origin.Y - size.Height - 4, size.Width + 6, size.Height + baseline + 6), frame.Cols, frame.Rows);

        Cv2.Rectangle(frame, bgRect, new Scalar(15, 15, 15), -1);
        Cv2.Rectangle(frame, bgRect, color, 1);

        Cv2.PutText(
            frame,
            text,
            new Point(bgRect.X + 3, bgRect.Y + size.Height + 2),
            HersheyFonts.HersheySimplex,
            fontScale,
            color,
            1,
            LineTypes.AntiAlias);
    }

    private static void DrawStylizedBox(Mat frame, Rect box, Scalar color, int thickness)
    {
        box = ClampRect(box, frame.Cols, frame.Rows);
        Cv2.Rectangle(frame, box, color, thickness, LineTypes.AntiAlias);

        int cornerLen = Math.Min(box.Width / 4, Math.Min(box.Height / 4, 18));
        int t = thickness + 1;

        Cv2.Line(frame, new Point(box.X, box.Y), new Point(box.X + cornerLen, box.Y), color, t);
        Cv2.Line(frame, new Point(box.X, box.Y), new Point(box.X, box.Y + cornerLen), color, t);

        Cv2.Line(frame, new Point(box.Right, box.Y), new Point(box.Right - cornerLen, box.Y), color, t);
        Cv2.Line(frame, new Point(box.Right, box.Y), new Point(box.Right, box.Y + cornerLen), color, t);

        Cv2.Line(frame, new Point(box.X, box.Bottom), new Point(box.X + cornerLen, box.Bottom), color, t);
        Cv2.Line(frame, new Point(box.X, box.Bottom), new Point(box.X, box.Bottom - cornerLen), color, t);

        Cv2.Line(frame, new Point(box.Right, box.Bottom), new Point(box.Right - cornerLen, box.Bottom), color, t);
        Cv2.Line(frame, new Point(box.Right, box.Bottom), new Point(box.Right, box.Bottom - cornerLen), color, t);
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, maxW - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, maxH - 1));
        int w = Math.Clamp(rect.Width, 1, Math.Max(1, maxW - x));
        int h = Math.Clamp(rect.Height, 1, Math.Max(1, maxH - y));
        return new Rect(x, y, w, h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var tex in _textureCache.Values)
        {
            tex.Dispose();
        }
        _textureCache.Clear();

        foreach (var f in _gifFrames)
        {
            f.Dispose();
        }
        _gifFrames.Clear();

        _auxVideoCap?.Dispose();
        _auxVideoCap = null;
        _auxVideoFrame?.Dispose();
        _auxVideoFrame = null;
    }
}