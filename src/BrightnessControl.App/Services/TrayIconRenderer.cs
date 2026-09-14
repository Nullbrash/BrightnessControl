using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BrightnessControl.App.Services;

public enum TrayIconDesign
{
    Spokes,
    DotRays,
    ThinRays,
    TwinHorizon,
    DotSunrise,
    TwinHorizonCapsule,
}

// Иконки трея больше не статичные .ico-ресурсы — форма, цвет и масштаб теперь
// независимые параметры (FP8), рисуются на лету через GDI+ (System.Drawing,
// уже зависимость проекта — TrayService и так использует System.Drawing.Icon).
// Координаты дизайнов — те же, что были выверены через Artifact-прототипы, но
// теперь параметризованы: Scale() масштабирует любую точку от центра холста
// (32,32). Заливка везде плоская, одним цветом — версия с радиальным бликом
// (света/тень из одного базового цвета) была снята: на белом ещё туда-сюда, но
// на красном/жёлтом смесь высветленного/затемнённого тона поверх плоских спиц
// уже не читалась как "просто красная иконка", а выглядела нездорово-пятнистой.
public static class TrayIconRenderer
{
    private const int NativeSize = 32;
    private const float ViewBox = 64f;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Render(TrayIconDesign design, Color baseColor, int scalePercent)
    {
        using var bmp = RenderBitmap(design, baseColor, scalePercent, NativeSize);

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            using var ms = new MemoryStream();
            temp.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    // FP7 — статический многоразмерный .ico для файла exe/ярлыков (Пуск,
    // Рабочий стол): в отличие от Render() (один размер 32px, только для
    // живой иконки в трее через Shell_NotifyIcon), Explorer/ярлыкам нужно
    // НЕСКОЛЬКО разрешений в одном файле, иначе крупные значки (рабочий
    // стол, "Все приложения" плиткой) выглядят растянуто/размыто. Формат
    // .ico поддерживает PNG-кадры начиная с Vista — упаковываем через
    // System.Drawing.Bitmap.Save(..., ImageFormat.Png) для каждого размера,
    // не через устаревший BMP+AND-маску. Не вызывается из обычного потока
    // приложения — одноразовая генерация Assets/app.ico при сборке (см.
    // README/комментарий у ApplicationIcon в .csproj).
    public static byte[] RenderIcoBytes(TrayIconDesign design, Color baseColor, int scalePercent, int[] sizes)
    {
        var pngFrames = new byte[sizes.Length][];
        for (var i = 0; i < sizes.Length; i++)
        {
            using var bmp = RenderBitmap(design, baseColor, scalePercent, sizes[i]);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngFrames[i] = ms.ToArray();
        }

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0); // reserved
            writer.Write((ushort)1); // type = icon
            writer.Write((ushort)sizes.Length);

            var dataOffset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                var size = sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0); // color palette — none (32bpp)
                writer.Write((byte)0); // reserved
                writer.Write((ushort)1); // color planes
                writer.Write((ushort)32); // bits per pixel
                writer.Write((uint)pngFrames[i].Length);
                writer.Write((uint)dataOffset);
                dataOffset += pngFrames[i].Length;
            }

            foreach (var frame in pngFrames)
            {
                writer.Write(frame);
            }
        }

        return output.ToArray();
    }

    public static Avalonia.Media.Imaging.Bitmap RenderPreview(TrayIconDesign design, Color baseColor, int scalePercent, int pixelSize = 32)
    {
        using var bmp = RenderBitmap(design, baseColor, scalePercent, pixelSize);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(ms);
    }

    // FP11 — пользовательская растровая иконка (см. TraySettings.CustomTrayIcons):
    // без параметрического цвета, только масштаб (тот же диапазон 100-170%, что
    // и у встроенных векторных форм) — картинка рисуется "как есть".
    public static Icon RenderCustom(string filePath, int scalePercent)
    {
        using var bmp = RenderCustomBitmap(filePath, scalePercent, NativeSize);

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            using var ms = new MemoryStream();
            temp.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static Avalonia.Media.Imaging.Bitmap RenderCustomPreview(string filePath, int scalePercent, int pixelSize = 32)
    {
        using var bmp = RenderCustomBitmap(filePath, scalePercent, pixelSize);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(ms);
    }

    private static Bitmap RenderCustomBitmap(string filePath, int scalePercent, int pixelSize)
    {
        var canvas = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        using var source = Image.FromFile(filePath);

        // При 100% масштабе картинка заполняет ~80% холста — тот же зрительный
        // отступ от края, что и у встроенных векторных форм (те тоже не рисуются
        // впритык к границе 64×64 viewbox) — дальше линейно растёт со scalePercent.
        var basePortion = pixelSize * 0.8f;
        var sourceMax = Math.Max(source.Width, source.Height);
        var fitScale = sourceMax == 0 ? 1f : basePortion / sourceMax;
        var finalScale = fitScale * (scalePercent / 100f);

        var drawWidth = source.Width * finalScale;
        var drawHeight = source.Height * finalScale;
        var offsetX = (pixelSize - drawWidth) / 2f;
        var offsetY = (pixelSize - drawHeight) / 2f;

        g.DrawImage(source, offsetX, offsetY, drawWidth, drawHeight);

        return canvas;
    }

    private static Bitmap RenderBitmap(TrayIconDesign design, Color baseColor, int scalePercent, int pixelSize)
    {
        var bmp = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(pixelSize / ViewBox, pixelSize / ViewBox);

        var scale = scalePercent / 100f;
        using var brush = new SolidBrush(baseColor);
        switch (design)
        {
            case TrayIconDesign.Spokes:
                DrawSpokes(g, brush, scale);
                break;
            case TrayIconDesign.DotRays:
                DrawDotRays(g, brush, scale);
                break;
            case TrayIconDesign.ThinRays:
                DrawThinRays(g, brush, baseColor, scale);
                break;
            case TrayIconDesign.TwinHorizon:
                DrawTwinHorizon(g, brush, baseColor, scale, rayStrokeWidth: 4f);
                break;
            case TrayIconDesign.DotSunrise:
                DrawDotSunrise(g, brush, baseColor, scale);
                break;
            case TrayIconDesign.TwinHorizonCapsule:
                // Пользователь попросил совместить "Twin Horizon" (двойная линия
                // горизонта) с "Capsule Sunrise" (толстые лучи), но с бОльшим их
                // числом, чем было в Capsule (3) — переиспользуем ту же геометрию
                // Twin Horizon (5 лучей), просто с толщиной как у капсул.
                DrawTwinHorizon(g, brush, baseColor, scale, rayStrokeWidth: 6f);
                break;
        }

        return bmp;
    }

    private static PointF Scale(float x, float y, float scale) =>
        new(32 + (x - 32) * scale, 32 + (y - 32) * scale);

    private static void DrawSpokes(Graphics g, Brush brush, float scale)
    {
        float[][] spokes =
        [
            [32,32, 46.5f,28.1f, 57,32, 46.5f,35.9f],
            [32,32, 45,39.5f, 49.7f,49.7f, 39.5f,45],
            [32,32, 35.9f,46.5f, 32,57, 28.1f,46.5f],
            [32,32, 19,39.5f, 14.3f,49.7f, 24.5f,45],
            [32,32, 17.5f,28.1f, 7,32, 17.5f,35.9f],
            [32,32, 24.5f,19, 14.3f,14.3f, 19,24.5f],
            [32,32, 28.1f,17.5f, 32,7, 35.9f,17.5f],
            [32,32, 39.5f,19, 49.7f,14.3f, 45,24.5f],
        ];

        foreach (var pts in spokes)
        {
            var points = new PointF[pts.Length / 2];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = Scale(pts[i * 2], pts[i * 2 + 1], scale);
            }

            g.FillPolygon(brush, points);
        }

        var hubR = 12f * scale;
        g.FillEllipse(brush, 32 - hubR, 32 - hubR, hubR * 2, hubR * 2);
    }

    private static void DrawDotRays(Graphics g, Brush brush, float scale)
    {
        var discR = 13f * scale;
        g.FillEllipse(brush, 32 - discR, 32 - discR, discR * 2, discR * 2);

        var ringR = 24f * scale;
        var dotR = 3.5f * scale;
        for (var i = 0; i < 8; i++)
        {
            var angle = i * (Math.PI / 4);
            var cx = 32 + ringR * (float)Math.Cos(angle);
            var cy = 32 + ringR * (float)Math.Sin(angle);
            g.FillEllipse(brush, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
        }
    }

    private static void DrawThinRays(Graphics g, Brush brush, Color baseColor, float scale)
    {
        using var pen = new Pen(baseColor, 3.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        (float X1, float Y1, float X2, float Y2)[] rays =
        [
            (50,32, 60,32), (44.73f,44.73f, 51.8f,51.8f), (32,50, 32,60), (19.27f,44.73f, 12.2f,51.8f),
            (14,32, 4,32), (19.27f,19.27f, 12.2f,12.2f), (32,14, 32,4), (44.73f,19.27f, 51.8f,12.2f),
        ];
        foreach (var (x1, y1, x2, y2) in rays)
        {
            g.DrawLine(pen, Scale(x1, y1, scale), Scale(x2, y2, scale));
        }

        var discR = 14f * scale;
        g.FillEllipse(brush, 32 - discR, 32 - discR, discR * 2, discR * 2);
    }

    // Полудиск (не полный круг) — солнце выглядывает из-за линии горизонта.
    // AddArc(180°, 180°) рисует верхнюю дугу; CloseFigure() затем замыкает её
    // ПРЯМОЙ линией между концами дуги (хорда), а не через центр — иначе
    // получился бы "пирог" (сектор), а не силуэт полукруга.
    private static void DrawHalfDisc(Graphics g, Brush brush, PointF center, float r)
    {
        using var path = new GraphicsPath();
        path.AddArc(center.X - r, center.Y - r, r * 2, r * 2, 180, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // Диск смещён выше центра холста (center.Y=38 в исходных координатах, не 32) —
    // освобождает место под ДВЕ линии горизонта снизу (вторая — короче и тоньше,
    // как отражение в воде). rayStrokeWidth параметризован: TwinHorizon использует
    // тонкие лучи (4), TwinHorizonCapsule — толстые (6) поверх той же композиции.
    private static void DrawTwinHorizon(Graphics g, Brush brush, Color strokeColor, float scale, float rayStrokeWidth)
    {
        DrawHalfDisc(g, brush, Scale(32, 38, scale), 14f * scale);

        using var horizonPen = new Pen(strokeColor, 3.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(horizonPen, Scale(6, 42, scale), Scale(58, 42, scale));

        using var reflectionPen = new Pen(strokeColor, 2f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(reflectionPen, Scale(16, 48, scale), Scale(48, 48, scale));

        using var rayPen = new Pen(strokeColor, rayStrokeWidth * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        (float X1, float Y1, float X2, float Y2)[] rays =
        [
            (17.28f,29.5f, 10.35f,25.5f), (23.5f,23.28f, 19.5f,16.35f), (32,21, 32,13),
            (40.5f,23.28f, 44.5f,16.35f), (46.72f,29.5f, 53.65f,25.5f),
        ];
        foreach (var (x1, y1, x2, y2) in rays)
        {
            g.DrawLine(rayPen, Scale(x1, y1, scale), Scale(x2, y2, scale));
        }
    }

    private static void DrawDotSunrise(Graphics g, Brush brush, Color strokeColor, float scale)
    {
        DrawHalfDisc(g, brush, Scale(32, 41, scale), 13f * scale);

        var dotR = 3.2f * scale;
        (float X, float Y)[] dots =
        [
            (10.39f, 34.16f), (20.53f, 24.62f), (32, 21), (43.47f, 24.62f), (50.79f, 34.16f),
        ];
        foreach (var (x, y) in dots)
        {
            var p = Scale(x, y, scale);
            g.FillEllipse(brush, p.X - dotR, p.Y - dotR, dotR * 2, dotR * 2);
        }

        using var horizonPen = new Pen(strokeColor, 3f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(horizonPen, Scale(6, 41, scale), Scale(58, 41, scale));
    }
}
