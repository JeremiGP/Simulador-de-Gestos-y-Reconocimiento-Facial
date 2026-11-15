using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using OpenCvSharp;

namespace Stupipandi.Biometrics;

/// <summary>
/// Resultado de la comparación de un rostro con las muestras guardadas.
/// </summary>
public sealed record FaceRecognitionResult(
    bool IsRecognized,
    string Label,
    double Distance,
    bool ModelIsReady,
    int ConfidencePercent = 0);

/// <summary>
/// Reconocedor biométrico facial basado en comparación vectorial normalizada (RMS).
/// Extrae descriptores dimensionales normalizados en escala de grises y ecualización de histograma
/// para comparar la distancia euclidiana cuadrática media contra las plantillas enroladas.
/// </summary>
public sealed class FaceRecognizer
{
    private const int FaceWidth = 64;
    private const int FaceHeight = 64;
    private const int FeatureLength = FaceWidth * FaceHeight;
    private const int CurrentModelVersion = 1;

    private readonly List<FaceSample> _samples = new();

    /// <summary>
    /// Distancia máxima para considerar que una muestra corresponde a una persona.
    /// </summary>
    public double Threshold { get; set; }

    /// <summary>
    /// Indica si el modelo contiene al menos una plantilla biométrica válida.
    /// </summary>
    public bool IsTrained => _samples.Count > 0;

    /// <summary>
    /// Cantidad total de muestras enroladas en memoria.
    /// </summary>
    public int SampleCount => _samples.Count;

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="FaceRecognizer"/>.
    /// </summary>
    /// <param name="threshold">Umbral de distancia RMS base para aceptación biométrica.</param>
    public FaceRecognizer(double threshold = 52.0)
    {
        if (threshold <= 0 || double.IsNaN(threshold) || double.IsInfinity(threshold))
            throw new ArgumentOutOfRangeException(nameof(threshold), "El umbral debe ser un número positivo.");

        Threshold = threshold;
    }

    /// <summary>
    /// Añade una muestra facial asociada a una identidad al conjunto de datos.
    /// </summary>
    public void AddSample(Mat faceImage, string personName)
    {
        ArgumentNullException.ThrowIfNull(faceImage);
        if (faceImage.IsDisposed)
            throw new ObjectDisposedException(nameof(faceImage));

        string cleanName = personName?.Trim() ?? string.Empty;
        if (cleanName.Length == 0)
            throw new ArgumentException("El nombre de la persona no puede estar vacío.", nameof(personName));
        if (faceImage.Empty())
            throw new ArgumentException("La imagen del rostro está vacía.", nameof(faceImage));

        byte[] features = ExtractFeatures(faceImage);
        _samples.Add(new FaceSample(cleanName, features));
    }

    /// <summary>
    /// Recorta un rostro desde el fotograma completo y lo añade como muestra de entrenamiento.
    /// </summary>
    public void AddSampleFromFrame(Mat frame, Rect faceBounds, string personName)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed)
            throw new ObjectDisposedException(nameof(frame));
        if (frame.Empty())
            throw new ArgumentException("El frame está vacío.", nameof(frame));

        Rect safeFace = ClipRectangle(faceBounds, frame.Cols, frame.Rows);
        if (safeFace.Width <= 0 || safeFace.Height <= 0)
            throw new ArgumentException("El rectángulo facial queda fuera del frame.", nameof(faceBounds));

        using Mat faceCrop = frame[safeFace].Clone();
        AddSample(faceCrop, personName);
    }

    /// <summary>
    /// Compara la imagen de entrada con todas las plantillas registradas y devuelve la coincidencia más cercana.
    /// </summary>
    public FaceRecognitionResult Predict(Mat faceImage)
    {
        ArgumentNullException.ThrowIfNull(faceImage);
        if (faceImage.IsDisposed || faceImage.Empty())
            return new FaceRecognitionResult(false, "Desconocido", double.PositiveInfinity, IsTrained, 0);

        if (!IsTrained)
            return new FaceRecognitionResult(false, "Modelo sin entrenar", double.PositiveInfinity, false, 0);

        byte[] currentFeatures = ExtractFeatures(faceImage);
        double bestDistance = double.PositiveInfinity;
        string bestLabel = "Desconocido";

        foreach (FaceSample sample in _samples)
        {
            double distance = CalculateRmsDistance(currentFeatures, sample.Features);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestLabel = sample.Label;
            }
        }

        bool recognized = bestDistance <= Threshold;
        int confidence = recognized
            ? (int)Math.Clamp((1.0 - (bestDistance / Threshold)) * 100.0 + 10.0, 10.0, 100.0)
            : 0;

        return new FaceRecognitionResult(
            recognized,
            recognized ? bestLabel : "Desconocido",
            bestDistance,
            true,
            confidence);
    }

    /// <summary>
    /// Guarda las muestras registradas en formato XML en el archivo especificado.
    /// </summary>
    public void Save(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Indica la ruta del modelo.", nameof(modelPath));
        if (!IsTrained)
            throw new InvalidOperationException("No hay muestras; añade al menos un rostro antes de guardar el modelo.");

        string fullPath = Path.GetFullPath(modelPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var document = new FaceModelDocument
        {
            Version = CurrentModelVersion,
            Samples = _samples.Select(sample => new FaceSampleDocument
            {
                Label = sample.Label,
                Features = sample.Features
            }).ToList()
        };

        var serializer = new XmlSerializer(typeof(FaceModelDocument));
        using FileStream stream = File.Create(fullPath);
        serializer.Serialize(stream, document);
    }

    /// <summary>
    /// Carga las muestras biométricas desde un archivo XML previo. Devuelve false si el archivo no existe.
    /// </summary>
    public bool Load(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Indica la ruta del modelo.", nameof(modelPath));
        if (!File.Exists(modelPath))
            return false;

        var serializer = new XmlSerializer(typeof(FaceModelDocument));
        using FileStream stream = File.OpenRead(modelPath);
        var document = serializer.Deserialize(stream) as FaceModelDocument;

        if (document is null || document.Version != CurrentModelVersion)
            throw new InvalidDataException("El archivo face_model.xml no tiene un formato o versión compatible.");
        if (document.Samples is null || document.Samples.Count == 0)
            throw new InvalidDataException("El modelo no contiene muestras faciales.");

        var loadedSamples = new List<FaceSample>();
        foreach (FaceSampleDocument sample in document.Samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Label) || sample.Features is null || sample.Features.Length != FeatureLength)
                throw new InvalidDataException("El modelo contiene una muestra facial dañada o incompleta.");

            loadedSamples.Add(new FaceSample(sample.Label, sample.Features));
        }

        _samples.Clear();
        _samples.AddRange(loadedSamples);
        return true;
    }

    private static byte[] ExtractFeatures(Mat faceImage)
    {
        using Mat gray = FaceImageTools.ToGray(faceImage);
        using var equalized = new Mat();
        using var resized = new Mat();

        Cv2.EqualizeHist(gray, equalized);
        Cv2.Resize(equalized, resized, new Size(FaceWidth, FaceHeight), 0, 0, InterpolationFlags.Area);

        var features = new byte[FeatureLength];
        Marshal.Copy(resized.Data, features, 0, features.Length);
        return features;
    }

    private static double CalculateRmsDistance(byte[] first, byte[] second)
    {
        long sumOfSquares = 0;
        for (int i = 0; i < FeatureLength; i++)
        {
            int difference = first[i] - second[i];
            sumOfSquares += (long)difference * difference;
        }
        return Math.Sqrt(sumOfSquares / (double)FeatureLength);
    }

    private static Rect ClipRectangle(Rect rectangle, int imageWidth, int imageHeight)
    {
        int x = Math.Clamp(rectangle.X, 0, imageWidth);
        int y = Math.Clamp(rectangle.Y, 0, imageHeight);
        int width = Math.Clamp(rectangle.Width, 0, imageWidth - x);
        int height = Math.Clamp(rectangle.Height, 0, imageHeight - y);
        return new Rect(x, y, width, height);
    }

    private sealed record FaceSample(string Label, byte[] Features);

    [XmlRoot("FaceModel")]
    public sealed class FaceModelDocument
    {
        [XmlAttribute("version")]
        public int Version { get; set; }

        [XmlElement("Sample")]
        public List<FaceSampleDocument> Samples { get; set; } = new();
    }

    public sealed class FaceSampleDocument
    {
        [XmlAttribute("label")]
        public string Label { get; set; } = string.Empty;

        [XmlElement("Features")]
        public byte[] Features { get; set; } = Array.Empty<byte>();
    }
}

/// <summary>
/// Conversión auxiliar de matrices de imagen a escala de grises.
/// </summary>
internal static class FaceImageTools
{
    public static Mat ToGray(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed)
            throw new ObjectDisposedException(nameof(source));
        if (source.Empty())
            throw new ArgumentException("La imagen está vacía.", nameof(source));

        var gray = new Mat();
        switch (source.Channels())
        {
            case 1:
                source.CopyTo(gray);
                break;
            case 3:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                gray.Dispose();
                throw new ArgumentException("La imagen debe tener 1, 3 o 4 canales.", nameof(source));
        }

        return gray;
    }
}
