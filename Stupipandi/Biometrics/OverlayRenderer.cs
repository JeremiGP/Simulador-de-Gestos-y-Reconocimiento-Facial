using System;
using System.Collections.Generic;
using OpenCvSharp;
using GestureFaceSimulator.State;

namespace GestureFaceSimulator.Biometrics;

/// <summary>
/// Dibuja los resultados biométricos y la información del simulador sobre el frame.
/// Modifica el Mat recibido; no crea ni libera el frame original.
/// </summary>
public sealed class OverlayRenderer
{
    private static readonly Scalar RecognizedColor = new(0, 220, 0);
    private static readonly Scalar UnknownColor = new(0, 0, 230);
    private static readonly Scalar EyeColor = new(255, 180, 0);
    private static readonly Scalar HudColor = new(255, 255, 255);

    public void DrawFace(
        Mat frame,
        Rect faceBounds,
        FaceRecognitionResult result,
        IReadOnlyList<Rect>? eyeBounds = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(result);
        if (frame.Empty())
            return;

        Scalar color = result.IsRecognized ? RecognizedColor : UnknownColor;
        Cv2.Rectangle(frame, faceBounds, color, 2, LineTypes.AntiAlias);

        string label = result.Label;
        if (double.IsFinite(result.Distance))
            label += $" ({result.Distance:0.0})";

        int textY = faceBounds.Y > 26 ? faceBounds.Y - 8 : faceBounds.Y + 22;
        Cv2.PutText(
            frame,
            label,
            new Point(Math.Max(0, faceBounds.X), Math.Max(18, textY)),
            HersheyFonts.HersheySimplex,
            0.55,
            color,
            2,
            LineTypes.AntiAlias);

        if (eyeBounds is null)
            return;

        foreach (Rect eye in eyeBounds)
            Cv2.Rectangle(frame, eye, EyeColor, 1, LineTypes.AntiAlias);
    }

    public void DrawHud(Mat frame, ApplicationMode mode, double fps, string? lastGesture = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
            return;

        string modeText = mode switch
        {
            ApplicationMode.AwaitingFace => "Esperando rostro",
            ApplicationMode.GestureInteraction => "Interaccion por gestos",
            ApplicationMode.AccessDenied => "Acceso no reconocido",
            ApplicationMode.TrainingRequired => "Modelo facial sin entrenar",
            ApplicationMode.Exiting => "Finalizando",
            _ => mode.ToString()
        };

        Cv2.PutText(
            frame,
            modeText,
            new Point(12, 26),
            HersheyFonts.HersheySimplex,
            0.62,
            HudColor,
            2,
            LineTypes.AntiAlias);

        if (double.IsFinite(fps) && fps >= 0)
        {
            Cv2.PutText(
                frame,
                $"FPS: {fps:0.0}",
                new Point(12, 52),
                HersheyFonts.HersheySimplex,
                0.52,
                HudColor,
                1,
                LineTypes.AntiAlias);
        }

        if (!string.IsNullOrWhiteSpace(lastGesture) && mode == ApplicationMode.GestureInteraction)
        {
            Cv2.PutText(
                frame,
                $"Gesto: {lastGesture}",
                new Point(12, 78),
                HersheyFonts.HersheySimplex,
                0.52,
                HudColor,
                1,
                LineTypes.AntiAlias);
        }
    }
}
