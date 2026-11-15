using System;
using System.Collections.Generic;
using OpenCvSharp;
using Stupipandi.Gestures;
using Stupipandi.State;

namespace Stupipandi.Biometrics;

/// <summary>
/// Motor gráfico de alto rendimiento para renderizar información de telemetría,
/// cuadros delimitadores estabilizados, badges de expresiones faciales y HUD interactivo.
/// Opera directamente sobre matrices BGR existentes sin copias de memoria innecesarias.
/// </summary>
public sealed class OverlayRenderer
{
    private static readonly Scalar ColorRecognized  = new(50, 220, 50);    // Verde esmeralda
    private static readonly Scalar ColorUnknown     = new(40, 40, 230);    // Rojo carmesí
    private static readonly Scalar ColorWarning     = new(0, 215, 255);    // Amarillo dorado
    private static readonly Scalar ColorEye         = new(255, 210, 40);   // Cyan cielo
    private static readonly Scalar ColorGesture     = new(20, 160, 255);   // Naranja ámbar
    private static readonly Scalar ColorWhite       = new(255, 255, 255);  // Blanco
    private static readonly Scalar ColorMuted       = new(190, 190, 190);  // Gris claro

    /// <summary>
    /// Dibuja el recuadro facial estabilizado, etiqueta de usuario, porcentaje de confianza,
    /// recuadros de ojos y badge de expresión facial.
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

        // Cuadro del rostro con esquinas reforzadas
        DrawStylizedBox(frame, faceBounds, mainColor, 2);

        // Etiqueta superior de identidad
        string labelText = result.Label;
        if (result.IsRecognized && result.ConfidencePercent > 0)
        {
            labelText += $" [{result.ConfidencePercent}%]";
        }
        else if (double.IsFinite(result.Distance))
        {
            labelText += $" (d={result.Distance:0.0})";
        }

        // Fondo de contraste superior
        int textY = faceBounds.Y > 28 ? faceBounds.Y - 8 : faceBounds.Y + 24;
        DrawPillText(frame, labelText, new Point(faceBounds.X, textY), mainColor, 0.55);

        // Badge de expresión facial (si está disponible)
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
                DrawPillText(frame, expression.Label, new Point(faceBounds.X, expY), expColor, 0.50);
            }
        }

        // Detección de ojos dentro del rostro
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
        string? activePrompt = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty()) return;

        int frameW = frame.Cols;
        int frameH = frame.Rows;

        // Barra superior de estado
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

        // Indicador luminoso de estado
        Cv2.Circle(frame, new Point(20, 22), 7, modeColor, -1);
        Cv2.PutText(frame, modeText, new Point(36, 27),
            HersheyFonts.HersheySimplex, 0.55, modeColor, 2, LineTypes.AntiAlias);

        // Línea 2: FPS, espacio de color y último gesto aceptado
        string subInfo = $"FPS: {fps:0.0}   |   Piel: {colorSpaceName}";
        if (!string.IsNullOrWhiteSpace(lastGesture) && mode == ApplicationMode.GestureInteraction)
        {
            subInfo += $"   |   Gesto activo: {lastGesture}";
        }

        Cv2.PutText(frame, subInfo, new Point(14, 50),
            HersheyFonts.HersheySimplex, 0.44, ColorWhite, 1, LineTypes.AntiAlias);

        // Barra inferior de atajos de teclado
        var botBar = new Rect(0, frameH - 26, frameW, 26);
        Cv2.Rectangle(frame, botBar, new Scalar(16, 16, 16), -1);
        Cv2.Line(frame, new Point(0, frameH - 26), new Point(frameW, frameH - 26), new Scalar(50, 50, 50), 1);

        string shortcuts = "ESC/Q: Salir  |  T: Enrolar rostro  |  S: Guardar modelo  |  R: Reiniciar  |  C: Espacio HSV/YCrCb";
        Cv2.PutText(frame, shortcuts, new Point(12, frameH - 8),
            HersheyFonts.HersheySimplex, 0.40, ColorMuted, 1, LineTypes.AntiAlias);

        // Modal de notificación emergente (ej. enrolando)
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
    /// Traduce el enum de gestos a una denominación clara en español.
    /// </summary>
    public static string FormatGestureName(GestureType gesture) => gesture switch
    {
        GestureType.Fist         => "Puno cerrado",
        GestureType.ThumbsUp     => "Pulgar arriba",
        GestureType.OneFinger    => "1 Dedo (Indice)",
        GestureType.TwoFingers   => "2 Dedos (Paz / Victoria)",
        GestureType.ThreeFingers => "3 Dedos",
        GestureType.FourFingers  => "4 Dedos",
        GestureType.OpenHand     => "Mano abierta (5 dedos)",
        GestureType.Rock         => "Signo del Rock / Cuernos",
        _                        => "Ninguno"
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

        // Esquinas resaltadas de mira técnica
        int cornerLen = Math.Min(box.Width / 4, Math.Min(box.Height / 4, 18));
        int t = thickness + 1;

        // Top-Left
        Cv2.Line(frame, new Point(box.X, box.Y), new Point(box.X + cornerLen, box.Y), color, t);
        Cv2.Line(frame, new Point(box.X, box.Y), new Point(box.X, box.Y + cornerLen), color, t);

        // Top-Right
        Cv2.Line(frame, new Point(box.Right, box.Y), new Point(box.Right - cornerLen, box.Y), color, t);
        Cv2.Line(frame, new Point(box.Right, box.Y), new Point(box.Right, box.Y + cornerLen), color, t);

        // Bottom-Left
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
}
