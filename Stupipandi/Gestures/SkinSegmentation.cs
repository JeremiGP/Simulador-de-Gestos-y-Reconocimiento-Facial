using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace Stupipandi.Gestures;

public static class SkinSegmentation
{
    public enum ColorSpace
    {
        Hsv,
        YCrCb
    }

    public static Mat CreateMask(
        Mat bgrFrame,
        ColorSpace colorSpace = ColorSpace.Hsv,
        IReadOnlyList<Rect>? facesToSuppress = null,
        bool cleanNoise = true)
    {
        ValidateFrame(bgrFrame);

        using var hsv = new Mat();
        using var ycrcb = new Mat();
        Cv2.CvtColor(bgrFrame, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(bgrFrame, ycrcb, ColorConversionCodes.BGR2YCrCb);

        using var maskHsv = new Mat();
        using var maskYcrcb = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 38, 65), new Scalar(24, 185, 255), maskHsv);
        Cv2.InRange(ycrcb, new Scalar(0, 133, 77), new Scalar(255, 173, 127), maskYcrcb);

        var mask = new Mat();
        try
        {
            if (colorSpace == ColorSpace.Hsv)
            {
                Cv2.BitwiseAnd(maskHsv, maskYcrcb, mask);
            }
            else
            {
                maskYcrcb.CopyTo(mask);
            }

            if (facesToSuppress is not null)
            {
                foreach (var face in facesToSuppress)
                {
                    // 1. Cabeza y cabello (margen lateral ajustado para NO borrar las manos en la cabeza)
                    int padX = (int)(face.Width * 0.10);
                    int padTop = (int)(face.Height * 0.30);
                    int padChin = (int)(face.Height * 0.08);

                    int hx = Math.Clamp(face.X - padX, 0, mask.Cols - 1);
                    int hy = Math.Clamp(face.Y - padTop, 0, mask.Rows - 1);
                    int hw = Math.Clamp(face.Width + (padX * 2), 1, mask.Cols - hx);
                    int hh = Math.Clamp(face.Height + padTop + padChin, 1, mask.Rows - hy);
                    Cv2.Rectangle(mask, new Rect(hx, hy, hw, hh), Scalar.Black, -1);

                    // 2. Cuello corto y estrecho (deja libre la zona del pecho para ojiva/rezo)
                    int neckW = (int)(face.Width * 0.38);
                    int neckX = Math.Clamp(face.X + (face.Width - neckW) / 2, 0, mask.Cols - 1);
                    int neckY = Math.Clamp(face.Bottom, 0, mask.Rows - 1);
                    int neckH = Math.Clamp((int)(face.Height * 0.38), 1, mask.Rows - neckY);
                    if (neckW > 0 && neckH > 0)
                    {
                        Cv2.Rectangle(mask, new Rect(neckX, neckY, neckW, neckH), Scalar.Black, -1);
                    }
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

    public static void CleanMask(Mat binaryMask, int kernelSize = 5)
    {
        ArgumentNullException.ThrowIfNull(binaryMask);
        if (binaryMask.IsDisposed)
            throw new ObjectDisposedException(nameof(binaryMask));

        if (binaryMask.Empty() || binaryMask.Channels() != 1)
            throw new ArgumentException("La máscara debe estar inicializada y tener un único canal.", nameof(binaryMask));

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
        Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Close, kernel);
    }

    private static void ValidateFrame(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed)
            throw new ObjectDisposedException(nameof(frame));

        if (frame.Empty() || frame.Channels() != 3)
            throw new ArgumentException("El fotograma debe ser BGR de 3 canales.", nameof(frame));
    }
}