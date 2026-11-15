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
/// Distance es la distancia RMS entre vectores; cuanto menor, mayor similitud.
/// </summary>
public sealed record FaceRecognitionResult(
    bool IsRecognized,
    string Label,
    double Distance,
    bool ModelIsReady);

/// <summary>
/// Reconocedor sencillo por distancia vectorial.
/// No necesita OpenCvSharp.Face/LBPH: normaliza cada cara a un vector de píxeles
/// y compara la distancia RMS con las muestras enroladas.
/// </summary>
public sealed class FaceRecognizer
{
    private const int FaceWidth       = 64;
    private const int FaceHeight      = 64;
    private const int FeatureLength   = FaceWidth * FaceHeight;
    private const int CurrentModelVersion = 1;

    private readonly List<FaceSample> _samples = new();

    public FaceRecognizer(double threshold = 52.0)
    {
        if (threshold <= 0 || double.IsNaN(threshold) || double.IsInfinity(threshold))
            throw new ArgumentOutOfRangeException(nameof(threshold), "El umbral debe ser un número positivo.");

        Threshold = threshold;
    }

    /// <summary>
    /// Distancia máxima para considerar que una muestra corresponde a una persona.
    /// </summary>
    public double Threshold { get; set; }

    public bool IsTrained   => _samples.Count > 0;
    public int  SampleCount => _samples.Count;

    /// <summary>
    /// Añade una muestra. faceImage debe contener principalmente el rostro,
    /// no el frame completo de la cámara.
    /// </summary>
    public void AddSample(Mat faceImage, string personName)
    {
        ArgumentNullException.ThrowIfNull(faceImage);

        string cleanName = personName?.Trim() ?? string.Empty;
        if (cleanName.Length == 0)
            throw new ArgumentException("El nombre de la persona no puede estar vacío.", nameof(personName));
        if (faceImage.Empty())
            throw new ArgumentException("La imagen del rostro está vacía.", nameof(faceImage));

        byte[] features = ExtractFeatures(faceImage);
        _samples.Add(new FaceSample(cleanName, features));
    }

    /// <summary>
    /// Recorta un rostro desde el frame y lo añade como muestra de entrenamiento.
    /// </summary>
    public void AddSampleFromFrame(Mat frame, Rect faceBounds, string personName)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
            throw new ArgumentException("El frame está vacío.", nameof(frame));

        Rect safeFace = ClipRectangle(faceBounds, frame.Cols, frame.Rows);
        if (safeFace.Width <= 0 || safeFace.Height <= 0)
            throw new ArgumentException("El rectángulo facial queda fuera del frame.", nameof(faceBounds));

        using Mat faceCrop = frame[safeFace].Clone();
        AddSample(faceCrop, personName);
    }

    /// <summary>
    /// Compara una cara con todas las muestras y devuelve la coincidencia más cercana.
    /// </summary>
    public FaceRecognitionResult Predict(Mat faceImage)
    {
        ArgumentNullException.ThrowIfNull(faceImage);

        if (!IsTrained)
            return new FaceRecognitionResult(false, "Modelo sin entrenar", double.PositiveInfinity, false);
        if (faceImage.Empty())
            return new FaceRecognitionResult(false, "Desconocido", double.PositiveInfinity, true);

        byte[] currentFeatures = ExtractFeatures(faceImage);
        double bestDistance    = double.PositiveInfinity;
        string bestLabel       = "Desconocido";

        foreach (FaceSample sample in _samples)
        {
            double distance = CalculateRmsDistance(currentFeatures, sample.Features);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestLabel    = sample.Label;
            }
        }

        bool recognized = bestDistance <= Threshold;
        return new FaceRecognitionResult(
            recognized,
            recognized ? bestLabel : "Desconocido",
            bestDistance,
            true);
    }

    /// <summary>
    /// Guarda las muestras en la ruta indicada (XML serializado).
    /// </summary>
    public void Save(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Indica la ruta del modelo.", nameof(modelPath));
        if (!IsTrained)
            throw new InvalidOperationException("No hay muestras; añade al menos un rostro antes de guardar el modelo.");

        string fullPath  = Path.GetFullPath(modelPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var document = new FaceModelDocument
        {
            Version = CurrentModelVersion,
            Samples = _samples.Select(sample => new FaceSampleDocument
            {
                Label    = sample.Label,
                Features = sample.Features
            }).ToList()
        };

        var serializer = new XmlSerializer(typeof(FaceModelDocument));
        using FileStream stream = File.Create(fullPath);
        serializer.Serialize(stream, document);
    }

    /// <summary>
    /// Carga el modelo. Devuelve false si el archivo todavía no existe.
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
        using Mat gray     = FaceImageTools.ToGray(faceImage);
        using var equalized = new Mat();
        using var resized   = new Mat();

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
            sumOfSquares  += (long)difference * difference;
        }
        return Math.Sqrt(sumOfSquares / (double)FeatureLength);
    }

    private static Rect ClipRectangle(Rect rectangle, int imageWidth, int imageHeight)
    {
        int x      = Math.Clamp(rectangle.X,      0, imageWidth);
        int y      = Math.Clamp(rectangle.Y,      0, imageHeight);
        int width  = Math.Clamp(rectangle.Width,  0, imageWidth  - x);
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

        // XmlSerializer almacena byte[] como Base64 dentro del XML.
        [XmlElement("Features")]
        public byte[] Features { get; set; } = Array.Empty<byte>();
    }
}

/// <summary>
/// Conversión común a escala de grises para el módulo biométrico.
/// </summary>
internal static class FaceImageTools
{
    public static Mat ToGray(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new ArgumentException("La imagen está vacía.", nameof(source));

        var gray = new Mat();
        switch (source.Channels())
        {
            case 1: source.CopyTo(gray); break;
            case 3: Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);  break;
            case 4: Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY); break;
            default:
                gray.Dispose();
                throw new ArgumentException("La imagen debe tener 1, 3 o 4 canales.", nameof(source));
        }

        return gray;
    }
}
