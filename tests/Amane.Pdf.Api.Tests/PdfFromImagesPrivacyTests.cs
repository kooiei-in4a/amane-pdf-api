using System.Net;
using System.Text;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfFromImagesPrivacyTests
{
    [TestMethod]
    [DataRow("standard")]
    [DataRow("original")]
    public async Task GpsDateAndCamera_AreRemovedFromPdfAndEveryExtractedStream(string quality)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var input = FromImagesFixtures.ExifWithGps(await CompressFixtures.JpegAsync(test, 64, 48));
        foreach (var marker in FromImagesFixtures.PrivateExifText)
            Assert.IsTrue(input.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0);
        Assert.IsTrue(input.AsSpan().IndexOf(FromImagesFixtures.GpsCoordinates) >= 0);
        using var form = PdfTestContext.MergeForm(input);
        using var response = await test.Client.PostAsync("/api/pdf/from-images?quality=" + quality, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var pdf = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(pdf, 1);
        AssertPrivateMetadataAbsent(pdf);
        var document = await PdfCompressTests.InspectAsync(test, pdf);
        var image = document.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.AreEqual(48, image.GetProperty("width").GetInt32());
        Assert.AreEqual(64, image.GetProperty("height").GetInt32());
        var imageObject = "obj:" + image.GetProperty("object").GetString();
        var imageChecked = false;
        foreach (var obj in document.GetProperty("qpdf")[1].EnumerateObject())
        {
            if (!obj.Value.TryGetProperty("stream", out var stream)) continue;
            var data = Convert.FromBase64String(stream.GetProperty("data").GetString()!);
            AssertPrivateMetadataAbsent(data);
            if (obj.Name != imageObject) continue;
            Assert.IsTrue(data.AsSpan().StartsWith(new byte[] { 255, 216 }));
            imageChecked = true;
        }
        Assert.IsTrue(imageChecked, "抽出したJPEGの検査が必要です。");
        var path = Path.Combine(test.Root, "privacy.pdf");
        var expanded = Path.Combine(test.Root, "privacy-qdf.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        Assert.AreEqual(0, (await test.QpdfAsync("--qdf", "--object-streams=disable",
            "--stream-data=uncompress", path, expanded)).ExitCode);
        AssertPrivateMetadataAbsent(await File.ReadAllBytesAsync(expanded));
        foreach (var marker in FromImagesFixtures.PrivateExifText)
            Assert.IsFalse(test.Logs.Any(line => line.Contains(marker, StringComparison.Ordinal)));
        test.AssertClean();
    }

    private static void AssertPrivateMetadataAbsent(byte[] bytes)
    {
        foreach (var marker in FromImagesFixtures.PrivateExifText)
            Assert.IsFalse(bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0, marker);
        Assert.IsFalse(bytes.AsSpan().IndexOf(FromImagesFixtures.GpsCoordinates) >= 0, "GPSの有理数データが残っています。");
        Assert.IsFalse(bytes.AsSpan().IndexOf("Exif\0\0"u8) >= 0, "Exif APP1が残っています。");
    }
}
