#!/usr/bin/env python3
"""実Docker上のPDF APIを検証する。Python標準ライブラリのみ使用する。"""

import contextlib
import http.client
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import uuid
import zlib
import importlib.util
import zipfile


FIXTURE = (Path(__file__).resolve().parent.parent / "tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf").read_bytes()
PASSWORD = "docker-fixture-日本語é🔒"
WRONG_PASSWORD = "incorrect-fixture-password"
SENTINEL = "PDF-CONTENT-SENTINEL"
CHECK_ROOT = "/tmp/smoke-check"
LIMITED_EXEC = ("/usr/bin/env", "--ignore-signal=XFSZ", "--", "prlimit", "--core=0:0")


def docker(*arguments, data=None, check=True):
    return subprocess.run(
        ["docker", *arguments], input=data, capture_output=True, check=check, timeout=45
    )


def multipart(file=None, password=PASSWORD, files=None):
    boundary = "smoke-" + uuid.uuid4().hex
    parts = []
    inputs = files if files is not None else ([] if file is None else [file])
    for input_file in inputs:
        parts.append(
            (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="../../untrusted.pdf"\r\n'
             "Content-Type: application/octet-stream\r\n\r\n").encode() + input_file + b"\r\n"
        )
    if password is not None:
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="password"\r\n\r\n{password}\r\n'.encode()
        )
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), "multipart/form-data; boundary=" + boundary


class ApiContainer:
    def __init__(self, image, settings=(), isolated=False, memory_mib=1536, mounts=()):
        self.memory_mib = memory_mib
        self.name = "amane-pdf-smoke-" + uuid.uuid4().hex
        self.port = None
        self.image = image
        self.settings = settings
        self.isolated = isolated
        self.mounts = mounts
        self.created = False
        self.post_count = 0

    def start(self):
        arguments = [
            "run", "--detach", "--name", self.name,
            "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m",
            "--cpus", "1", "--memory", f"{self.memory_mib}m", "--memory-swap", f"{self.memory_mib}m", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges=true",
        ]
        if self.isolated:
            arguments += ["--network", "none"]
        else:
            arguments += ["--publish", "127.0.0.1::8080"]
        for setting in self.settings:
            arguments += ["--env", setting]
        for source, target in self.mounts:
            arguments += ["--mount", f"type=bind,src={source},dst={target},readonly"]
        docker(*arguments, self.image)
        self.created = True
        if not self.isolated:
            self.port = int(docker("port", self.name, "8080/tcp").stdout.decode().strip().rsplit(":", 1)[1])
        for _ in range(100):
            try:
                status, _, body = self.request("GET", "/healthz")
                if status == 200 and body == b"Healthy":
                    break
            except (OSError, subprocess.SubprocessError):
                pass
            time.sleep(0.2)
        else:
            raise AssertionError("Docker health check failed.")
        assert docker("exec", self.name, "id", "-u").stdout.strip() != b"0", "App must run as non-root."
        container = json.loads(docker("inspect", self.name).stdout)[0]
        assert container["HostConfig"]["ReadonlyRootfs"]
        assert "/tmp" in container["HostConfig"]["Tmpfs"]
        assert container["HostConfig"]["NanoCpus"] == 1_000_000_000
        assert container["HostConfig"]["Memory"] == self.memory_mib * 1024 * 1024
        assert container["HostConfig"]["MemorySwap"] == self.memory_mib * 1024 * 1024
        assert not any(mount["Type"] == "volume" for mount in container["Mounts"])
        if self.isolated:
            assert container["HostConfig"]["NetworkMode"] == "none"

    def exec(self, *arguments):
        return docker("exec", self.name, *arguments)

    def request(self, method, path, body=b"", content_type=None, chunked=False):
        if method == "POST":
            self.post_count += 1
        if self.isolated:
            # コンテナ内loopbackだけでHTTPを送る。curl等をruntimeへ追加しない。
            headers = f"{method} {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nContent-Length: {len(body)}\r\n"
            if content_type:
                headers += f"Content-Type: {content_type}\r\n"
            raw = docker(
                "exec", "--interactive", self.name, "bash", "-c",
                "exec 3<>/dev/tcp/127.0.0.1/8080; cat >&3; cat <&3",
                data=(headers + "\r\n").encode() + body,
            ).stdout
            class BufferedSocket:
                def makefile(self, mode):
                    return io.BytesIO(raw)

            # Problem Details may use chunked encoding even when PDF downloads have Content-Length.
            with http.client.HTTPResponse(BufferedSocket()) as response:
                response.begin()
                return response.status, dict(response.getheaders()), response.read()
        headers = {}
        if content_type:
            headers["Content-Type"] = content_type
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=40)
        try:
            payload = (body[i:i + 4096] for i in range(0, len(body), 4096)) if chunked else body
            connection.request(method, path, body=payload, headers=headers, encode_chunked=chunked)
            response = connection.getresponse()
            return response.status, dict(response.getheaders()), response.read()
        finally:
            connection.close()

    def protect(self, file, password=PASSWORD, expected=200, chunked=False):
        body, content_type = multipart(file, password)
        status, headers, result = self.request("POST", "/api/pdf/protect", body, content_type, chunked)
        assert status == expected, f"Expected HTTP {expected}, received {status}."
        media_type = headers.get("Content-Type", "").split(";", 1)[0]
        if expected == 200:
            assert media_type == "application/pdf" and result.startswith(b"%PDF-")
        else:
            assert media_type == "application/problem+json"
            assert json.loads(result)["status"] == expected
            for secret in (PASSWORD.encode(), SENTINEL.encode(), b"/tmp/", b"stack", b"qpdf"):
                assert secret not in result, "Problem Details exposed internal information."
        self.assert_clean()
        return result

    def unlock(self, file, password=PASSWORD, expected_reason=None):
        body, content_type = multipart(file, password)
        status, headers, result = self.request("POST", "/api/pdf/unlock", body, content_type)
        expected = 422 if expected_reason else 200
        assert status == expected, f"Expected HTTP {expected}, received {status}."
        media_type = headers.get("Content-Type", "").split(";", 1)[0]
        if expected == 200:
            assert media_type == "application/pdf" and result.startswith(b"%PDF-")
            assert "filename=unlocked.pdf" in headers.get("Content-Disposition", "")
        else:
            assert media_type == "application/problem+json"
            problem = json.loads(result)
            assert problem["status"] == 422 and problem["reason"] == expected_reason
            for secret in (PASSWORD.encode(), WRONG_PASSWORD.encode(), SENTINEL.encode(), b"/tmp/", b"stack", b"qpdf"):
                assert secret not in result, "Problem Details exposed internal information."
        self.assert_clean()
        return result

    def rotate(self, file, angle=90, pages=None):
        body, content_type = multipart(file, password=None)
        path = f"/api/pdf/rotate?angle={angle}"
        if pages is not None:
            path += "&pages=" + pages
        status, headers, result = self.request("POST", path, body, content_type)
        assert status == 200, f"Expected HTTP 200, received {status}."
        assert headers.get("Content-Type", "").split(";", 1)[0] == "application/pdf"
        assert "filename=rotated.pdf" in headers.get("Content-Disposition", "")
        assert result.startswith(b"%PDF-")
        self.assert_clean()
        return result

    def optimize(self, file):
        body, content_type = multipart(file, password=None)
        status, headers, result = self.request("POST", "/api/pdf/optimize", body, content_type)
        assert status == 200, f"Expected HTTP 200, received {status}."
        assert headers.get("Content-Type", "").split(";", 1)[0] == "application/pdf"
        assert "filename=optimized.pdf" in headers.get("Content-Disposition", "")
        assert result.startswith(b"%PDF-")
        self.assert_clean()
        return result

    def merge(self, files):
        body, content_type = multipart(password=None, files=files)
        status, headers, result = self.request("POST", "/api/pdf/merge", body, content_type)
        assert status == 200, f"Expected HTTP 200, received {status}."
        assert headers.get("Content-Type", "").split(";", 1)[0] == "application/pdf"
        assert "filename=merged.pdf" in headers.get("Content-Disposition", "")
        assert result.startswith(b"%PDF-")
        self.assert_clean()
        return result

    def extract(self, file, pages):
        body, content_type = multipart(file, password=None)
        status, headers, result = self.request("POST", "/api/pdf/extract?pages=" + pages, body, content_type)
        assert status == 200, f"Expected HTTP 200, received {status}."
        assert headers.get("Content-Type", "").split(";", 1)[0] == "application/pdf"
        assert "filename=extracted.pdf" in headers.get("Content-Disposition", "")
        assert result.startswith(b"%PDF-")
        self.assert_clean()
        return result

    def assert_clean(self):
        assert docker(
            "exec", self.name, "sh", "-c",
            "if [ -d /tmp/amane-pdf-api ]; then find /tmp/amane-pdf-api -mindepth 1 -print; fi",
        ).stdout == b"", "PDF temporary files remain."
        result = docker("logs", self.name)
        logs = result.stdout + result.stderr
        for secret in (PASSWORD.encode(), WRONG_PASSWORD.encode(), SENTINEL.encode(), b"../../untrusted.pdf", b"/tmp/amane-pdf-api/"):
            assert secret not in logs, "Application logs exposed input or job paths."

    def verify_encryption(self, pdf):
        docker("exec", self.name, "mkdir", "-m", "700", "-p", CHECK_ROOT)
        docker(
            "exec", "--interactive", self.name, "sh", "-c",
            "umask 077; cat > /tmp/smoke-check/output.pdf", data=pdf,
        )
        output = CHECK_ROOT + "/output.pdf"
        assert docker("exec", self.name, "qpdf", "--is-encrypted", output, check=False).returncode == 0
        for password, inspection, exit_code in ((PASSWORD, "check", 0), (PASSWORD, "showEncryption", 0), (WRONG_PASSWORD, "check", 2)):
            job = json.dumps({"inputFile": output, "password": password, inspection: ""}).encode()
            docker(
                "exec", "--interactive", self.name, "sh", "-c",
                "umask 077; cat > /tmp/smoke-check/job.json", data=job,
            )
            result = docker("exec", self.name, "qpdf", "--job-json-file=" + CHECK_ROOT + "/job.json", check=False)
            assert result.returncode == exit_code, "Password/inspection check failed."
            if inspection == "showEncryption":
                assert b"AESv3" in result.stdout and b"R = 6" in result.stdout
        docker("exec", self.name, "rm", "-rf", CHECK_ROOT)

    def page_rotations(self, pdf):
        docker("exec", self.name, "mkdir", "-m", "700", "-p", CHECK_ROOT)
        docker(
            "exec", "--interactive", self.name, "sh", "-c",
            "umask 077; cat > /tmp/smoke-check/output.pdf", data=pdf,
        )
        docker("exec", self.name, "qpdf", "--check", CHECK_ROOT + "/output.pdf")
        result = docker("exec", self.name, "qpdf", "--json", CHECK_ROOT + "/output.pdf")
        document = json.loads(result.stdout)
        objects = document["qpdf"][1]
        rotations = [
            objects["obj:" + page["object"]]["value"].get("/Rotate", 0)
            for page in document["pages"]
        ]
        docker("exec", self.name, "rm", "-rf", CHECK_ROOT)
        return rotations

    def verify_decryption(self, pdf):
        docker("exec", self.name, "mkdir", "-m", "700", "-p", CHECK_ROOT)
        try:
            docker(
                "exec", "--interactive", self.name, "sh", "-c",
                "umask 077; cat > /tmp/smoke-check/output.pdf", data=pdf,
            )
            output = CHECK_ROOT + "/output.pdf"
            assert docker("exec", self.name, "qpdf", "--is-encrypted", output, check=False).returncode == 2
            assert docker("exec", self.name, "qpdf", "--requires-password", output, check=False).returncode == 2
            docker("exec", self.name, "qpdf", "--check", output)
            assert docker("exec", self.name, "qpdf", "--show-npages", output).stdout.strip() == b"1"
        finally:
            docker("exec", self.name, "rm", "-rf", CHECK_ROOT)

    def close(self):
        if self.created:
            docker("rm", "--force", self.name)


@contextlib.contextmanager
def running_container(image, settings=(), isolated=False, mounts=()):
    api = ApiContainer(image, settings, isolated, mounts=mounts)
    try:
        api.start()
        yield api
    finally:
        api.close()


@contextlib.contextmanager
def request_failing_qpdf(image, isolated=False):
    # Let all real startup checks finish before injecting a request-stage error.
    with tempfile.TemporaryDirectory(prefix="amane-qpdf-fault-") as directory:
        wrapper = Path(directory) / "qpdf"
        wrapper.write_text('''#!/bin/sh
for argument do last=$argument; done
case "$last" in */pdfcpu-blank.pdf) exec qpdf "$@";; esac
if [ "$1" = --version ] || [ -f "$(dirname "$last")/pdfcpu-layer.json" ]; then
    exec qpdf "$@"
fi
exec /usr/bin/cat "$@"
''')
        wrapper.chmod(0o755)
        with running_container(image, ("Pdf__QpdfPath=/validation/qpdf",), isolated,
                               ((wrapper, "/validation/qpdf"),)) as api:
            yield api


def pdfcpu_smoke(api):
    root = "/tmp/pdfcpu-smoke"
    prefix = "/opt/amane-pdf"
    api.exec("mkdir", "-m", "700", root)
    environment = ["env", f"HOME={root}", f"XDG_CONFIG_HOME={root}", "GOMEMLIMIT=200MiB",
                   "GOGC=100", "GODEBUG=", "GOMAXPROCS=1", "GOTRACEBACK=none"]
    tool = [*environment, *LIMITED_EXEC, "--as=1073741824:1073741824", "--fsize=8388609:8388609", "--",
            prefix + "/bin/pdfcpu", "-c", prefix + "/config", "--offline"]
    assert "version: 0.16.1" in api.exec(*tool, "version").stdout.decode().splitlines()
    assert b"BIZUDPGothic-Regular (" in api.exec(*tool, "fonts", "list").stdout
    api.exec("sh", "-c", "test ! -w /opt/amane-pdf/config/pdfcpu/config.yml && "
             "test ! -w /opt/amane-pdf/config/pdfcpu/fonts && "
             "test ! -w /opt/amane-pdf/config/pdfcpu/fonts/BIZUDPGothic-Regular.gob")
    assert api.exec("stat", "-c", "%u:%a", prefix + "/config/pdfcpu/config.yml").stdout.strip() == b"0:444"
    repository = Path(__file__).resolve().parent.parent
    license_paths, expected_hashes = [], []
    for line in (repository / "third_party/pdfcpu-licenses.sha256").read_text().splitlines():
        digest, path = line.split("  ", 1)
        path = path.removeprefix("pdfcpu/")
        license_paths.append(prefix + "/licenses/" + path)
        expected_hashes.append(digest)
    actual = api.exec("sha256sum", *license_paths).stdout.decode().splitlines()
    assert [line.split()[0] for line in actual] == expected_hashes
    assert api.exec("cat", prefix + "/licenses/THIRD_PARTY_NOTICES.md").stdout == (repository / "THIRD_PARTY_NOTICES.md").read_bytes()
    fixtures = repository / "tests/Amane.Pdf.Api.Tests/Fixtures"
    for name in ("pdfcpu-blank.json", "pdfcpu-layer.json"):
        docker("exec", "-i", api.name, "sh", "-c", f"umask 077; cat > {root}/{name}", data=(fixtures / name).read_bytes())
    api.exec("qpdf", "--json-input", root + "/pdfcpu-blank.json", root + "/blank.pdf")
    api.exec(*tool, "create", root + "/pdfcpu-layer.json", root + "/blank.pdf", root + "/layer.pdf")
    api.exec("qpdf", "--check", root + "/layer.pdf")
    assert docker("exec", api.name, "qpdf", "--is-encrypted", root + "/layer.pdf", check=False).returncode == 2
    assert api.exec("qpdf", "--show-npages", root + "/layer.pdf").stdout == b"1\n"
    metadata = api.exec("qpdf", "--json=2", "--json-stream-data=none", "--decode-level=none", root + "/layer.pdf").stdout
    assert b"/FontFile2" in metadata and b"/ToUnicode" in metadata and b"+BIZUDPGothic-Regular" in metadata
    pdf = api.exec("cat", root + "/layer.pdf").stdout
    api.exec("rm", "-rf", root)
    print("Docker pdfcpu: locked version/Japanese draw/subset font/read-only config/licenses PASS", flush=True)
    return pdf


def flate_image_pdf():
    """16 MP RGB。圧縮fixtureだけをメモリに保持する。"""
    compressor = zlib.compressobj(1)
    row = bytes(x % 251 for x in range(4096 * 3))
    parts = [compressor.compress(row) for _ in range(4096)]
    parts.append(compressor.flush())
    image = b"".join(parts)
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 4 0 R >> >> >>",
        (f"<< /Type /XObject /Subtype /Image /Width 4096 /Height 4096 /ColorSpace /DeviceRGB "
         f"/BitsPerComponent 8 /Filter /FlateDecode /Length {len(image)} >>\nstream\n").encode() + image + b"\nendstream",
    ]
    output = io.BytesIO()
    output.write(b"%PDF-1.4\n")
    offsets = []
    for number, value in enumerate(objects, 1):
        offsets.append(output.tell())
        output.write(f"{number} 0 obj\n".encode() + value + b"\nendobj\n")
    xref = output.tell()
    output.write(b"xref\n0 5\n0000000000 65535 f \n")
    for offset in offsets:
        output.write(f"{offset:010} 00000 n \n".encode())
    output.write(f"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode())
    return output.getvalue()


def assert_startup_failure(image, settings, expected_message="PDF処理のメモリ制限の自己テストに失敗しました。"):
    name = "amane-pdf-smoke-startup-" + uuid.uuid4().hex
    arguments = ["run", "--detach", "--name", name, "--read-only", "--network", "none",
                 "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m", "--cpus", "1",
                 "--memory", "1.5g", "--memory-swap", "1.5g", "--cap-drop", "ALL",
                 "--security-opt", "no-new-privileges=true"]
    for setting in settings:
        arguments += ["--env", setting]
    try:
        docker(*arguments, image)
        for _ in range(50):
            state = json.loads(docker("inspect", name).stdout)[0]["State"]
            if not state["Running"]:
                break
            time.sleep(0.1)
        assert not state["Running"] and state["ExitCode"] != 0, "Invalid limits must prevent startup."
        logs = docker("logs", name)
        output = logs.stdout + logs.stderr
        assert expected_message.encode() in output
        for detail in (b"/missing/", b"/src/", b"prlimit:", b"error while loading", b"Unhandled exception"):
            assert detail not in output, "Startup logs exposed internal details."
    finally:
        docker("rm", "--force", name, check=False)


def split_request(api, source, expected=200):
    body, ct = multipart(source, password=None)
    code, headers, result = api.request("POST", "/api/pdf/split?every=1", body, ct)
    assert code == expected, ("split", code, expected)
    if expected == 200:
        assert headers["Content-Type"].split(";")[0] == "application/zip"
        assert "filename=split.zip" in headers["Content-Disposition"]
        assert int(headers["Content-Length"]) == len(result)
    else:
        problem = json.loads(result)
        assert problem["status"] == expected
        assert problem.get("reason") == ("output-too-large" if expected == 422 else None)
        assert "Content-Disposition" not in headers
        for value in (b"/tmp/", SENTINEL.encode(), PASSWORD.encode(), b"untrusted.pdf"):
            assert value not in result
    api.assert_clean()
    assert api.request("GET", "/healthz")[0] == 200
    return result


def split_calibration(api, source):
    """HTTPからは見えない終了コードを、APIと同じenv/prlimit/qpdfで確認する。"""
    root = "/tmp/split-smoke-check"
    api.exec("mkdir", "-m", "700", root)
    docker("exec", "-i", api.name, "sh", "-c", "umask 077; cat > /tmp/split-smoke-check/input.pdf", data=source)
    lengths = []
    records = []
    try:
        for page in (1, 2):
            output = root + "/part.pdf"
            docker("exec", "-i", api.name, "sh", "-c", "umask 077; cat > /tmp/split-smoke-check/part.pdf", data=b"")
            args = ("qpdf", root + "/input.pdf", "--pages", ".", str(page), "--", output)
            result = docker("exec", "--env", "JPEGMEM=600M", api.name, *LIMITED_EXEC, "--as=570425344:570425344", "--", *args, check=False)
            assert result.returncode == 0
            length = int(api.exec("stat", "-c", "%s", output).stdout)
            lengths.append(length)
            if page == 2:
                for budget in (length, length - 1, 128):
                    result = docker("exec", "--env", "JPEGMEM=600M", api.name, *LIMITED_EXEC, "--as=570425344:570425344",
                                    f"--fsize={budget+1}:{budget+1}", "--", *args, check=False)
                    actual = int(api.exec("stat", "-c", "%s", output).stdout)
                    records.append({"budget": budget, "exit": result.returncode, "bytes": actual})
                    if budget >= length - 1:
                        assert result.returncode == 0 and actual == length
                    else:
                        assert result.returncode in (0, 2) and actual == budget + 1
        print("Docker split calibration " + json.dumps(records), flush=True)
        return sum(lengths), lengths[-1]
    finally:
        api.exec("rm", "-rf", root)


def split_smoke(image):
    for tz in ("UTC", "Asia/Tokyo"):
        with running_container(image, ("TZ=" + tz,), isolated=True) as api:
            source = api.merge([api.rotate(FIXTURE), FIXTURE])
            result = split_request(api, source)
            with zipfile.ZipFile(io.BytesIO(result)) as archive:
                assert archive.namelist() == ["part-001_p1.pdf", "part-002_p2.pdf"]
                for entry, rotation in zip(archive.infolist(), (90, 0)):
                    assert entry.compress_type == zipfile.ZIP_STORED
                    assert entry.date_time == (1980, 1, 1, 0, 0, 0)
                    assert api.page_rotations(archive.read(entry)) == [rotation]
            api.assert_clean()
            if tz == "UTC":
                total, last = split_calibration(api, source)
                fixture = source
    # The total bound becomes the partBudget on the final part. Job capacity must not bind first.
    for limit, expected in ((total, 200), (total - 1, 422), (total - last + 128, 422)):
        with running_container(image, (f"Pdf__MaxSplitOutputBytes={limit}",), isolated=True) as api:
            split_request(api, fixture, expected)
    with request_failing_qpdf(image, isolated=True) as api:
        split_request(api, fixture, 500)
    print("Docker split: Stored/pages/names/TZ/exact/exit0 overflow/real FSIZE/500/cleanup/health PASS", flush=True)


def compress_smoke(image):
    spec = importlib.util.spec_from_file_location("compress_validation", Path(__file__).with_name("compress-validation.py"))
    validation = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validation)
    with running_container(image, isolated=True) as api:
        fixture_root = "/tmp/compress-smoke-fixture"
        for package in ("libjpeg-turbo-progs", "libjpeg-turbo8", "libturbojpeg0"):
            api.exec("test", "-r", f"/usr/share/doc/{package}/copyright")
        for executable in ("djpeg", "cjpeg"):
            version = api.exec(executable, "-version")
            print((version.stdout + version.stderr).decode().strip())
        api.exec("mkdir", "-m", "700", "-p", fixture_root)
        try:
            for gray in (False, True):
                w, h = 2200, 1600
                row = bytes((x // 10 + (x % 3) * 17) % 256 for x in range(w * (1 if gray else 3)))
                pnm = f"P{5 if gray else 6}\n{w} {h}\n255\n".encode() + row * h
                api_data = fixture_root + "/fixture.pnm"
                docker("exec", "-i", api.name, "sh", "-c", "umask 077; cat > /tmp/compress-smoke-fixture/fixture.pnm", data=pnm)
                api.exec("cjpeg", "-quality", "90", "-outfile", fixture_root + "/fixture.jpg", api_data)
                raw = api.exec("cat", fixture_root + "/fixture.jpg").stdout
                assert len(raw) >= 32768
                for executable, args in (
                    ("cjpeg", ("-quality", "75", "-maxmemory", "64M", "-strict", api_data)),
                    ("djpeg", ("-maxmemory", "64M", "-maxscans", "100", "-strict", fixture_root + "/fixture.jpg")),
                ):
                    output = fixture_root + "/limited-" + executable
                    result = docker("exec", api.name, *LIMITED_EXEC, "--as=67108864:67108864", "--fsize=64:64",
                                    "--", executable, "-outfile", output, *args, check=False)
                    assert result.returncode == 1, (executable, result.returncode)
                    assert api.exec("stat", "-c", "%s", output).stdout.strip() == b"64"
                source = validation.image_pdf(raw, w, h, color="/DeviceGray" if gray else "/DeviceRGB")
                for level in ("standard", "strong"):
                    body, ct = multipart(source, password=None)
                    code, headers, result = api.request("POST", "/api/pdf/compress?level=" + level, body, ct)
                    assert code == 200 and headers["X-Pdf-Images-Recompressed"] == "1"
                    assert len(result) < len(source) and "filename=compressed.pdf" in headers["Content-Disposition"]
                    api.verify_decryption(result)
                    api.assert_clean()
            # A real FSIZE overflow returns an I/O error with SIGXFSZ ignored.
            result = docker("exec", api.name, *LIMITED_EXEC, "--as=67108864:67108864", "--fsize=4096:4096", "--", "dd",
                "if=/dev/zero", "of=" + fixture_root + "/limited", "bs=8192", "count=2", "status=none", check=False)
            assert result.returncode == 1, result.returncode
            assert api.exec("stat", "-c", "%s", fixture_root + "/limited").stdout.strip() == b"4096"
            assert api.exec("stat", "-c", "%a", fixture_root).stdout.strip() == b"700"
            assert api.exec("stat", "-c", "%a", api_data).stdout.strip() == b"600"
        finally:
            api.exec("rm", "-rf", fixture_root)
        api.assert_clean()
    for settings in (("Pdf__DjpegPath=/missing/djpeg",), ("Pdf__CjpegPath=/bin/false",), ("Pdf__JpegAddressSpaceLimitBytes=1",)):
        assert_startup_failure(image, settings)
    print("Docker compress: both levels/color/gray/permissions/startup/FSIZE I/O errors/three copyrights PASS")


def main(image):
    with running_container(image, isolated=True) as api:
        pdfcpu_smoke(api)
    for setting in ("Pdf__PdfcpuPath=/missing/pdfcpu", "Pdf__PdfcpuConfigDir=/missing/config",
                    "Pdf__PdfcpuAddressSpaceLimitBytes=268435456"):
        assert_startup_failure(image, (setting,), "pdfcpu・日本語フォントの自己テストに失敗しました。")
    clean_smoke(image)
    compress_smoke(image)
    split_smoke(image)
    post_count = 0
    with running_container(image) as api:
        api.exec("test", "-r", "/usr/share/doc/util-linux/copyright")
        api.exec("prlimit", "--version")
        print(api.exec("/usr/bin/env", "--version").stdout.decode().splitlines()[0])
        # Observe the inherited limits and send SIGXFSZ after both execs.
        api.exec(*LIMITED_EXEC, "--as=67108864:67108864", "--fsize=4096:4096", "--", "/bin/sh", "-c",
                 "grep -Eq '^Max core file size +0 +0 +bytes' /proc/$$/limits && "
                 "grep -Eq '^Max address space +67108864 +67108864 +bytes' /proc/$$/limits && "
                 "grep -Eq '^Max file size +4096 +4096 +bytes' /proc/$$/limits && kill -XFSZ $$")
        version = api.exec("qpdf", "--version").stdout.decode().splitlines()[0]
        print(version)
        match = re.match(r"qpdf version (\d+)\.", version)
        assert match and int(match.group(1)) >= 12, "qpdf 12 or later is required: " + version
        for option in ("--remove-info", "--remove-metadata"):
            api.exec("qpdf", "--help=" + option)
        schema = json.loads(docker("exec", api.name, "qpdf", "--job-json-help").stdout)
        assert "256bit" in schema["encrypt"] and "passwordMode" in schema and "check" in schema and "isEncrypted" in schema
        assert "requiresPassword" in schema and "decrypt" in schema
        encrypted = api.protect(FIXTURE)
        api.verify_encryption(encrypted)
        api.verify_decryption(api.unlock(encrypted))
        api.unlock(encrypted, WRONG_PASSWORD, expected_reason="wrong-password")
        rotated = api.rotate(FIXTURE)
        assert api.page_rotations(rotated) == [90]
        assert api.page_rotations(api.rotate(rotated, pages="1")) == [180]
        extracted = api.extract(FIXTURE, "1")
        assert api.page_rotations(extracted) == [0]
        optimized = api.optimize(FIXTURE)
        assert api.page_rotations(optimized) == [0]
        assert api.page_rotations(api.merge([rotated, FIXTURE])) == [90, 0]
        api.protect(None, expected=400)
        api.protect(FIXTURE, password=None, expected=400)
        api.protect(b"", expected=422)
        api.protect(SENTINEL.encode(), expected=422)
        api.protect(b"%PDF-1.4\n" + SENTINEL.encode() + b"\n%%EOF", expected=422)
        api.protect(FIXTURE.replace(b"/Length 41", b"/Length 39"), expected=422)
        api.protect(encrypted, expected=422)
        print("Docker E2E: health/non-root/read-only/tmpfs/AES-256/unlock/optimization/merge/rotation/page selection/passwords/400/422/logs/cleanup PASS")
        post_count += api.post_count
    with running_container(image, (f"Pdf__MaxFileBytes={len(FIXTURE)}",)) as api:
        api.protect(FIXTURE)
        api.protect(FIXTURE + b"X", expected=413)
        api.protect(FIXTURE + b"X", expected=413, chunked=True)
        print("Docker E2E: exact file limit / Content-Length and chunked 413 PASS")
        post_count += api.post_count
    with request_failing_qpdf(image) as api:
        api.protect(FIXTURE, expected=500)
        print("Docker E2E: sanitized internal failure / cleanup PASS")
        post_count += api.post_count
    large = flate_image_pdf()
    with running_container(image, (f"Pdf__QpdfAddressSpaceLimitBytes={64 * 1024 * 1024}",), isolated=True) as api:
        api.protect(FIXTURE)
        problem = json.loads(api.protect(large, expected=422))
        assert problem["title"] == "このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。"
        assert "reason" not in problem
        post_count += api.post_count
    with running_container(image, isolated=True) as api:
        api.verify_encryption(api.protect(large))
        post_count += api.post_count
    for settings in (("Pdf__PrlimitPath=/missing/prlimit",), ("Pdf__QpdfPath=/missing/qpdf",),
                     ("Pdf__QpdfAddressSpaceLimitBytes=1",), ("Pdf__QpdfAddressSpaceLimitBytes=16777216",)):
        assert_startup_failure(image, settings)
    for settings in (("Pdf__QpdfAddressSpaceLimitBytes=0",), ("Pdf__QpdfJpegMemory=64MiB",), ("Pdf__PrlimitPath=",)):
        assert_startup_failure(image, settings, "PDF設定値が不正です。")
    print("Docker E2E: real prlimit / resource 422 / raised limit / startup self-test / util-linux copyright PASS")
    with running_container(image, isolated=True) as api:
        encrypted = api.protect(FIXTURE)
        api.verify_encryption(encrypted)
        api.verify_decryption(api.unlock(encrypted))
        api.unlock(encrypted, WRONG_PASSWORD, expected_reason="wrong-password")
        print("Docker E2E: no network / loopback HTTP / real encryption and unlock PASS")
        post_count += api.post_count
    print(f"Docker E2E: {post_count} POST checks PASS")


def clean_request(api, source, expected=200, cleanup=True):
    body, ct = multipart(source, password=None)
    code, headers, result = api.request("POST", "/api/pdf/clean", body, ct)
    assert code == expected, ("clean", code, expected)
    if expected == 200:
        assert headers["Content-Type"].split(";")[0] == "application/pdf"
        assert "filename=cleaned.pdf" in headers["Content-Disposition"]
        assert int(headers["Content-Length"]) == len(result)
    else:
        problem = json.loads(result)
        assert problem["status"] == expected
        assert problem.get("reason") == "too-complex"
        assert "Content-Disposition" not in headers
        for value in (b"/tmp/", SENTINEL.encode(), b"untrusted.pdf"):
            assert value not in result
    if cleanup:
        api.assert_clean()
    return result


def clean_smoke(image):
    with running_container(image, isolated=True) as api:
        root = "/tmp/clean-smoke"
        api.exec("mkdir", "-m", "700", root)
        source = Path(__file__).resolve().parent.parent / "tests/Amane.Pdf.Api.Tests/Fixtures/clean-source.json"
        docker("exec", "-i", api.name, "sh", "-c", "umask 077; cat > /tmp/clean-smoke/source.json", data=source.read_bytes())
        api.exec("qpdf", "--json-input", root + "/source.json", root + "/source.pdf")
        api.exec("qpdf", "--check", root + "/source.pdf")
        fixture = api.exec("cat", root + "/source.pdf").stdout
        result = clean_request(api, fixture)
        docker("exec", "-i", api.name, "sh", "-c", "umask 077; cat > /tmp/clean-smoke/output.pdf", data=result)
        api.exec("qpdf", "--check", root + "/output.pdf")
        api.exec("qpdf", "--qdf", "--object-streams=disable", "--decode-level=all", root + "/output.pdf", root + "/expanded.pdf")
        expanded = api.exec("cat", root + "/expanded.pdf").stdout
        for marker in (b"ANNOTATION_PAYLOAD_MARKER", b"NAMETREE_PAYLOAD_MARKER", b"RICHMEDIA_PAYLOAD_MARKER", b"GOTOE_PAYLOAD_MARKER",
                       b"RELATED_FILE_PAYLOAD_MARKER", b"ATTACHMENT_DESCRIPTION_MARKER", b"ATTACHMENT_AUTHOR_MARKER", b"DOCUMENT_XMP_MARKER",
                       b"AUTHOR_MARKER", b"PRODUCER_MARKER", b"PARENTLESS_POPUP_MARKER", b"PARENT_REFERENCED_POPUP_MARKER"):
            assert marker not in expanded, marker.decode()
        for marker in (b"RICHMEDIA_FILENAME_RETAINED", b"GOTOE_FILENAME_RETAINED", b"COMMENT_AUTHOR_RETAINED", b"/OBJR", b"PAGE_XMP_RETAINED"):
            assert marker in expanded, marker.decode()
        assert api.exec("qpdf", "--show-npages", root + "/output.pdf").stdout == b"1\n"
        api.exec("rm", "-rf", root)
        print("Docker clean: RichMedia/GoToE/EF/RF/attachments/authors/both Popup directions/expanded markers/cleanup PASS", flush=True)
    for setting in ("Pdf__CleanJsonLimitBytes=128", "Pdf__CleanOutputLimitBytes=128"):
        with running_container(image, (setting,), isolated=True) as api:
            clean_request(api, FIXTURE, expected=422)
    for setting in ("Pdf__CleanJsonLimitBytes=0", "Pdf__CleanOutputLimitBytes=0", "Pdf__CleanJobLimitBytes=1"):
        assert_startup_failure(image, (setting,), "PDF設定値が不正です。")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "amane-pdf-api:ci")
