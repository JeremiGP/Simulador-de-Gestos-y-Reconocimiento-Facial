using System;
using System.Collections.Generic;
using System.IO;
using OpenCvSharp;
using Stupipandi.Game;
using Stupipandi.Gestures;
using Stupipandi.State;

namespace Stupipandi.Biometrics;

/// <summary>
/// Motor gráfico de alto rendimiento para renderizar telemetría, realidad aumentada (sombreros AR),
/// animaciones GIF, video en miniatura (PiP), reemplazo de fondo y elementos interactivos del juego High Noon.
/// Implementa IDisposable para garantizar la liberación estricta de texturas y matrices nativas.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private static readonly Scalar ColorRecognized  = new(50, 220, 50);    // Verde esmeralda
    private static readonly Scalar ColorUnknown     = new(40, 40, 230);    // Rojo carmesí
    private static readonly Scalar ColorWarning     = new(0, 215, 255);    // Amarillo dorado
    private static readonly Scalar ColorEye         = new(255, 210, 40);   // Cyan cielo
    private static readonly Scalar ColorGesture     = new(20, 160, 255);   // Naranja ámbar
    private static readonly Scalar ColorWhite       = new(255, 255, 255);  // Blanco
    private static readonly Scalar ColorMuted       = new(190, 190, 190);  // Gris claro

    private static readonly string MediaFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Media");

    // Caché de texturas AR y multimedia
    private readonly Dictionary<string, Mat> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Mat> _gifFrames = new();
    private int _gifFrameIndex = 0;
    private long _lastGifFrameTicks = 0;

    // Reproductor de video auxiliar para PiP (Trigger 6: video_1.mp4)
    private VideoCapture? _auxVideoCap;
    private string? _auxVideoPath;
    private Mat? _auxVideoFrame;

    // Filtros de suavizado EMA para sombreros AR (evitar jitter)
    private Rect? _smoothedHatRect;
    private Rect? _smoothedKippahRect;
    private const double HatAlpha = 0.28;

    private bool _disposed;

    /// <summary>
    /// Dibuja el recuadro facial estabilizado, etiqueta de usuario, confianza, ojos y badges de expresión.
    /// </summary>
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

        // Badge de expresión facial y apertura oral
        if (expression is not null && expression.Expression != FacialExpression.Neutral)
        {
            Scalar expColor = expression.Expression switch
            {
                FacialExpression.Smiling    => ColorWarning,
                FacialExpression.Surprised  => ColorEye,
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

    /// <summary>
    /// Dibuja el cuadro estabilizado y la identificación del gesto de mano.
    /// </summary>
    public void DrawGesture(Mat frame, Rect handBounds, GestureType gesture)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty() || gesture == GestureType.None || handBounds.Width <= 0 || handBounds.Height <= 0) return;

        DrawStylizedBox(frame, handBounds, ColorGesture, 2);

        string gestureName = FormatGestureName(gesture);
        int textY = Math.Max(20, handBounds.Y - 8);
        DrawPillText(frame, $"Gesto: {gestureName}", new Point(handBounds.X, textY), ColorGesture, 0.58);
    }

    /// <summary>
    /// Renderiza el sombrero de vaquero AR con suavizado EMA encima de la cabeza del usuario.
    /// </summary>
    public void DrawCowboyHat(Mat frame, Rect faceBounds)
    {
        Mat? hat = GetOrLoadTexture("cowboy_hat.png");
        if (hat is null || hat.Empty()) return;

        // Calcular posición geométrica encima de la cabeza
        int hatWidth = (int)(faceBounds.Width * 1.55);
        int hatHeight = (int)(hatWidth * ((double)hat.Height / Math.Max(1, hat.Width)));
        int hatX = faceBounds.X + (faceBounds.Width - hatWidth) / 2;
        int hatY = faceBounds.Y - (int)(hatHeight * 0.75);

        Rect targetRect = new(hatX, hatY, hatWidth, hatHeight);

        // Suavizado espacial EMA para evitar temblores
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

    /// <summary>
    /// Renderiza la Kipá tradicional judía con anclaje suavizado sobre la coronilla del usuario.
    /// </summary>
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

    /// <summary>
    /// Muestra una imagen overlay centrada en pantalla (Trigger 1: Lion Roar Meme).
    /// </summary>
    public void DrawMemeOverlay(Mat frame, string filename = "image_84e5ca.jpg")
    {
        Mat? img = GetOrLoadTexture(filename);
        if (img is null || img.Empty()) return;

        int targetW = Math.Min((int)(frame.Cols * 0.45), img.Cols);
        int targetH = (int)(targetW * ((double)img.Rows / img.Cols));

        int x = (frame.Cols - targetW) / 2;
        int y = frame.Rows - targetH - 40;

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        using var resized = new Mat();
        Cv2.Resize(img, resized, new Size(target.Width, target.Height));

        // Marco estilizado
        Cv2.Rectangle(frame, new Rect(target.X - 3, target.Y - 3, target.Width + 6, target.Height + 6), ColorWarning, 2);
        resized.CopyTo(frame[target]);
    }

    /// <summary>
    /// Renderiza un marco animado de GIF en la esquina superior derecha (Trigger 2: guy-funny.gif).
    /// </summary>
    public void DrawAnimatedGif(Mat frame, string filename = "guy-funny.gif")
    {
        EnsureGifLoaded(filename);
        if (_gifFrames.Count == 0) return;

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (nowTicks - _lastGifFrameTicks > System.Diagnostics.Stopwatch.Frequency / 15) // ~15 FPS
        {
            _gifFrameIndex = (_gifFrameIndex + 1) % _gifFrames.Count;
            _lastGifFrameTicks = nowTicks;
        }

        Mat currentFrame = _gifFrames[_gifFrameIndex];
        int targetW = (int)(frame.Cols * 0.32);
        int targetH = (int)(targetW * ((double)currentFrame.Rows / Math.Max(1, currentFrame.Cols)));

        int x = frame.Cols - targetW - 20;
        int y = 75;

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        using var resized = new Mat();
        Cv2.Resize(currentFrame, resized, new Size(target.Width, target.Height));

        Cv2.Rectangle(frame, new Rect(target.X - 2, target.Y - 2, target.Width + 4, target.Height + 4), ColorWarning, 2);
        resized.CopyTo(frame[target]);
    }

    /// <summary>
    /// Renderiza un video en Picture-in-Picture en la esquina superior izquierda (Trigger 6: video_1.mp4).
    /// </summary>
    public void DrawPictureInPictureVideo(Mat frame, string videoFilename = "video_1.mp4")
    {
        EnsureAuxVideoOpen(videoFilename);
        if (_auxVideoCap is null || !_auxVideoCap.IsOpened()) return;

        _auxVideoFrame ??= new Mat();
        if (!_auxVideoCap.Read(_auxVideoFrame) || _auxVideoFrame.Empty())
        {
            // Reiniciar video al inicio en bucle
            _auxVideoCap.Set(VideoCaptureProperties.PosFrames, 0);
            if (!_auxVideoCap.Read(_auxVideoFrame) || _auxVideoFrame.Empty())
                return;
        }

        int targetW = (int)(frame.Cols * 0.30);
        int targetH = (int)(targetW * ((double)_auxVideoFrame.Rows / Math.Max(1, _auxVideoFrame.Cols)));
        int x = 20;
        int y = 75;

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        using var resized = new Mat();
        Cv2.Resize(_auxVideoFrame, resized, new Size(target.Width, target.Height));

        Cv2.Rectangle(frame, new Rect(target.X - 2, target.Y - 2, target.Width + 4, target.Height + 4), new Scalar(0, 0, 220), 2);
        resized.CopyTo(frame[target]);
    }

    /// <summary>
    /// Dibuja la insignia o imagen de Dragonite patriota en la esquina inferior derecha (Trigger 6).
    /// </summary>
    public void DrawDragoniteOverlay(Mat frame, string filename = "dragonite_espanol.jpeg")
    {
        Mat? img = GetOrLoadTexture(filename);
        if (img is null || img.Empty()) return;

        int targetW = (int)(frame.Cols * 0.28);
        int targetH = (int)(targetW * ((double)img.Rows / Math.Max(1, img.Cols)));

        int x = frame.Cols - targetW - 20;
        int y = frame.Rows - targetH - 35;

        Rect target = ClampRect(new Rect(x, y, targetW, targetH), frame.Cols, frame.Rows);
        using var resized = new Mat();
        Cv2.Resize(img, resized, new Size(target.Width, target.Height));

        Cv2.Rectangle(frame, new Rect(target.X - 2, target.Y - 2, target.Width + 4, target.Height + 4), ColorWarning, 2);
        resized.CopyTo(frame[target]);
    }

    /// <summary>
    /// Reemplaza el fondo de la cámara por la imagen provista (Trigger 4: jesus_bg.jpg),
    /// preservando la silueta humana a través de la máscara de piel y contorno corporal.
    /// </summary>
    public void ReplaceBackground(Mat frame, Mat skinMask, Rect? faceBounds, string bgFilename = "jesus_bg.jpg")
    {
        Mat? bg = GetOrLoadTexture(bgFilename);
        if (bg is null || bg.Empty()) return;

        using var resizedBg = new Mat();
        Cv2.Resize(bg, resizedBg, new Size(frame.Cols, frame.Rows));

        // Crear máscara humana aproximada combinando la piel con una dilatación moderada
        using var humanMask = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(25, 25));
        Cv2.Dilate(skinMask, humanMask, kernel, iterations: 2);

        // Si hay rostro detectado, asegurar que el óvalo del rostro y el torso estén incluidos
        if (faceBounds.HasValue)
        {
            var face = faceBounds.Value;
            // Zona del rostro
            Cv2.Circle(humanMask, new Point(face.X + face.Width / 2, face.Y + face.Height / 2), (int)(face.Width * 0.65), Scalar.White, -1);
            // Zona del cuello y torso superior
            var torso = new Rect(
                Math.Max(0, face.X - (int)(face.Width * 0.40)),
                face.Bottom,
                Math.Min(frame.Cols, (int)(face.Width * 1.80)),
                Math.Min(frame.Rows - face.Bottom, (int)(face.Height * 1.60)));
            Cv2.Rectangle(humanMask, ClampRect(torso, frame.Cols, frame.Rows), Scalar.White, -1);
        }

        // Suavizar bordes de la máscara para composición natural
        Cv2.GaussianBlur(humanMask, humanMask, new Size(21, 21), 0);

        // Composición pixel a pixel con canal de peso
        unsafe
        {
            byte* framePtr = (byte*)frame.Data.ToPointer();
            byte* bgPtr = (byte*)resizedBg.Data.ToPointer();
            byte* maskPtr = (byte*)humanMask.Data.ToPointer();

            int totalPixels = frame.Cols * frame.Rows;

            for (int i = 0; i < totalPixels; i++)
            {
                byte alpha = maskPtr[i];
                if (alpha < 240)
                {
                    double fgWeight = alpha / 255.0;
                    double bgWeight = 1.0 - fgWeight;

                    int idx = i * 3;
                    framePtr[idx + 0] = (byte)(framePtr[idx + 0] * fgWeight + bgPtr[idx + 0] * bgWeight);
                    framePtr[idx + 1] = (byte)(framePtr[idx + 1] * fgWeight + bgPtr[idx + 1] * bgWeight);
                    framePtr[idx + 2] = (byte)(framePtr[idx + 2] * fgWeight + bgPtr[idx + 2] * bgWeight);
                }
            }
        }
    }

    /// <summary>
    /// Renderiza los elementos interactivos del minijuego High Noon (Trigger 3).
    /// </summary>
    public void DrawHighNoonGame(Mat frame, HighNoonGameEngine gameEngine, Point? indexFingerTip, Rect? faceBounds)
    {
        // 1. Título "HIGH NOON" con borde negro
        string title = "HIGH NOON";
        var tSize = Cv2.GetTextSize(title, HersheyFonts.HersheyComplex, 1.3, 3, out int baseline);
        int tx = (frame.Cols - tSize.Width) / 2;
        int ty = 90;

        Cv2.PutText(frame, title, new Point(tx, ty), HersheyFonts.HersheyComplex, 1.3, Scalar.Black, 6, LineTypes.AntiAlias);
        Cv2.PutText(frame, title, new Point(tx, ty), HersheyFonts.HersheyComplex, 1.3, new Scalar(0, 220, 255), 3, LineTypes.AntiAlias);

        // 2. Sombrero de vaquero en la cabeza
        if (faceBounds.HasValue)
        {
            DrawCowboyHat(frame, faceBounds.Value);
        }

        // 3. Renderizar barriles que caen
        Mat? barrelTex = GetOrLoadTexture("barrel.png");
        foreach (var b in gameEngine.ActiveBarrels)
        {
            Rect bRect = new((int)(b.X - b.Radius), (int)(b.Y - b.Radius), (int)(b.Radius * 2), (int)(b.Radius * 2));
            if (barrelTex is not null && !barrelTex.Empty())
            {
                DrawAlphaOverlay(frame, barrelTex, bRect);
            }
            else
            {
                Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)b.Radius, new Scalar(30, 80, 180), -1);
                Cv2.Circle(frame, new Point((int)b.X, (int)b.Y), (int)b.Radius, new Scalar(60, 140, 240), 2);
            }
        }

        // 4. Renderizar partículas de chispas
        foreach (var p in gameEngine.ActiveParticles)
        {
            Cv2.Circle(frame, new Point((int)p.X, (int)p.Y), (int)p.Radius, new Scalar(0, (int)(255 * p.Life), 255), -1);
        }

        // 5. Renderizar mira técnica en la punta del dedo índice
        if (indexFingerTip.HasValue)
        {
            Point tip = indexFingerTip.Value;
            Cv2.Circle(frame, tip, 16, new Scalar(0, 255, 255), 2, LineTypes.AntiAlias);
            Cv2.Circle(frame, tip, 4, new Scalar(0, 0, 255), -1);
            Cv2.Line(frame, new Point(tip.X - 22, tip.Y), new Point(tip.X + 22, tip.Y), new Scalar(0, 255, 255), 1);
            Cv2.Line(frame, new Point(tip.X, tip.Y - 22), new Point(tip.X, tip.Y + 22), new Scalar(0, 255, 255), 1);
        }

        // 6. Marcador de puntuación
        string scoreText = $"SCORE: {gameEngine.Score}   |   RECORD: {gameEngine.HighScore}";
        DrawPillText(frame, scoreText, new Point(frame.Cols / 2 - 120, 125), ColorWarning, 0.65);
    }

    /// <summary>
    /// Renderiza el HUD completo del simulador con barra de telemetría y barra de atajos.
    /// </summary>
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

        // Barra superior
        var topBar = new Rect(0, 0, frameW, 64);
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

        // Barra inferior
        var botBar = new Rect(0, frameH - 26, frameW, 26);
        Cv2.Rectangle(frame, botBar, new Scalar(16, 16, 16), -1);
        Cv2.Line(frame, new Point(0, frameH - 26), new Point(frameW, frameH - 26), new Scalar(50, 50, 50), 1);

        string shortcuts = "ESC/Q: Salir  |  T: Enrolar rostro  |  S: Guardar modelo  |  R: Reiniciar  |  C: Color Piel";
        Cv2.PutText(frame, shortcuts, new Point(12, frameH - 8),
            HersheyFonts.HersheySimplex, 0.40, ColorMuted, 1, LineTypes.AntiAlias);

        if (!string.IsNullOrWhiteSpace(activePrompt))
        {
            var pSize = Cv2.GetTextSize(activePrompt, HersheyFonts.HersheySimplex, 0.65, 2, out int pBaseline);
            var centerRect = new Rect(
                (frameW - pSize.Width) / 2 - 16,
                (frameH - pSize.Height) / 2 - 16,
                pSize.Width + 32,
                pSize.Height + pBaseline + 32);

            centerRect = ClampRect(centerRect, frameW, frameH);

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

    /// <summary>
    /// Superpone una textura BGRA de 4 canales con canal alfa sobre una región BGR de destino.
    /// </summary>
    private static void DrawAlphaOverlay(Mat frame, Mat overlayBgra, Rect destRect)
    {
        if (overlayBgra.Empty() || destRect.Width <= 0 || destRect.Height <= 0) return;

        // Limitar coordenadas dentro de los límites del lienzo
        int x0 = Math.Max(0, destRect.X);
        int y0 = Math.Max(0, destRect.Y);
        int x1 = Math.Min(frame.Cols, destRect.Right);
        int y1 = Math.Min(frame.Rows, destRect.Bottom);

        if (x1 <= x0 || y1 <= y0) return;

        int renderW = x1 - x0;
        int renderH = y1 - y0;

        using var resizedOverlay = new Mat();
        Cv2.Resize(overlayBgra, resizedOverlay, new Size(destRect.Width, destRect.Height));

        int srcX = x0 - destRect.X;
        int srcY = y0 - destRect.Y;
        using var srcSub = resizedOverlay[new Rect(srcX, srcY, renderW, renderH)];
        using var dstSub = frame[new Rect(x0, y0, renderW, renderH)];

        // Mezcla alfa por píxel
        if (srcSub.Channels() == 4)
        {
            unsafe
            {
                byte* srcPtr = (byte*)srcSub.Data.ToPointer();
                byte* dstPtr = (byte*)dstSub.Data.ToPointer();
                int totalPixels = renderW * renderH;

                for (int i = 0; i < totalPixels; i++)
                {
                    int srcIdx = i * 4;
                    int dstIdx = i * 3;
                    byte a = srcPtr[srcIdx + 3];

                    if (a > 0)
                    {
                        double alpha = a / 255.0;
                        double invAlpha = 1.0 - alpha;

                        dstPtr[dstIdx + 0] = (byte)(srcPtr[srcIdx + 0] * alpha + dstPtr[dstIdx + 0] * invAlpha);
                        dstPtr[dstIdx + 1] = (byte)(srcPtr[srcIdx + 1] * alpha + dstPtr[dstIdx + 1] * invAlpha);
                        dstPtr[dstIdx + 2] = (byte)(srcPtr[srcIdx + 2] * alpha + dstPtr[dstIdx + 2] * invAlpha);
                    }
                }
            }
        }
        else
        {
            srcSub.CopyTo(dstSub);
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
            // Intentar cargar con canal alfa si es PNG
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
            _auxVideoPath = path;
            _auxVideoCap = new VideoCapture(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OverlayRenderer] Error al abrir video '{videoFilename}': {ex.Message}");
        }
    }

    public static string FormatGestureName(GestureType gesture) => gesture switch
    {
        GestureType.Fist               => "Puno cerrado",
        GestureType.ThumbsUp           => "Pulgar arriba",
        GestureType.OneFinger          => "1 Dedo (Indice)",
        GestureType.TwoFingers         => "2 Dedos (Paz / Victoria)",
        GestureType.ThreeFingers       => "3 Dedos",
        GestureType.FourFingers        => "4 Dedos",
        GestureType.OpenHand           => "Mano abierta (5 dedos)",
        GestureType.Rock               => "Signo del Rock / Cuernos",
        GestureType.FingerGun          => "Pistola con dedos (High Noon)",
        GestureType.PrayingPalms       => "Palmas en Oracion",
        GestureType.SteepledFingertips => "Manos en Ojiva (Merkel)",
        GestureType.MilitarySalute     => "Saludo Militar",
        GestureType.HandsOnHead        => "Manos en la Cabeza",
        _                              => "Ninguno"
    };

    private static void DrawPillText(Mat frame, string text, Point origin, Scalar color, double fontScale)
    {
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, fontScale, 1, out int baseline);
        var bgRect = new Rect(origin.X - 2, origin.Y - size.Height - 4, size.Width + 6, size.Height + baseline + 6);
        bgRect = ClampRect(bgRect, frame.Cols, frame.Rows);

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

        // Bottom-Right
        Cv2.Line(frame, new Point(box.Right, box.Bottom), new Point(box.Right - cornerLen, box.Bottom), color, t);
        Cv2.Line(frame, new Point(box.Right, box.Bottom), new Point(box.Right, box.Bottom - cornerLen), color, t);
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, maxW - 1);
        int y = Math.Clamp(rect.Y, 0, maxH - 1);
        int w = Math.Clamp(rect.Width, 1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
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
