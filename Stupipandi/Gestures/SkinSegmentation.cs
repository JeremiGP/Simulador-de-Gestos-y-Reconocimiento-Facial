using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Convierte fotogramas BGR en máscaras binarias de piel usando HSV o YCrCb
/// con supresión forzada de la región cefálica (rostro, cabello y cuello)
/// para erradicar por completo la detección de dedos fantasma en la cabeza.
/// </summary>
public static class SkinSegmentation
{
    public enum ColorSpace
    {
        Hsv,
        YCrCb
    }

    /// <summary>
    /// Genera una máscara binaria de piel (8UC1) enmascarando en negro (0) cualquier rostro y cuello detectado.
    /// </summary>
    /// <param name="bgrFrame">Fotograma BGR de entrada.</param>
    /// <param name="colorSpace">Espacio de color para umbralización.</param>
    /// <param name="facesToSuppress">Colección de rectángulos faciales para suprimir rostro, pelo y cuello.</param>
    /// <param name="cleanNoise">Aplica apertura y cierre morfológico.</param>
    public static Mat CreateMask(
        Mat bgrFrame,
        ColorSpace colorSpace = ColorSpace.Hsv,
        IReadOnlyList<Rect>? facesToSuppress = null,
        bool cleanNoise = true)
    {
        ValidateFrame(bgrFrame);

        using var converted = new Mat();
        Cv2.CvtColor(
            bgrFrame,
            converted,
            colorSpace == ColorSpace.Hsv
                ? ColorConversionCodes.BGR2HSV
                : ColorConversionCodes.BGR2YCrCb);

        var mask = new Mat();
        try
        {
            var (lower, upper) = GetSkinRange(colorSpace);
            Cv2.InRange(converted, lower, upper, mask);

            // ── Supresión radical de cabeza, cabello y cuello ────────────────
            if (facesToSuppress is not null)
            {
                foreach (var face in facesToSuppress)
                {
                    // Expansión lateral (+28%), superior para cabello (+42%) e inferior para cuello (+85%)
                    int padX = (int)(face.Width * 0.28);
                    int padTop = (int)(face.Height * 0.42);
                    int padBottom = (int)(face.Height * 0.85);

                    int x = Math.Clamp(face.X - padX, 0, mask.Cols - 1);
                    int y = Math.Clamp(face.Y - padTop, 0, mask.Rows - 1);
                    int w = Math.Clamp(face.Width + (padX * 2), 1, mask.Cols - x);
                    int h = Math.Clamp(face.Height + padTop + padBottom, 1, mask.Rows - y);

                    // Llenar con negro absoluto (0) en la máscara binaria
                    Cv2.Rectangle(mask, new Rect(x, y, w, h), Scalar.Black, -1);
                }
            }

            if (cleanNoise)
            {
                CleanMask(mask);
            }

            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Elimina ruido aislado y rellena oquedades en los dedos mediante morfología matemática.
    /// </summary>
    public static void CleanMask(Mat binaryMask, int kernelSize = 5)
    {
        ArgumentNullException.ThrowIfNull(binaryMask);
        if (binaryMask.IsDisposed)
            throw new ObjectDisposedException(nameof(binaryMask));

        if (binaryMask.Empty() || binaryMask.Channels() != 1)
        {
            throw new ArgumentException("La máscara debe estar inicializada y tener un único canal.", nameof(binaryMask));
        }

        if (kernelSize < 3 || kernelSize % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kernelSize), "El tamaño del kernel debe ser impar y mayor o igual que 3.");
        }

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
        Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Close, kernel);
    }

    private static (Scalar Lower, Scalar Upper) GetSkinRange(ColorSpace colorSpace)
    {
        return colorSpace switch
        {
            ColorSpace.Hsv =>
                (new Scalar(0, 32, 60), new Scalar(25, 180, 255)),
            ColorSpace.YCrCb =>
                (new Scalar(0, 133, 77), new Scalar(255, 173, 127)),
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace))
        };
    }

    private static void ValidateFrame(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed)
            throw new ObjectDisposedException(nameof(frame));

        if (frame.Empty() || frame.Channels() != 3)
        {
            throw new ArgumentException("El fotograma debe ser BGR, estar inicializado y tener tres canales.", nameof(frame));
        }
    }
}
