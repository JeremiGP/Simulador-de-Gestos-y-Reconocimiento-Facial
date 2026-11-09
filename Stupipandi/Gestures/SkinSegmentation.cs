using OpenCvSharp;

namespace Stupipandi.Gestures;

/// <summary>
/// Convierte fotogramas BGR en máscaras binarias de piel usando HSV o YCrCb.
/// </summary>
public static class SkinSegmentation
{
    /// <summary>
    /// Espacio de color usado para segmentar la piel.
    /// </summary>
    public enum ColorSpace
    {
        Hsv,
        YCrCb
    }

    /// <summary>
    /// Crea una máscara binaria de piel con valores 0 y 255.
    /// El llamador debe liberar la matriz devuelta.
    /// </summary>
    public static Mat CreateMask(
        Mat bgrFrame,
        ColorSpace colorSpace = ColorSpace.Hsv,
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
    /// Elimina ruido aislado y rellena pequeños huecos de la máscara.
    /// </summary>
    public static void CleanMask(Mat binaryMask, int kernelSize = 5)
    {
        ArgumentNullException.ThrowIfNull(binaryMask);
        if (binaryMask.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(binaryMask));
        }

        if (binaryMask.Empty() || binaryMask.Channels() != 1)
        {
            throw new ArgumentException(
                "La máscara debe estar inicializada y tener un único canal.",
                nameof(binaryMask));
        }

        if (kernelSize < 3 || kernelSize % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kernelSize),
                kernelSize,
                "El tamaño del kernel debe ser impar y mayor o igual que 3.");
        }

        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse,
            new Size(kernelSize, kernelSize));

        Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(binaryMask, binaryMask, MorphTypes.Close, kernel);
    }

    private static (Scalar Lower, Scalar Upper) GetSkinRange(ColorSpace colorSpace)
    {
        return colorSpace switch
        {
            ColorSpace.Hsv =>
                (new Scalar(0, 30, 60), new Scalar(25, 180, 255)),
            ColorSpace.YCrCb =>
                (new Scalar(0, 133, 77), new Scalar(255, 173, 127)),
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace))
        };
    }

    private static void ValidateFrame(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(frame));
        }

        if (frame.Empty() || frame.Channels() != 3)
        {
            throw new ArgumentException(
                "El fotograma debe ser BGR, estar inicializado y tener tres canales.",
                nameof(frame));
        }
    }
}
