#!/usr/bin/env python3
"""LV01/LV02の修正を隔離コピーで変異検証する。事前にsourceをRelease buildする。"""
import argparse
import difflib
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

API = 'src/Amane.Pdf.Api/'
TESTS = 'tests/Amane.Pdf.Api.Tests/'
PAGE = 'PdfPageNumbersTests.InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf'
WATER = 'PdfWatermarkTests.InvalidQueryPrecedesUploadAndTools'
STAMP = 'PdfStampImageTests.InvalidQueryPrecedesUploadAndTools'
FILL = 'PdfFillTemplateTests.Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools'
ALL = [PAGE, WATER, STAMP, FILL]
CONTROL_CASES = 4
MULTIPART_START = '    internal static async Task<byte[]> ReadFileAndTextAsync('
MULTIPART_END = '    public static Task<IReadOnlyList<string>> ReadMergeFilesAsync('
MUTATIONS = [
    ('page-query-removed', 'PdfPageNumbersEndpoint.cs',
     '() => numbers = PageNumberOptions.Parse(context.Request.Query),',
     '() => numbers = new PageNumberOptions("bc", 0, 28, "number", 1, 1, 10.5),', [PAGE]),
    ('watermark-query-removed', 'PdfWatermarkEndpoint.cs',
     '() => watermark = WatermarkOptions.Parse(context.Request.Query),',
     '() => watermark = new WatermarkOptions(true, "#808080"),', [WATER]),
    ('stamp-query-removed', 'PdfStampImageEndpoint.cs',
     '() => query = StampImageQuery.Parse(context.Request.Query),',
     '() => query = new StampImageQuery(1, 0.72m, 0.08m, 0.12m),', [STAMP]),
    ('unknown-text-allowed', 'MultipartPdfUpload.cs',
     'else throw new BadHttpRequestException("Unexpected or duplicate multipart field.");',
     'else if (name != "text") throw new BadHttpRequestException("Unexpected or duplicate multipart field.");', [FILL]),
    ('fill-query-removed', 'PdfFillTemplateEndpoint.cs',
     '() => { if (context.Request.QueryString.HasValue) throw new BadHttpRequestException("Unexpected query.", 400); },',
     '() => { },', [FILL]),
    ('fill-fields-validation-removed', 'PdfFillTemplateEndpoint.cs',
     'async (files, token) => fields = FillTemplateFields.Parse(await MultipartPdfUpload.ReadFileAndTextAsync(\n'
     '                context.Request, files, "fields", FillTemplateFields.MaxBytes, settings.MaxFileBytes,\n'
     '                settings.MaxFillTemplateRequestBytes, token, textTooLargeStatus: 413)),',
     'async (files, token) =>\n'
     '            {\n'
     '                _ = await MultipartPdfUpload.ReadFileAndTextAsync(\n'
     '                    context.Request, files, "fields", FillTemplateFields.MaxBytes, settings.MaxFileBytes,\n'
     '                    settings.MaxFillTemplateRequestBytes, token, textTooLargeStatus: 413);\n'
     '                fields = FillTemplateFields.Parse("[{\\"page\\":1,\\"x\\":0.1,\\"y\\":0.2,\\"width\\":0.8,\\"height\\":0.6,\\"fontSize\\":12,\\"align\\":\\"left\\",\\"text\\":\\"ok\\"}]"u8.ToArray());\n'
     '            },', [FILL]),
    ('query-validation-after-upload', 'PdfEndpointHelpers.cs',
     '            validateRequest?.Invoke();\n', '', ALL),
]
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def equal_failure(expected, actual, assertion):
    return {key: re.escape(value) for key, value in
            dict(expected=expected, actual=actual, assertion=assertion).items()}


EXPECTATIONS = {
    'page-query-removed': (1, 1, [equal_failure('BadRequest', 'InternalServerError',
                                            'Assert.AreEqual(status, response.StatusCode)')]),
    'watermark-query-removed': (1, 1, [equal_failure('BadRequest', 'InternalServerError',
                                                 'Assert.AreEqual(status, response.StatusCode)')]),
    'stamp-query-removed': (1, 1, [equal_failure('400', '500', 'Assert.AreEqual(status, (int)response.StatusCode)')]),
    'unknown-text-allowed': (1, 1, [equal_failure('BadRequest', 'InternalServerError',
                                              'Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode)')]),
    'fill-query-removed': (1, 1, [equal_failure('400', '500', 'Assert.AreEqual(status, (int)response.StatusCode)')]),
    'fill-fields-validation-removed': (1, 1, [equal_failure('400', '500',
                                                        'Assert.AreEqual(status, (int)response.StatusCode)')]),
    'query-validation-after-upload': (4, 4, [equal_failure('0', '1', 'Assert.AreEqual(0, body.ReadCount)')]),
}


def validate_run(row, methods, expectation=None):
    """失敗件数とassertを照合し、無関係な失敗・空の発見・中断を拒否する。"""
    assert not row['run_errors'], row
    assert row['counters']['aborted'] == row['counters']['error'] == '0', row
    assert {r['method'] for r in row['results']} == set(methods), row
    assert row['results'] and all(r['outcome'] in ('Passed', 'Failed') for r in row['results']), row
    failures = [r for r in row['results'] if r['outcome'] == 'Failed']
    assert row['exit_code'] == (1 if expectation else 0), row
    if expectation is None:
        assert not failures, row
        return
    total, failed, patterns = expectation
    assert len(row['results']) == total and len(failures) == failed and failed > 0, row
    assert all(any(all(re.fullmatch(pattern, result.get(key, ''), re.DOTALL)
                       for key, pattern in signature.items())
                   for signature in patterns) for result in failures), row


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summarize(path):
    root = ET.parse(path).getroot()
    definitions = {d.attrib['id']: d.find('t:TestMethod', NS).attrib
                   for d in root.findall('t:TestDefinitions/t:UnitTest', NS)}
    results = []
    for result in root.findall('t:Results/t:UnitTestResult', NS):
        definition = definitions[result.attrib['testId']]
        row = dict(method=definition['className'].split('.')[-1] + '.' + definition['name'],
                   outcome=result.attrib['outcome'], duration=result.attrib['duration'])
        error = result.find('t:Output/t:ErrorInfo/t:Message', NS)
        if error is not None:
            row['message'] = error.text
            row['failure'] = error.text.splitlines()[0]
            for line in error.text.splitlines():
                if line.startswith('expected:'):
                    row['expected'] = line.partition(':')[2].strip()
                elif line.startswith('actual:'):
                    row['actual'] = line.partition(':')[2].strip()
                elif line.startswith('Assert.'):
                    row['assertion'] = line
        results.append(row)
    return dict(times=root.find('t:Times', NS).attrib,
                counters=root.find('t:ResultSummary/t:Counters', NS).attrib,
                results=results, trx_sha256=sha(path),
                run_errors=[r.find('t:Text', NS).text for r in
                            root.findall('t:ResultSummary/t:RunInfos/t:RunInfo[@outcome="Error"]', NS)])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    # The temporary copy is the only place production mutations are written.
    with tempfile.TemporaryDirectory(prefix='pdf-validation-mutations-') as temporary:
        copy = Path(temporary) / 'source'
        shutil.copytree(source, copy, ignore=shutil.ignore_patterns('.git', 'docs'))
        originals = {name: (copy / API / name).read_text() for _, name, *_ in MUTATIONS}
        input_hashes = {str(path.relative_to(source)): sha(path)
                        for path in sorted((source / TESTS).glob('*.cs'))}
        report = dict(baseline_commit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip(),
                      fixed_test_sha256=input_hashes, controls=[], mutations=[])

        def run(name, methods, failed):
            with (output / (name + '-build.log')).open('w') as log:
                subprocess.run(['dotnet', 'build', TESTS + 'Amane.Pdf.Api.Tests.csproj', '-c', 'Release',
                                '--no-restore', '-t:Rebuild', '-m:1', '--disable-build-servers'],
                               cwd=copy, stdout=log, stderr=subprocess.STDOUT, check=True)
            trx_path = output / (name + '.trx')
            with (output / (name + '-test.log')).open('w') as log:
                result = subprocess.run(['dotnet', 'test', TESTS + 'Amane.Pdf.Api.Tests.csproj', '-c', 'Release',
                                         '--no-build', '--filter', '|'.join('FullyQualifiedName~' + m for m in methods),
                                         '--logger', 'trx;LogFileName=' + trx_path.name, '--results-directory', str(output)],
                                        cwd=copy, stdout=log, stderr=subprocess.STDOUT)
            row = summarize(trx_path)
            row.update(name=name, exit_code=result.returncode,
                       api_assembly_sha256=sha(copy / TESTS / 'bin/Release/net10.0/Amane.Pdf.Api.dll'))
            expectation = EXPECTATIONS[name] if failed else None
            validate_run(row, methods, expectation)
            if not failed:
                assert len(row['results']) == CONTROL_CASES, row
            if failed:
                row['expected_failure'] = dict(total=expectation[0], failed=expectation[1], patterns=expectation[2])
                row['matched_assertions'] = True
            print(name + ': ' + str(sum(r['outcome'] == 'Failed' for r in row['results'])) + ' failed / ' +
                  str(sum(r['outcome'] == 'Passed' for r in row['results'])) + ' passed', flush=True)
            return row

        report['controls'].append(run('control-before', ALL, False))
        for name, filename, old, new, methods in MUTATIONS:
            original = originals[filename]
            if filename == 'MultipartPdfUpload.cs':
                start, end = original.index(MULTIPART_START), original.index(MULTIPART_END)
                section = original[start:end]
                assert section.count(old) == 1
                changed = original[:start] + section.replace(old, new) + original[end:]
            else:
                assert original.count(old) == 1, name
                changed = original.replace(old, new)
            if name == 'query-validation-after-upload':
                mark = '            await readUploadAsync(files, operation.Token);\n'
                assert changed.count(mark) == 1
                changed = changed.replace(mark, mark + '            validateRequest?.Invoke();\n')
            path = copy / API / filename
            path.write_text(changed)  # Updating mtime and Rebuild both prevent stale DLL reuse.
            patch = ''.join(difflib.unified_diff(original.splitlines(True), changed.splitlines(True),
                                               fromfile='a/' + API + filename, tofile='b/' + API + filename))
            (output / (name + '.patch')).write_text(patch)
            try:
                row = run(name, methods, True)
                row.update(source=API + filename, source_sha256=hashlib.sha256(changed.encode()).hexdigest(),
                           patch_sha256=hashlib.sha256(patch.encode()).hexdigest())
                report['mutations'].append(row)
                (output / 'results.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
            finally:
                path.write_text(original)
        report['controls'].append(run('control-after', ALL, False))
        assert all((copy / API / f).read_text() == original for f, original in originals.items())
        assert all(sha(source / name) == digest for name, digest in input_hashes.items())
        (output / 'results.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')


if __name__ == '__main__':
    main()
