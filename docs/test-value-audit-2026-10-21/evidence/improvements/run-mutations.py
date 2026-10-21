#!/usr/bin/env python3
"""LV04/07/08/10/11の比較・cleanupの退行を、既存の隔離runnerで検証する。"""
from pathlib import Path
import json

# Reuse the already audited runner; allow parameter rows unaffected by a mutation to pass.
runner = Path(__file__).parents[1] / 'validation-fix/run-mutations.py'
source = runner.read_text().replace(
    "assert all(r['outcome'] == expected for r in row['results']), row",
    "assert (any(r['outcome'] == 'Failed' for r in row['results']) if failed else all(r['outcome'] == 'Passed' for r in row['results'])), row")
source = source.replace("print(name + ': ' + str(len(row['results'])) + ' ' + expected, flush=True)",
                        "print(name + ': ' + str(sum(r['outcome'] == 'Failed' for r in row['results'])) + ' failed / ' + str(sum(r['outcome'] == 'Passed' for r in row['results'])) + ' passed', flush=True)")
namespace = {'__name__': 'regression_runner'}
exec(compile(source, str(runner), 'exec'), namespace)
watermark = 'PdfWatermarkTests.AllStylesAndColorsPreserveGeometryAndDrawOnceOnEveryPage'
colors = 'PdfCompressTests.SupportedColorSpaces_ArePreserved'
overlay = 'PdfOverlayResourceTests.EveryStage_CancellationStopsProcessTree_AndBuilderRemovesIntermediates'
fallback = 'PdfCompressResourceTests.BatchStdoutOverflow_FallsBackOncePerImage_AndCleansPartialRaw'
binary = 'PdfCleanTests.BinaryStreamCapture_PreservesInvalidUtf8Bytes'
namespace['API'] = ''
namespace['ALL'] = [watermark, colors, overlay, fallback, binary]
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
]
if __name__ == '__main__':
    namespace['main']()
