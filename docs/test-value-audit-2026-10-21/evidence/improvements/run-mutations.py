#!/usr/bin/env python3
"""比較・cleanup・本番ZIP書込の退行を、assert照合付きの隔離runnerで検証する。"""
from pathlib import Path
import re

# Reuse functions without rewriting their source or weakening the failure checks.
runner = Path(__file__).parents[1] / 'validation-fix/run-mutations.py'
namespace = {'__name__': 'regression_runner'}
exec(compile(runner.read_text(), str(runner), 'exec'), namespace)
watermark = 'PdfWatermarkTests.AllStylesAndColorsPreserveGeometryAndDrawOnceOnEveryPage'
colors = 'PdfCompressTests.SupportedColorSpaces_ArePreserved'
overlay = 'PdfOverlayResourceTests.EveryStage_CancellationStopsProcessTree_AndBuilderRemovesIntermediates'
fallback = 'PdfCompressResourceTests.BatchStdoutOverflow_FallsBackOncePerImage_AndCleansPartialRaw'
binary = 'PdfCleanTests.BinaryStreamCapture_PreservesInvalidUtf8Bytes'
zip_names = 'PdfSplitFileNameTests.MaximumUnicodeNameAnd500Entries_FitExistingZipOverhead'
namespace['API'] = ''
namespace['ALL'] = [watermark, colors, overlay, fallback, binary, zip_names]
namespace['CONTROL_CASES'] = 20
namespace['MUTATIONS'] = [
    ('colorspace-whitepoint-replaced', 'src/Amane.Pdf.Api/PdfCompressProcessor.cs',
     '            dict["/Width"] = rewritten.Width;',
     '            dict["/ColorSpace"] = JsonNode.Parse("[\\"/CalRGB\\",{\\"/WhitePoint\\":[1,1,1]}]");\n            dict["/Width"] = rewritten.Width;', [colors]),
    ('watermark-color-forced-gray', 'src/Amane.Pdf.Api/PdfWatermarkEndpoint.cs',
     'return new(diagonal, color);', 'return new(diagonal, "#808080");', [watermark]),
    ('watermark-text-forced-draft', 'src/Amane.Pdf.Api/PdfWatermarkEndpoint.cs',
     'watermark!.CreateLayer(canvas, text!)', 'watermark!.CreateLayer(canvas, "DRAFT")', [watermark]),
    ('overlay-intermediate-cleanup-removed', 'src/Amane.Pdf.Api/PdfOverlayBuilder.cs',
     'try { capacity.Delete(path); }', 'try { /* cleanup removed */ }', [overlay]),
    ('compress-raw-cleanup-removed', 'src/Amane.Pdf.Api/PdfCompressProcessor.cs',
     'Directory.Delete(directory, true);', '/* raw directory cleanup removed */', [fallback]),
    ('binary-capture-lossy-utf8', 'tests/Amane.Pdf.Api.Tests/PdfTestContext.cs',
     'return (process.ExitCode, output.ToArray());',
     'return (process.ExitCode, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(output.ToArray())));', [binary]),
    ('split-entry-comment-inflated', 'src/Amane.Pdf.Api/PdfSplitProcessor.cs',
     '                entry.LastWriteTime =',
     '                entry.Comment = new string(\'x\', 2200);\n                entry.LastWriteTime =', [zip_names]),
]
equal = namespace['equal_failure']
namespace['EXPECTATIONS'] = {
    'colorspace-whitepoint-replaced': (3, 3, [
        dict(assertion=re.escape('Assert.AreEqual(color.Contains("ICCBased") ? "/ICCBased" : gray ? "/CalGray" : "/CalRGB", actual[0].GetString())'),
             expected='"/(ICCBased|CalGray)"', actual='"/CalRGB"'),
        dict(failure=re.escape('CollectionAssert.AreEqual failed. Element at index 0 do not match.'))]),
    'watermark-color-forced-gray': (8, 4, [equal('0.7529411764705882', '0.5',
        'Assert.AreEqual(expectedColor[channel], double.Parse(actualColor.Groups[channel + 1].Value, CultureInfo.InvariantCulture), <delta>)')]),
    'watermark-text-forced-draft': (8, 4, [
        dict(assertion=re.escape('Assert.AreEqual(text, DrawnText(after, dictionary, content))'),
             expected='"(社外秘|見本|取扱注意)"', actual='"DRAFT"')]),
    'overlay-intermediate-cleanup-removed': (6, 5, [dict(failure=
        r'CollectionAssert\.AreEquivalent failed\. The number of elements in the collections do not match\. Expected:<1>\. Actual:<[234]>\.')]),
    'compress-raw-cleanup-removed': (1, 1, [dict(message=
        '.*' + re.escape('Raw extraction directory from a previous attempt still exists.') + '.*')]),
    'binary-capture-lossy-utf8': (1, 1, [dict(failure=
        re.escape('CollectionAssert.AreEqual failed. Element at index 0 do not match.'))]),
    'split-entry-comment-inflated': (1, 1, [dict(message=
        '.*' + re.escape('Production ZIP overhead budget must fit 500 maximum Unicode names.') + '.*')]),
}
if __name__ == '__main__':
    namespace['main']()
