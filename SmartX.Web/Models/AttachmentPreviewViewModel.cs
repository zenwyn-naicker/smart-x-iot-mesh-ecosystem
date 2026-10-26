using System.Text;

namespace SmartX.Web.Models;

public sealed class AttachmentPreviewViewModel
{
    public Guid SensorId { get; init; }
    public Guid AttachmentId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string? ImageDataUrl { get; private set; }
    public string? Text { get; private set; }
    public bool IsTruncated { get; private set; }

    public void Load(byte[] content)
    {
        ImageDataUrl = null;
        Text = null;
        IsTruncated = false;
        var extension = Path.GetExtension(FileName).ToLowerInvariant();
        var imageType = extension switch
        {
            ".png" when content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) => "image/png",
            ".jpg" or ".jpeg" when content.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) => "image/jpeg",
            _ => null
        };

        if (imageType is not null)
        {
            ImageDataUrl = $"data:{imageType};base64,{Convert.ToBase64String(content)}";
            return;
        }

        if (extension is not (".txt" or ".log" or ".json" or ".cfg"))
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(content);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            var buffer = new char[100001];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, Math.Min(count, 100000));
            if (text.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
            {
                return;
            }

            Text = text;
            IsTruncated = count > 100000;
        }
        catch (DecoderFallbackException)
        {
        }
    }
}
