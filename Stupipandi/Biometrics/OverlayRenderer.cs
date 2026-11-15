using System;
using System.Collections.Generic;
using OpenCvSharp;
using Stupipandi.Gestures;
using Stupipandi.State;

namespace Stupipandi.Biometrics;

/// <summary>
/// Dibuja los resultados biométricos, información de gestos y el HUD del simulador sobre el frame.
/// Modifica el <see cref="Mat"/> recibido; no crea copias innecesarias ni libera el frame original.
/// </summary>
public sealed class OverlayRenderer
{
    private static readonly Scalar ColorRecognized  = new(0, 220, 0);     // Verde brillante
    private static readonly Scalar ColorUnknown     = new(40, 40, 230);   // Rojo
    private static readonly Scalar ColorEye         = new(255, 200, 0);   // Cyan/Azul claro
    private static readonly Scalar ColorSmile       = new(0, 255, 255);   // Amarillo
    private static readonly Scalar ColorGesture     = new(0, 165, 255);   // Naranja
    private static readonly Scalar ColorHudText     = new(255, 255, 255); // Blanco
    private static readonly Scalar ColorHudMuted    = new(190, 190, 190); // Gris claro

    /// <summary>
    /// Dibuja el cuadro delimitador del rostro, la etiqueta de identificación con fondo de contraste,
    /// los ojos detectados y el indicador de sonrisa.
    /// </summary>
    public void DrawFace(
        Mat frame,
        Rect faceBounds,
        FaceRecognitionResult result,
        IReadOnlyList<Rect>? eyeBounds = null,
        bool isSmiling = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(result);
        if (frame.Empty()) return;

        Scalar color = result.IsRecognized ? ColorRecognized : ColorUnknown;

        // Cuadro del rostro
        Cv2.Rectangle(frame, faceBounds, color, 2, LineTypes.AntiAlias);

        // Etiqueta superior
        string label = result.Label;
        if (double.IsFinite(result.Distance))
        {
            label += $" ({result.Distance:0.0})";
        }

        if (isSmiling)
        {
            label += "  [Sonrisa :-)]";
        }

        // Fondo para que el texto sea perfectamente legible
        int textY = faceBounds.Y > 26 ? faceBounds.Y - 8 : faceBounds.Y + 22;
        var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.55, 1, out int baseline);
        var labelOrigin = new Point(Math.Max(0, faceBounds.X), Math.Max(18, textY));
        var bgRect = new Rect(
            labelOrigin.X - 2,
            labelOrigin.Y - textSize.Height - 4,
            textSize.Width + 6,
            textSize.Height + baseline + 6);

        // Clamping al frame
        bgRect = ClampRect(bgRect, frame.Cols, frame.Rows);
        Cv2.Rectangle(frame, bgRect, Scalar.Black, -1);
        Cv2.Rectangle(frame, bgRect, color, 1);

        Cv2.PutText(
            frame,
            label,
            labelOrigin,
            HersheyFonts.HersheySimplex,
            0.55,
            color,
            1,
            LineTypes.AntiAlias);

        // Detección de ojos dentro del rostro
        if (eyeBounds is not null)
        {
            foreach (Rect eye in eyeBounds)
            {
                Cv2.Rectangle(frame, eye, ColorEye, 1, LineTypes.AntiAlias);
            }
        }
    }

    /// <summary>
    /// Dibuja el cuadro y nombre del gesto de la mano detectado.
    /// </summary>
    public void DrawGesture(Mat frame, Rect handBounds, GestureType gesture)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty() || gesture == GestureType.Unknown) return;

        Cv2.Rectangle(frame, handBounds, ColorGesture, 2, LineTypes.AntiAlias);

        string gestureName = FormatGestureName(gesture);

        int textY = Math.Max(18, handBounds.Y - 8);
        var textSize = Cv2.GetTextSize(gestureName, HersheyFonts.HersheySimplex, 0.60, 2, out int baseline);
        var bgRect = ClampRect(new Rect(handBounds.X, textY - textSize.Height - 4, textSize.Width + 6, textSize.Height + baseline + 4), frame.Cols, frame.Rows);

        Cv2.Rectangle(frame, bgRect, Scalar.Black, -1);
        Cv2.Rectangle(frame, bgRect, ColorGesture, 1);

        Cv2.PutText(
            frame,
            gestureName,
            new Point(bgRect.X + 3, bgRect.Y + textSize.Height + 2),
            HersheyFonts.HersheySimplex,
            0.60,
            ColorGesture,
            2,
            LineTypes.AntiAlias);
    }

    /// <summary>
    /// Renders el HUD superior e inferior con estado del sistema, FPS, atajos y espacio de color activo.
    /// </summary>
    public void DrawHud(
        Mat frame,
        ApplicationMode mode,
        double fps,
        string? currentUser = null,
        string? lastGesture = null,
        string? colorSpaceName = "HSV",
        string? activePrompt = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty()) return;

        int frameW = frame.Cols;
        int frameH = frame.Rows;

        // Barra superior translúcida / oscura
        var topBar = new Rect(0, 0, frameW, 64);
        Cv2.Rectangle(frame, topBar, new Scalar(25, 25, 25), -1);
        Cv2.Line(frame, new Point(0, 64), new Point(frameW, 64), new Scalar(70, 70, 70), 1);

        // Indicador de modo
        (string modeText, Scalar modeColor) = mode switch
        {
            ApplicationMode.AwaitingFace =>
                ("Esperando rostro en camara...", ColorHudMuted),
            ApplicationMode.GestureInteraction =>
                ($"ACCESO CONCEDIDO: {currentUser ?? "Usuario"} (Interaccion activa)", ColorRecognized),
            ApplicationMode.AccessDenied =>
                ("ACCESO DENEGADO: Rostro desconocido", ColorUnknown),
            ApplicationMode.TrainingRequired =>
                ("MODELO SIN ENTRENAR: Presione 'T' para enrolar su rostro", ColorSmile),
            ApplicationMode.Exiting =>
                ("Cerrando simulador...", ColorHudMuted),
            _ => (mode.ToString(), ColorHudMuted)
        };

        // Círculo indicador de estado
        Cv2.Circle(frame, new Point(18, 22), 6, modeColor, -1);
        Cv2.PutText(frame, modeText, new Point(32, 27),
            HersheyFonts.HersheySimplex, 0.58, modeColor, 2, LineTypes.AntiAlias);

        // Línea 2: FPS y Espacio de Color
        string subInfo = $"FPS: {fps:0.0}   |   Color piel: {colorSpaceName}";
        if (!string.IsNullOrWhiteSpace(lastGesture) && mode == ApplicationMode.GestureInteraction)
        {
            subInfo += $"   |   Ultimo gesto: {lastGesture}";
        }

        Cv2.PutText(frame, subInfo, new Point(14, 50),
            HersheyFonts.HersheySimplex, 0.46, ColorHudText, 1, LineTypes.AntiAlias);

        // Banner inferior de atajos de teclado
        var botBar = new Rect(0, frameH - 26, frameW, 26);
        Cv2.Rectangle(frame, botBar, new Scalar(20, 20, 20), -1);
        Cv2.Line(frame, new Point(0, frameH - 26), new Point(frameW, frameH - 26), new Scalar(60, 60, 60), 1);

        string shortcuts = "ESC/Q: Salir  |  T: Enrolar rostro  |  S: Guardar modelo  |  R: Reiniciar  |  C: Cambiar espacio color";
        Cv2.PutText(frame, shortcuts, new Point(12, frameH - 8),
            HersheyFonts.HersheySimplex, 0.42, ColorHudMuted, 1, LineTypes.AntiAlias);

        // Mensaje interactivo destacado si existe (ej. enrolando)
        if (!string.IsNullOrWhiteSpace(activePrompt))
        {
            var promptSize = Cv2.GetTextSize(activePrompt, HersheyFonts.HersheySimplex, 0.65, 2, out int pBaseline);
            var centerRect = new Rect(
                (frameW - promptSize.Width) / 2 - 12,
                (frameH - promptSize.Height) / 2 - 12,
                promptSize.Width + 24,
                promptSize.Height + pBaseline + 24);
            centerRect = ClampRect(centerRect, frameW, frameH);

            Cv2.Rectangle(frame, centerRect, new Scalar(10, 10, 80), -1);
            Cv2.Rectangle(frame, centerRect, ColorSmile, 2);
            Cv2.PutText(
                frame,
                activePrompt,
                new Point(centerRect.X + 12, centerRect.Y + promptSize.Height + 10),
                HersheyFonts.HersheySimplex,
                0.65,
                ColorHudText,
                2,
                LineTypes.AntiAlias);
        }
    }

    public static string FormatGestureName(GestureType gesture) => gesture switch
    {
        GestureType.Fist         => "Puno cerrado",
        GestureType.OneFinger    => "1 Dedo (Indice)",
        GestureType.TwoFingers   => "2 Dedos (Paz / Victoria)",
        GestureType.ThreeFingers => "3 Dedos",
        GestureType.FourFingers  => "4 Dedos",
        GestureType.OpenHand     => "Mano abierta (5 dedos)",
        _                        => "Desconocido"
    };

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, maxW - 1);
        int y = Math.Clamp(rect.Y, 0, maxH - 1);
        int w = Math.Clamp(rect.Width, 1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }
}
