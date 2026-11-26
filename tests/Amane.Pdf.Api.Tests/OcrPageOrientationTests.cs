using System.Text;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class OcrPageOrientationTests
{
    [TestMethod]
    [DataRow("0", "7.00", 0, true)]
    [DataRow("0", "6.99", 0, false)]
    [DataRow("90", "12.50", 90, false)]
    [DataRow("180", "6.22", 180, false)]
    [DataRow("270", "12.50", 270, false)]
    public void ReadsAngleButOnlyConfidentUprightAllowsAdaptiveOtsu(
        string angle, string margin, int expectedDegrees, bool expectedUpright)
    {
        var orientation = OcrPageOrientation.Parse(Encoding.ASCII.GetBytes(
            $"Page number: 0\nOrientation in degrees: {angle}\nOrientation confidence: {margin}\nScript: Japanese\n"));
        Assert.IsNotNull(orientation);
        Assert.AreEqual(expectedDegrees, orientation.Value.Degrees);
        Assert.AreEqual(expectedUpright, orientation.Value.IsConfidentlyUpright);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Orientation in degrees: 0\n")]
    [DataRow("Orientation in degrees: 0\nOrientation confidence: NaN\n")]
    [DataRow("Orientation in degrees: 45\nOrientation confidence: 9.50\n")]
    [DataRow("Orientation in degrees: 0\nOrientation in degrees: 180\nOrientation confidence: 9.50\n")]
    public void RejectsMissingOrInvalidOrientation(string output)
        => Assert.IsNull(OcrPageOrientation.Parse(Encoding.ASCII.GetBytes(output)));
}
