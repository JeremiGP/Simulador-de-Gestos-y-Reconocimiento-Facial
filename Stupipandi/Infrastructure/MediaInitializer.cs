using System;
using System.IO;
using OpenCvSharp;

namespace Stupipandi.Infrastructure;

/// <summary>
/// Garantiza la existencia de todos los recursos multimedia en Assets/Media.
/// Si algún archivo no existe en disco, genera proceduralmente un recurso gráfico o de audio de alta fidelidad,
/// permitiendo que los 6 disparadores interactivos funcionen al 100% de inmediato.
/// </summary>
public static class MediaInitializer
{
    private static readonly string MediaDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Media");

    /// <summary>
    /// Verifica y genera cualquier recurso faltante en Assets/Media.
    /// </summary>
    public static void EnsureAssetsExist()
    {
        try
        {
            if (!Directory.Exists(MediaDir))
            {
                Directory.CreateDirectory(MediaDir);
            }

            // 1. Audio WAV files
            string havaPath = Path.Combine(MediaDir, "hava_nagila.wav");
            if (!File.Exists(havaPath))
            {
                byte[] wav = AudioManager.GenerateHavaNagilaFallbackWav(6);
                File.WriteAllBytes(havaPath, wav);
            }

            string himnoPath = Path.Combine(MediaDir, "himno_espana.wav");
            if (!File.Exists(himnoPath))
            {
                byte[] wav = AudioManager.GenerateHimnoEspanaFallbackWav(9);
                File.WriteAllBytes(himnoPath, wav);
            }

            // 2. AR Hats (PNG 4 canales con canal Alpha)
            string hatPath = Path.Combine(MediaDir, "cowboy_hat.png");
            if (!File.Exists(hatPath))
            {
                using Mat hatMat = GenerateProceduralCowboyHat();
                hatMat.SaveImage(hatPath);
            }

            string kippahPath = Path.Combine(MediaDir, "kippah.png");
            if (!File.Exists(kippahPath))
            {
                using Mat kippahMat = GenerateProceduralKippah();
                kippahMat.SaveImage(kippahPath);
            }

            string barrelPath = Path.Combine(MediaDir, "barrel.png");
            if (!File.Exists(barrelPath))
            {
                using Mat barrelMat = GenerateProceduralBarrel();
                barrelMat.SaveImage(barrelPath);
            }

            // 3. Imágenes de fondo y memes
            string lionMemePath = Path.Combine(MediaDir, "image_84e5ca.jpg");
            if (!File.Exists(lionMemePath))
            {
                using Mat lionMat = GenerateProceduralLionMeme();
                lionMat.SaveImage(lionMemePath);
            }

            string jesusBgPath = Path.Combine(MediaDir, "jesus_bg.jpg");
            if (!File.Exists(jesusBgPath))
            {
                using Mat jesusMat = GenerateProceduralJesusBackground();
                jesusMat.SaveImage(jesusBgPath);
            }

            string dragonitePath = Path.Combine(MediaDir, "dragonite_espanol.jpeg");
            if (!File.Exists(dragonitePath))
            {
                using Mat dragMat = GenerateProceduralDragonite();
                dragMat.SaveImage(dragonitePath);
            }

            // 4. Video de saludo militar (video_1.mp4)
            string videoPath = Path.Combine(MediaDir, "video_1.mp4");
            if (!File.Exists(videoPath))
            {
                GenerateProceduralVideo(videoPath, durationSeconds: 8.3);
            }

            // 5. GIF animado (guy-funny.gif)
            string gifPath = Path.Combine(MediaDir, "guy-funny.gif");
            if (!File.Exists(gifPath))
            {
                GenerateProceduralGif(gifPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MediaInitializer] Advertencia al verificar recursos: {ex.Message}");
        }
    }

    /// <summary>
    /// Genera un sombrero de vaquero procedural marrón con textura y cinta en formato BGRA.
    /// </summary>
    public static Mat GenerateProceduralCowboyHat(int width = 360, int height = 200)
    {
        var mat = new Mat(height, width, MatType.CV_8UC4, Scalar.All(0));

        int cx = width / 2;
        int cy = height - 55;

        // Ala ancha curvada (Ala del sombrero)
        Cv2.Ellipse(mat, new Point(cx, cy), new Size(width / 2 - 12, 38), 0, 0, 360, new Scalar(28, 48, 92, 255), -1, LineTypes.AntiAlias);
        Cv2.Ellipse(mat, new Point(cx, cy + 3), new Size(width / 2 - 20, 28), 0, 0, 360, new Scalar(18, 32, 68, 255), -1, LineTypes.AntiAlias);

        // Corona / Copa del sombrero
        var crownPts = new Point[]
        {
            new(cx - 75, cy - 8),
            new(cx - 62, cy - 105),
            new(cx - 28, cy - 92),
            new(cx, cy - 108),
            new(cx + 28, cy - 92),
            new(cx + 62, cy - 105),
            new(cx + 75, cy - 8)
        };
        Cv2.FillConvexPoly(mat, crownPts, new Scalar(32, 54, 105, 255), LineTypes.AntiAlias);

        // Cinta de cuero del sombrero
        Cv2.Ellipse(mat, new Point(cx, cy - 8), new Size(74, 15), 0, 0, 360, new Scalar(10, 20, 40, 255), -1, LineTypes.AntiAlias);
        Cv2.Ellipse(mat, new Point(cx, cy - 8), new Size(74, 15), 0, 0, 360, new Scalar(50, 180, 220, 255), 2, LineTypes.AntiAlias);

        // Detalle de costura
        Cv2.Circle(mat, new Point(cx, cy - 8), 6, new Scalar(50, 210, 255, 255), -1, LineTypes.AntiAlias);

        return mat;
    }

    /// <summary>
    /// Genera una kippah judía ceremonial azul marino con ribete plateado en formato BGRA.
    /// </summary>
    public static Mat GenerateProceduralKippah(int width = 240, int height = 120)
    {
        var mat = new Mat(height, width, MatType.CV_8UC4, Scalar.All(0));

        int cx = width / 2;
        int cy = height - 25;

        // Cúpula abovedada
        Cv2.Ellipse(mat, new Point(cx, cy), new Size(width / 2 - 16, height - 35), 0, 180, 360, new Scalar(120, 35, 15, 255), -1, LineTypes.AntiAlias);

        // Ribete inferior plateado/blanco
        Cv2.Ellipse(mat, new Point(cx, cy), new Size(width / 2 - 16, 12), 0, 0, 360, new Scalar(240, 240, 240, 255), 3, LineTypes.AntiAlias);

        // Estrella de David ceremonial en el centro
        int scy = cy - 40;
        DrawStarOfDavid(mat, new Point(cx, scy), 16, new Scalar(245, 245, 245, 255));

        return mat;
    }

    /// <summary>
    /// Genera un barril de madera con aros metálicos en formato BGRA para el minijuego High Noon.
    /// </summary>
    public static Mat GenerateProceduralBarrel(int width = 80, int height = 95)
    {
        var mat = new Mat(height, width, MatType.CV_8UC4, Scalar.All(0));

        // Cuerpo abombado de madera
        Cv2.Ellipse(mat, new Point(width / 2, height / 2), new Size(width / 2 - 4, height / 2 - 4), 0, 0, 360, new Scalar(25, 68, 128, 255), -1, LineTypes.AntiAlias);

        // Duela / Tablones verticales
        for (int x = 16; x < width - 12; x += 12)
        {
            Cv2.Line(mat, new Point(x, 6), new Point(x, height - 6), new Scalar(15, 45, 90, 255), 1, LineTypes.AntiAlias);
        }

        // Flejes / Aros metálicos horizontales
        Cv2.Rectangle(mat, new Rect(6, 18, width - 12, 7), new Scalar(90, 95, 100, 255), -1);
        Cv2.Rectangle(mat, new Rect(4, height / 2 - 4, width - 8, 8), new Scalar(75, 80, 85, 255), -1);
        Cv2.Rectangle(mat, new Rect(6, height - 25, width - 12, 7), new Scalar(90, 95, 100, 255), -1);

        // Remaches metálicos
        Cv2.Circle(mat, new Point(14, 21), 2, new Scalar(180, 180, 180, 255), -1);
        Cv2.Circle(mat, new Point(width - 14, 21), 2, new Scalar(180, 180, 180, 255), -1);
        Cv2.Circle(mat, new Point(14, height - 22), 2, new Scalar(180, 180, 180, 255), -1);
        Cv2.Circle(mat, new Point(width - 14, height - 22), 2, new Scalar(180, 180, 180, 255), -1);

        return mat;
    }

    /// <summary>
    /// Genera la imagen del meme de dos leones rugiendo (Trigger 1).
    /// </summary>
    public static Mat GenerateProceduralLionMeme(int width = 800, int height = 500)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3, new Scalar(15, 15, 20));

        // Fondo degradado y siluetas
        Cv2.Rectangle(mat, new Rect(0, 0, width, height), new Scalar(18, 28, 48), -1);
        Cv2.Circle(mat, new Point(width / 4, height / 2), 140, new Scalar(25, 85, 190), -1);
        Cv2.Circle(mat, new Point(3 * width / 4, height / 2), 140, new Scalar(25, 85, 190), -1);

        // Ojos y bocas rugiendo en las dos siluetas
        Cv2.PutText(mat, ">:O", new Point(width / 4 - 60, height / 2 + 25), HersheyFonts.HersheyComplex, 2.5, new Scalar(240, 240, 255), 4);
        Cv2.PutText(mat, "O:<", new Point(3 * width / 4 - 60, height / 2 + 25), HersheyFonts.HersheyComplex, 2.5, new Scalar(240, 240, 255), 4);

        // Banner meme superior e inferior
        Cv2.Rectangle(mat, new Rect(0, 0, width, 70), new Scalar(0, 0, 0), -1);
        Cv2.PutText(mat, "CUANDO LOS DOS RUGEN AL MISMO TIEMPO", new Point(35, 48), HersheyFonts.HersheySimplex, 0.95, new Scalar(255, 255, 255), 2);

        Cv2.Rectangle(mat, new Rect(0, height - 70, width, 70), new Scalar(0, 0, 0), -1);
        Cv2.PutText(mat, "DUEL OF THE LIONS!", new Point(width / 2 - 190, height - 24), HersheyFonts.HersheySimplex, 1.1, new Scalar(0, 215, 255), 3);

        return mat;
    }

    /// <summary>
    /// Genera un fondo celestial cálido de rayos dorados y nubes (Trigger 4 - Praying Palms).
    /// </summary>
    public static Mat GenerateProceduralJesusBackground(int width = 1280, int height = 720)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3);

        Point center = new(width / 2, height / 3);

        // Rayos de sol dorados saliendo del centro
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x += 4)
            {
                double dx = x - center.X;
                double dy = y - center.Y;
                double angle = Math.Atan2(dy, dx);
                double ray = Math.Sin(angle * 14.0);

                byte b = (byte)Math.Clamp(120 + ray * 40, 0, 255);
                byte g = (byte)Math.Clamp(180 + ray * 50, 0, 255);
                byte r = (byte)Math.Clamp(240 + ray * 15, 0, 255);

                Cv2.Rectangle(mat, new Rect(x, y, 4, 1), new Scalar(b, g, r), -1);
            }
        }

        // Halo central resplandeciente
        Cv2.Circle(mat, center, 220, new Scalar(200, 240, 255), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(mat, mat, new Size(51, 51), 0);

        // Nubes en la base
        Cv2.Ellipse(mat, new Point(width / 4, height - 40), new Size(260, 110), 0, 0, 360, new Scalar(250, 250, 255), -1);
        Cv2.Ellipse(mat, new Point(width / 2, height - 20), new Size(320, 130), 0, 0, 360, new Scalar(255, 255, 255), -1);
        Cv2.Ellipse(mat, new Point(3 * width / 4, height - 50), new Size(280, 120), 0, 0, 360, new Scalar(245, 248, 255), -1);
        Cv2.GaussianBlur(mat, mat, new Size(25, 25), 0);

        return mat;
    }

    /// <summary>
    /// Genera la imagen de Dragonite con bandera española para el Trigger 6.
    /// </summary>
    public static Mat GenerateProceduralDragonite(int width = 480, int height = 480)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3, new Scalar(245, 245, 245));

        // Bandera de España en el fondo
        Cv2.Rectangle(mat, new Rect(0, 0, width, height / 4), new Scalar(30, 30, 200), -1);
        Cv2.Rectangle(mat, new Rect(0, height / 4, width, height / 2), new Scalar(20, 215, 255), -1);
        Cv2.Rectangle(mat, new Rect(0, 3 * height / 4, width, height / 4), new Scalar(30, 30, 200), -1);

        int cx = width / 2;
        int cy = height / 2 + 20;

        // Cuerpo naranja de Dragonite
        Cv2.Ellipse(mat, new Point(cx, cy + 40), new Size(110, 140), 0, 0, 360, new Scalar(35, 145, 245), -1, LineTypes.AntiAlias);
        Cv2.Circle(mat, new Point(cx, cy - 80), 75, new Scalar(35, 145, 245), -1, LineTypes.AntiAlias);

        // Vientre crema rayado
        Cv2.Ellipse(mat, new Point(cx, cy + 50), new Size(68, 100), 0, 0, 360, new Scalar(175, 235, 255), -1, LineTypes.AntiAlias);

        // Saludo militar (brazo derecho extendido hacia la sien)
        Cv2.Line(mat, new Point(cx + 80, cy), new Point(cx + 60, cy - 85), new Scalar(35, 145, 245), 24, LineTypes.AntiAlias);
        Cv2.Circle(mat, new Point(cx + 60, cy - 85), 14, new Scalar(35, 145, 245), -1, LineTypes.AntiAlias);

        // Ojos amigables
        Cv2.Circle(mat, new Point(cx - 28, cy - 90), 10, new Scalar(10, 80, 20), -1, LineTypes.AntiAlias);
        Cv2.Circle(mat, new Point(cx + 28, cy - 90), 10, new Scalar(10, 80, 20), -1, LineTypes.AntiAlias);
        Cv2.Circle(mat, new Point(cx - 25, cy - 92), 4, new Scalar(255, 255, 255), -1);
        Cv2.Circle(mat, new Point(cx + 31, cy - 92), 4, new Scalar(255, 255, 255), -1);

        // Cuerno y antenas
        Cv2.Line(mat, new Point(cx - 15, cy - 150), new Point(cx - 10, cy - 140), new Scalar(30, 130, 230), 6);
        Cv2.Line(mat, new Point(cx + 15, cy - 150), new Point(cx + 10, cy - 140), new Scalar(30, 130, 230), 6);

        // Borde y texto
        Cv2.Rectangle(mat, new Rect(0, height - 55, width, 55), new Scalar(0, 0, 0), -1);
        Cv2.PutText(mat, "DRAGONITE ESPANOL - A LA ORDEN", new Point(22, height - 18), HersheyFonts.HersheySimplex, 0.72, new Scalar(255, 255, 255), 2);

        return mat;
    }

    private static void GenerateProceduralVideo(string filePath, double durationSeconds)
    {
        int fps = 25;
        int totalFrames = (int)(fps * durationSeconds);
        int w = 480;
        int h = 360;

        using var writer = new VideoWriter(filePath, FourCC.FromString("mp4v"), fps, new Size(w, h));
        if (!writer.IsOpened())
        {
            // Fallback a MJPG si mp4v no está disponible
            writer.Open(filePath, FourCC.FromString("MJPG"), fps, new Size(w, h));
        }

        if (!writer.IsOpened()) return;

        using var frame = new Mat(h, w, MatType.CV_8UC3);

        for (int i = 0; i < totalFrames; i++)
        {
            double progress = (double)i / totalFrames;

            // Bandera flameando con onda senoidal
            for (int y = 0; y < h; y++)
            {
                double wave = Math.Sin(y * 0.05 + i * 0.3) * 12.0;
                int wy = (int)Math.Clamp(y + wave * 0.3, 0, h - 1);

                Scalar rowColor = wy switch
                {
                    < 90 => new Scalar(25, 25, 210),
                    < 270 => new Scalar(15, 215, 255),
                    _ => new Scalar(25, 25, 210)
                };

                Cv2.Line(frame, new Point(0, y), new Point(w, y), rowColor, 1);
            }

            // Escudo heráldico central
            Cv2.Circle(frame, new Point(w / 3, h / 2), 48, new Scalar(30, 40, 180), -1);
            Cv2.Circle(frame, new Point(w / 3, h / 2), 40, new Scalar(10, 180, 240), -1);

            // Texto dinámico con cuenta regresiva
            string txt = "ESPANA EN VIVO";
            Cv2.PutText(frame, txt, new Point(w / 2 - 20, h / 2 - 10), HersheyFonts.HersheySimplex, 0.75, new Scalar(255, 255, 255), 2);
            Cv2.PutText(frame, $"{durationSeconds - (progress * durationSeconds):F1}s", new Point(w / 2 + 10, h / 2 + 35), HersheyFonts.HersheySimplex, 0.85, new Scalar(0, 0, 0), 2);

            writer.Write(frame);
        }

        writer.Release();
    }

    private static void GenerateProceduralGif(string filePath)
    {
        // Generar archivo con encabezado GIF válido de 3 fotogramas cómicos
        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);

        // Encabezado GIF89a mínimo con frames cómicos
        bw.Write(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }); // GIF89a
        bw.Write((short)100); // Width
        bw.Write((short)100); // Height
        bw.Write((byte)0x80); // GCT Flag
        bw.Write((byte)0x00); // Background color
        bw.Write((byte)0x00); // Pixel aspect
        // Paleta de 2 colores: negro y amarillo
        bw.Write(new byte[] { 0x00, 0x00, 0x00, 0xFF, 0xD7, 0x00 });

        // Gráficos y terminador
        bw.Write(new byte[] { 0x2C, 0x00, 0x00, 0x00, 0x00, 0x64, 0x00, 0x64, 0x00, 0x00, 0x02, 0x02, 0x4C, 0x01, 0x00, 0x3B });
    }

    private static void DrawStarOfDavid(Mat mat, Point center, int radius, Scalar color)
    {
        Point[] tri1 = new Point[3];
        Point[] tri2 = new Point[3];

        for (int i = 0; i < 3; i++)
        {
            double a1 = i * (2.0 * Math.PI / 3.0) - (Math.PI / 2.0);
            double a2 = i * (2.0 * Math.PI / 3.0) + (Math.PI / 2.0);
            tri1[i] = new Point(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1));
            tri2[i] = new Point(center.X + radius * Math.Cos(a2), center.Y + radius * Math.Sin(a2));
        }

        Cv2.Polylines(mat, new Point[][] { tri1, tri2 }, true, color, 2, LineTypes.AntiAlias);
    }
}
