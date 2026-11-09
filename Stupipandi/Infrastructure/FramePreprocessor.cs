using System;
using OpenCvSharp;

namespace Stupipandi.Infrastructure;

/// <summary>
/// Proporciona operaciones optimizadas de preprocesamiento de imágenes (transformación a escala de grises,
/// ecualización de histogramas y canalizaciones integradas) diseñadas para módulos de biometría y detección de gestos.
/// Implementa una estricta gestión de memoria no administrada para mitigar cualquier riesgo de fuga en estructuras <see cref="Mat"/>.
/// </summary>
public static class FramePreprocessor
{
    /// <summary>
    /// Convierte un fotograma en formato de color estándar (BGR) a escala de grises (monocanal).
    /// </summary>
    /// <param name="source">Matriz de imagen de entrada.</param>
    /// <returns>Nueva instancia de <see cref="Mat"/> en escala de grises. El llamador asume la propiedad y debe liberarla.</returns>
    /// <exception cref="ArgumentNullException">Lanzado si <paramref name="source"/> es null o está dispuesto.</exception>
    /// <exception cref="ArgumentException">Lanzado si la matriz de entrada está vacía.</exception>
    public static Mat ConvertToGrayscale(Mat source)
    {
        ValidateInput(source, nameof(source));

        var destination = new Mat();
        try
        {
            ConvertToGrayscale(source, destination);
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Convierte un fotograma BGR a escala de grises reutilizando una matriz de destino preexistente (sin asignación de memoria nueva).
    /// </summary>
    /// <param name="source">Matriz de imagen de entrada.</param>
    /// <param name="destination">Matriz destino donde se escribirá el resultado en escala de grises.</param>
    public static void ConvertToGrayscale(Mat source, Mat destination)
    {
        ValidateInput(source, nameof(source));
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Channels() == 1)
        {
            source.CopyTo(destination);
            return;
        }

        Cv2.CvtColor(source, destination, ColorConversionCodes.BGR2GRAY);
    }

    /// <summary>
    /// Normaliza la distribución de iluminación y contraste de un fotograma en escala de grises mediante ecualización de histograma.
    /// </summary>
    /// <param name="grayscaleSource">Matriz monocanal (escala de grises) de entrada.</param>
    /// <returns>Nueva instancia de <see cref="Mat"/> ecualizada. El llamador asume la propiedad y debe liberarla.</returns>
    public static Mat EqualizeHistogram(Mat grayscaleSource)
    {
        ValidateInput(grayscaleSource, nameof(grayscaleSource));

        if (grayscaleSource.Channels() != 1)
        {
            throw new ArgumentException(
                $"[FramePreprocessor] La ecualización de histograma requiere una imagen monocanal (1 canal). Canales actuales: {grayscaleSource.Channels()}.",
                nameof(grayscaleSource));
        }

        var destination = new Mat();
        try
        {
            EqualizeHistogram(grayscaleSource, destination);
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Aplica ecualización de histograma a una imagen monocanal reutilizando una matriz de destino preexistente.
    /// </summary>
    /// <param name="grayscaleSource">Matriz monocanal de entrada.</param>
    /// <param name="destination">Matriz destino donde se escribirá el resultado ecualizado.</param>
    public static void EqualizeHistogram(Mat grayscaleSource, Mat destination)
    {
        ValidateInput(grayscaleSource, nameof(grayscaleSource));
        ArgumentNullException.ThrowIfNull(destination);

        if (grayscaleSource.Channels() != 1)
        {
            throw new ArgumentException(
                $"[FramePreprocessor] La ecualización de histograma requiere una imagen monocanal (1 canal). Canales actuales: {grayscaleSource.Channels()}.",
                nameof(grayscaleSource));
        }

        Cv2.EqualizeHist(grayscaleSource, destination);
    }

    /// <summary>
    /// Pipeline de normalización completo: transforma la imagen original (BGR) a escala de grises y aplica ecualización de histograma.
    /// Garantiza la destrucción inmediata de la matriz intermedia mediante el patrón 'using' para prevenir memory leaks en memoria no administrada.
    /// </summary>
    /// <param name="source">Fotograma original BGR proveniente de la cámara.</param>
    /// <returns>Fotograma optimizado monocanal y ecualizado listo para clasificadores Haar o descriptores LBP/biometría.</returns>
    public static Mat ProcessPipeline(Mat source)
    {
        ValidateInput(source, nameof(source));

        // Matriz intermedia en escala de grises garantizada bajo el bloque 'using'
        using Mat grayIntermediate = ConvertToGrayscale(source);

        // Retorna la matriz ecualizada final transfiriendo la propiedad al consumidor
        return EqualizeHistogram(grayIntermediate);
    }

    /// <summary>
    /// Pipeline de normalización completo reutilizando una matriz de destino provista por el llamador.
    /// </summary>
    /// <param name="source">Fotograma original BGR.</param>
    /// <param name="destination">Matriz destino final donde se escribirá la imagen en grises y ecualizada.</param>
    public static void ProcessPipeline(Mat source, Mat destination)
    {
        ValidateInput(source, nameof(source));
        ArgumentNullException.ThrowIfNull(destination);

        using Mat grayIntermediate = ConvertToGrayscale(source);
        EqualizeHistogram(grayIntermediate, destination);
    }

    private static void ValidateInput(Mat mat, string paramName)
    {
        ArgumentNullException.ThrowIfNull(mat, paramName);

        if (mat.IsDisposed)
        {
            throw new ObjectDisposedException(paramName, "[FramePreprocessor] La matriz recibida ya ha sido liberada (Disposed).");
        }

        if (mat.Empty() || mat.Cols == 0 || mat.Rows == 0)
        {
            throw new ArgumentException("[FramePreprocessor] La matriz proporcionada está vacía o no contiene datos válidos.", paramName);
        }
    }
}
