namespace PortableScreenRecorder.Core;

public static class Geometry
{
    public static PixelRect BoundingRect(IEnumerable<PixelRect> rectangles)
    {
        var items = rectangles.Where(static r => !r.IsEmpty).ToArray();
        if (items.Length == 0)
        {
            return default;
        }

        var left = items.Min(static r => r.X);
        var top = items.Min(static r => r.Y);
        var right = items.Max(static r => r.Right);
        var bottom = items.Max(static r => r.Bottom);
        return new PixelRect(left, top, checked(right - left), checked(bottom - top));
    }

    public static PixelRect Normalize(int startX, int startY, int endX, int endY) =>
        new(Math.Min(startX, endX), Math.Min(startY, endY), Math.Abs(endX - startX), Math.Abs(endY - startY));

    public static PixelRect Clip(PixelRect value, PixelRect bounds)
    {
        var left = Math.Max(value.X, bounds.X);
        var top = Math.Max(value.Y, bounds.Y);
        var right = Math.Min(value.Right, bounds.Right);
        var bottom = Math.Min(value.Bottom, bounds.Bottom);
        return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top);
    }

    public static PixelRect LogicalToPhysical(PixelRect logical, double scaleX, double scaleY) =>
        new(
            (int)Math.Round(logical.X * scaleX),
            (int)Math.Round(logical.Y * scaleY),
            (int)Math.Round(logical.Width * scaleX),
            (int)Math.Round(logical.Height * scaleY));

    public static PixelRect EnsureEven(PixelRect rect) =>
        new(rect.X, rect.Y, rect.Width & ~1, rect.Height & ~1);
}
