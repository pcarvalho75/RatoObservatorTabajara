using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MouseTrackerTabajara.Core;
using MouseTrackerTabajara.Tracking;
namespace MouseTrackerTabajara.Desktop;
public static class CameraFrameMaterial
{
    public static BitmapSource Bitmap(byte[] bytes){using var stream=new MemoryStream(bytes,false);var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;}
    public static string BytesHash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string PixelHash(BitmapSource bitmap){var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);var stride=checked(bitmap.PixelWidth*4);var bytes=new byte[checked(stride*bitmap.PixelHeight)];converted.CopyPixels(bytes,stride,0);return BytesHash(bytes);}
    public static void Verify(CameraFrameIdentity identity,VideoAsset asset)
    {
        if(OpenCvTracker.Probe(asset.Path)!=asset.Metadata)throw new InvalidDataException("Registered metadata differs from the exact source recording.");
        using var decoder=new FrameDecoderPool(1);var bytes=decoder.Read(asset.Path,identity.Frame,expectedSha256:asset.SourceSha256);var bitmap=Bitmap(bytes);
        if(bitmap.PixelWidth!=identity.Width || bitmap.PixelHeight!=identity.Height || BytesHash(bytes)!=identity.EncodedImageSha256 || PixelHash(bitmap)!=identity.DisplayedPixelSha256)throw new InvalidDataException("Displayed frame/image fingerprint differs from source pixels; operation held.");
    }
}
